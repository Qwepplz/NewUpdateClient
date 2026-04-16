using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UpdateClient.Config;
using UpdateClient.ConsoleUi;
using UpdateClient.FileSystem;
using UpdateClient.Logging;
using UpdateClient.Remote;
using UpdateClient.Remote.Models;
using UpdateClient.Security;
using UpdateClient.State;

namespace UpdateClient.Sync
{
    internal interface IRepositorySynchronizer
    {
        SyncSummary Synchronize(RepositoryTarget target, RepositoryTreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string targetHash, string tempRootDirectoryPath, LogSession activeLog);
    }

    internal sealed class RepositorySynchronizer : IRepositorySynchronizer
    {
        private readonly IRemoteRepositoryClient remoteRepositoryClient;
        private readonly ISafePathService safePathService;
        private readonly IAtomicFileWriter atomicFileWriter;
        private readonly ISyncStateStore syncStateStore;
        private readonly IGitBlobHasher gitBlobHasher;

        public RepositorySynchronizer(
            IRemoteRepositoryClient remoteRepositoryClient,
            ISafePathService safePathService,
            IAtomicFileWriter atomicFileWriter,
            ISyncStateStore syncStateStore,
            IGitBlobHasher gitBlobHasher)
        {
            if (remoteRepositoryClient == null) throw new ArgumentNullException(nameof(remoteRepositoryClient));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            if (atomicFileWriter == null) throw new ArgumentNullException(nameof(atomicFileWriter));
            if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            this.remoteRepositoryClient = remoteRepositoryClient;
            this.safePathService = safePathService;
            this.atomicFileWriter = atomicFileWriter;
            this.syncStateStore = syncStateStore;
            this.gitBlobHasher = gitBlobHasher;
        }

        public SyncSummary Synchronize(RepositoryTarget target, RepositoryTreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string targetHash, string tempRootDirectoryPath, LogSession activeLog)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));
            if (treeResult.Tree == null) throw new InvalidOperationException("Repository tree is not available.");
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(targetHash)) throw new ArgumentException("Value cannot be empty.", nameof(targetHash));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            SyncSummary summary = new SyncSummary();

            HashSet<string> protectedPaths = this.safePathService.BuildProtectedPathSet(targetDirectoryPath);
            summary.StaleArtifactsRemoved = this.safePathService.RemoveStaleUpdaterArtifacts(targetDirectoryPath, protectedPaths);
            if (summary.StaleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", summary.StaleArtifactsRemoved));
            }

            string stateDirectoryPath = this.syncStateStore.GetStateDirectory(targetDirectoryPath, targetHash);
            string manifestPath = this.syncStateStore.GetLegacyManifestPath(stateDirectoryPath);

            Console.WriteLine("[1/4] Repository access ready...");
            summary.Branch = treeResult.Branch;
            summary.Source = treeResult.Source;
            Console.WriteLine(string.Format("       Branch: {0}", summary.Branch));
            Console.WriteLine(string.Format("       Source: {0}", summary.Source));

            ImportedSyncState importedState = this.syncStateStore.Import(stateDirectoryPath);
            if (importedState.CacheUnreadable)
            {
                Console.WriteLine("       Sync cache unreadable. Rebuilding...");
            }

            Dictionary<string, CachedFileState> cachedFiles = importedState.Files;
            Dictionary<string, CachedFileState> newCachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TreeEntry> remoteFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TreeEntry> excludedFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (TreeEntry entry in treeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = this.safePathService.NormalizeRelativePath(entry.path);
                if (IsExcludedRootFile(relativePath))
                {
                    excludedFiles[relativePath] = entry;
                }
                else
                {
                    remoteFiles[relativePath] = entry;
                }
            }

            Console.WriteLine("[2/4] Removing repo README/LICENSE when safe...");
            foreach (string relativePath in SortKeys(excludedFiles.Keys))
            {
                TreeEntry entry = excludedFiles[relativePath];
                string destinationPath = this.safePathService.GetTargetPathFromRelative(targetDirectoryPath, relativePath);
                string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFullPath) || !File.Exists(destinationPath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(targetDirectoryPath, destinationPath);

                bool matchesRemote = this.syncStateStore.MatchesCachedRemote(relativePath, destinationPath, entry, cachedFiles);
                if (!matchesRemote)
                {
                    matchesRemote = this.gitBlobHasher.MatchesRemoteBlob(destinationPath, entry);
                }

                if (matchesRemote)
                {
                    File.Delete(destinationPath);
                    this.safePathService.RemoveEmptyParentDirectories(destinationPath, targetDirectoryPath);
                    WriteLogOnlyLine(activeLog, "Removed README/LICENSE file: " + relativePath);
                }
                else
                {
                    WriteLogOnlyLine(activeLog, "Kept local README/LICENSE file: " + relativePath);
                }
            }

            Console.WriteLine("[3/4] Downloading and updating files...");
            List<string> newManifest = new List<string>();
            List<string> sortedRemoteFiles = SortKeys(remoteFiles.Keys);
            TextWriter progressWriter = activeLog == null ? Console.Out : activeLog.ConsoleWriter;

            using (ProgressDisplay progress = new ProgressDisplay(progressWriter, ProgressDisplay.CanRefresh()))
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = remoteFiles[relativePath];
                    string destinationPath = this.safePathService.GetTargetPathFromRelative(targetDirectoryPath, relativePath);
                    string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", summary.Added, summary.Updated, summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (protectedPaths.Contains(destinationFullPath))
                    {
                        WriteLogOnlyLine(activeLog, "Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    newManifest.Add(relativePath);
                    this.safePathService.AssertSafeManagedPath(targetDirectoryPath, destinationPath);
                    this.safePathService.AssertNoDirectoryConflict(destinationPath);

                    if (this.syncStateStore.MatchesCachedRemote(relativePath, destinationPath, entry, cachedFiles))
                    {
                        newCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        summary.Unchanged++;
                        WriteLogOnlyLine(activeLog, "Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && this.gitBlobHasher.MatchesRemoteBlob(destinationPath, entry))
                    {
                        newCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        summary.Unchanged++;
                        WriteLogOnlyLine(activeLog, "Verified match: " + relativePath);
                        continue;
                    }

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", summary.Added, summary.Updated, summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    this.DownloadRemoteFile(target, treeResult.Branch, entry, tempRootDirectoryPath, destinationPath, remoteKind);
                    newCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);

                    if (existed)
                    {
                        summary.Updated++;
                        WriteLogOnlyLine(activeLog, "Updated: " + relativePath);
                    }
                    else
                    {
                        summary.Added++;
                        WriteLogOnlyLine(activeLog, "Added: " + relativePath);
                    }
                }

                progress.Complete(
                    FormatProgressStatus("[3/4] Files", sortedRemoteFiles.Count, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2}", summary.Added, summary.Updated, summary.Unchanged)),
                    FormatProgressBarLine(sortedRemoteFiles.Count, sortedRemoteFiles.Count));
            }

            Console.WriteLine("[4/4] Removing files deleted upstream...");
            List<string> oldManifest = new List<string>(importedState.TrackedFiles);
            if (oldManifest.Count == 0 && File.Exists(manifestPath))
            {
                oldManifest = File.ReadAllLines(manifestPath)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(this.safePathService.NormalizeRelativePath)
                    .ToList();
            }

            HashSet<string> remoteSet = new HashSet<string>(newManifest, StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in oldManifest)
            {
                if (remoteSet.Contains(relativePath))
                {
                    continue;
                }

                string destinationPath = this.safePathService.GetTargetPathFromRelative(targetDirectoryPath, relativePath);
                string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFullPath) || !File.Exists(destinationPath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(targetDirectoryPath, destinationPath);
                File.Delete(destinationPath);
                this.safePathService.RemoveEmptyParentDirectories(destinationPath, targetDirectoryPath);
                summary.Removed++;
                WriteLogOnlyLine(activeLog, "Removed upstream-deleted file: " + relativePath);
            }

            List<string> sortedManifest = newManifest.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(manifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            this.syncStateStore.Export(stateDirectoryPath, sortedManifest, newCachedFiles);
            return summary;
        }

        private void DownloadRemoteFile(RepositoryTarget target, string branch, TreeEntry entry, string tempRootDirectoryPath, string destinationPath, RepositoryRemoteKind remoteKind)
        {
            string tempFilePath = this.remoteRepositoryClient.DownloadVerifiedFileToTemporaryPath(target, branch, entry, tempRootDirectoryPath, remoteKind);
            try
            {
                this.atomicFileWriter.WriteFileAtomically(tempFilePath, destinationPath);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void WriteLogOnlyLine(LogSession activeLog, string message)
        {
            if (activeLog == null)
            {
                return;
            }

            try
            {
                activeLog.WriteLogOnlyLine(message);
            }
            catch
            {
            }
        }

        private static List<string> SortKeys(IEnumerable<string> keys)
        {
            return keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsExcludedRootFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            string normalizedPath = relativePath.Trim('/');
            if (normalizedPath.IndexOf('/') >= 0)
            {
                return false;
            }

            return normalizedPath.StartsWith("README", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith("LECENSE", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatProgressStatus(string stageLabel, int current, int total, string metrics)
        {
            return string.Format("{0} {1}/{2} | {3}", stageLabel, Math.Max(0, current), Math.Max(0, total), metrics);
        }

        private static string FormatProgressBarLine(int current, int total)
        {
            int safeCurrent = Math.Max(0, current);
            int safeTotal = Math.Max(0, total);
            string suffix = string.Format(" {0}/{1}", safeCurrent, safeTotal);
            int width = GetProgressBarWidth(suffix.Length);
            return BuildProgressBar(safeCurrent, safeTotal, width) + suffix;
        }

        private static string BuildProgressBar(int current, int total, int width)
        {
            int safeWidth = Math.Max(8, width);
            int safeCurrent = Math.Max(0, current);
            int safeTotal = Math.Max(0, total);
            int filled = safeTotal <= 0
                ? safeWidth
                : Math.Min(safeWidth, (int)Math.Round((double)Math.Min(safeCurrent, safeTotal) * safeWidth / safeTotal, MidpointRounding.AwayFromZero));

            return "[" + new string('#', filled) + new string('-', safeWidth - filled) + "]";
        }

        private static int GetProgressBarWidth(int suffixLength)
        {
            try
            {
                int lineWidth = Math.Max(20, Console.BufferWidth - 1);
                return Math.Max(8, lineWidth - suffixLength - 2);
            }
            catch
            {
                return 48;
            }
        }
    }
}
