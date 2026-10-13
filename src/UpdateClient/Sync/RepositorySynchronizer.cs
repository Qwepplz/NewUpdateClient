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

        private sealed class SynchronizationContext
        {
            internal RepositoryTarget Target { get; set; }
            internal RepositoryTreeResult TreeResult { get; set; }
            internal RepositoryRemoteKind RemoteKind { get; set; }
            internal string TargetDirectoryPath { get; set; }
            internal string TargetHash { get; set; }
            internal string TempRootDirectoryPath { get; set; }
            internal LogSession ActiveLog { get; set; }
            internal SyncSummary Summary { get; private set; }
            internal HashSet<string> ProtectedPaths { get; set; }
            internal string StateDirectoryPath { get; set; }
            internal string ManifestPath { get; set; }
            internal ImportedSyncState ImportedState { get; set; }
            internal Dictionary<string, CachedFileState> CachedFiles { get; set; }
            internal Dictionary<string, CachedFileState> NewCachedFiles { get; set; }
            internal Dictionary<string, TreeEntry> RemoteFiles { get; set; }
            internal List<string> NewManifest { get; set; }
            internal TextWriter ProgressWriter { get; set; }

            internal SynchronizationContext()
            {
                this.Summary = new SyncSummary();
            }
        }

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
            SynchronizationContext context = this.CreateContext(target, treeResult, remoteKind, targetDirectoryPath, targetHash, tempRootDirectoryPath, activeLog);

            RepositoryTreeValidator.Validate(context.TreeResult.Tree);
            this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, context.TargetDirectoryPath);
            this.PrepareWorkingState(context);
            PrintRepositoryAccessReady(context);
            this.ClassifyRemoteEntries(context);
            this.RemoveExcludedRootFilesWhenSafe(context);
            this.DownloadAndUpdateFiles(context);
            this.RemoveUnexpectedStrictFiles(context);
            this.PersistState(context);

            return context.Summary;
        }

        private SynchronizationContext CreateContext(RepositoryTarget target, RepositoryTreeResult treeResult, RepositoryRemoteKind remoteKind, string targetDirectoryPath, string targetHash, string tempRootDirectoryPath, LogSession activeLog)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));
            if (treeResult.Tree == null) throw new InvalidOperationException("Repository tree is not available.");
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(targetHash)) throw new ArgumentException("Value cannot be empty.", nameof(targetHash));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            return new SynchronizationContext
            {
                Target = target,
                TreeResult = treeResult,
                RemoteKind = remoteKind,
                TargetDirectoryPath = targetDirectoryPath,
                TargetHash = targetHash,
                TempRootDirectoryPath = tempRootDirectoryPath,
                ActiveLog = activeLog
            };
        }

        private void PrepareWorkingState(SynchronizationContext context)
        {
            context.ProtectedPaths = this.safePathService.BuildProtectedPathSet(context.TargetDirectoryPath);
            context.Summary.StaleArtifactsRemoved = this.safePathService.RemoveStaleUpdaterArtifacts(context.TargetDirectoryPath, context.ProtectedPaths);
            if (context.Summary.StaleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", context.Summary.StaleArtifactsRemoved));
            }

            context.StateDirectoryPath = this.syncStateStore.GetStateDirectory(context.TargetDirectoryPath, context.TargetHash, context.Target);
            context.ManifestPath = this.syncStateStore.GetLegacyManifestPath(context.StateDirectoryPath);
        }

        private static void PrintRepositoryAccessReady(SynchronizationContext context)
        {
            Console.WriteLine("[1/4] Repository access ready...");
            context.Summary.Branch = context.TreeResult.Branch;
            context.Summary.Source = context.TreeResult.Source;
            Console.WriteLine(string.Format("       Branch: {0}", context.Summary.Branch));
            Console.WriteLine(string.Format("       Source: {0}", context.Summary.Source));
        }

        private void ClassifyRemoteEntries(SynchronizationContext context)
        {
            context.ImportedState = this.syncStateStore.Import(context.StateDirectoryPath);
            if (context.ImportedState.CacheUnreadable)
            {
                Console.WriteLine("       Sync cache unreadable. Rebuilding...");
            }

            context.CachedFiles = context.ImportedState.Files;
            context.NewCachedFiles = new Dictionary<string, CachedFileState>(PathRules.Comparer);
            context.RemoteFiles = new Dictionary<string, TreeEntry>(PathRules.Comparer);

            foreach (TreeEntry entry in context.TreeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = this.safePathService.NormalizeRelativePath(entry.path);
                if (!SyncPolicy.IsExcludedRootFile(relativePath))
                {
                    context.RemoteFiles[relativePath] = entry;
                }
            }
        }

        private void RemoveExcludedRootFilesWhenSafe(SynchronizationContext context)
        {
            Console.WriteLine("[2/4] Removing excluded root files...");
            foreach (string filePath in Directory.EnumerateFiles(context.TargetDirectoryPath))
            {
                string relativePath = Path.GetFileName(filePath);
                if (!SyncPolicy.IsExcludedRootFile(relativePath))
                {
                    continue;
                }

                string fullPath = this.safePathService.GetFullPath(filePath);
                if (context.ProtectedPaths.Contains(fullPath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, filePath);
                File.Delete(filePath);
                WriteLogOnlyLine(context.ActiveLog, "Removed excluded root file: " + relativePath);
            }
        }

        private void DownloadAndUpdateFiles(SynchronizationContext context)
        {
            Console.WriteLine("[3/4] Downloading and updating files...");
            context.NewManifest = new List<string>();
            List<string> sortedRemoteFiles = SortKeys(context.RemoteFiles.Keys);
            context.ProgressWriter = context.ActiveLog == null ? Console.Out : context.ActiveLog.ConsoleWriter;

            using (ProgressDisplay progress = new ProgressDisplay(context.ProgressWriter, ProgressDisplay.CanRefresh()))
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = context.RemoteFiles[relativePath];
                    string destinationPath = this.safePathService.GetTargetPathFromRelative(context.TargetDirectoryPath, relativePath);
                    string destinationFullPath = this.safePathService.GetFullPath(destinationPath);

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (context.ProtectedPaths.Contains(destinationFullPath))
                    {
                        WriteLogOnlyLine(context.ActiveLog, "Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    context.NewManifest.Add(relativePath);
                    this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, destinationPath);
                    this.safePathService.AssertNoDirectoryConflict(destinationPath);

                    if (this.syncStateStore.MatchesCachedRemote(relativePath, destinationPath, entry, context.CachedFiles))
                    {
                        context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        context.Summary.Unchanged++;
                        WriteLogOnlyLine(context.ActiveLog, "Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && this.gitBlobHasher.MatchesRemoteBlob(destinationPath, entry))
                    {
                        context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);
                        context.Summary.Unchanged++;
                        WriteLogOnlyLine(context.ActiveLog, "Verified match: " + relativePath);
                        continue;
                    }

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    this.DownloadRemoteFile(context.Target, context.TreeResult.Branch, entry, context.TempRootDirectoryPath, destinationPath, context.RemoteKind);
                    context.NewCachedFiles[relativePath] = this.syncStateStore.CreateLocalFileState(relativePath, destinationPath, entry.sha);

                    if (existed)
                    {
                        context.Summary.Updated++;
                        WriteLogOnlyLine(context.ActiveLog, "Updated: " + relativePath);
                    }
                    else
                    {
                        context.Summary.Added++;
                        WriteLogOnlyLine(context.ActiveLog, "Added: " + relativePath);
                    }
                }

                progress.Complete(
                    FormatProgressStatus("[3/4] Files", sortedRemoteFiles.Count, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2}", context.Summary.Added, context.Summary.Updated, context.Summary.Unchanged)),
                    FormatProgressBarLine(sortedRemoteFiles.Count, sortedRemoteFiles.Count));
            }
        }

        private void RemoveUnexpectedStrictFiles(SynchronizationContext context)
        {
            if (context.Target.StrictDirectories.Count == 0)
            {
                return;
            }

            Console.WriteLine("[4/4] Removing unexpected files from strict directories...");
            HashSet<string> remoteSet = new HashSet<string>(context.NewManifest, PathRules.Comparer);
            foreach (string strictDirectory in context.Target.StrictDirectories)
            {
                this.RemoveUnexpectedFilesInDirectory(context, strictDirectory, remoteSet);
            }
        }

        private void RemoveUnexpectedFilesInDirectory(SynchronizationContext context, string strictDirectory, ISet<string> remoteSet)
        {
            string normalizedDirectory = this.safePathService.NormalizeRelativePath(strictDirectory).Trim('/');
            string directoryPath = this.safePathService.GetTargetPathFromRelative(context.TargetDirectoryPath, normalizedDirectory);
            this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, directoryPath);
            if (File.Exists(directoryPath))
            {
                throw new InvalidOperationException("Strict directory path is a file: " + directoryPath);
            }
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            List<string> filePaths = new List<string>();
            List<string> directoryPaths = new List<string>();
            CollectStrictFileSystemEntries(directoryPath, filePaths, directoryPaths);

            foreach (string filePath in SortKeys(filePaths))
            {
                string destinationFullPath = this.safePathService.GetFullPath(filePath);
                if (context.ProtectedPaths.Contains(destinationFullPath) || !File.Exists(filePath))
                {
                    continue;
                }

                this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, filePath);
                string relativePath = this.GetRelativePathFromTarget(context.TargetDirectoryPath, filePath);
                if (!IsWithinStrictDirectory(relativePath, normalizedDirectory) || remoteSet.Contains(relativePath))
                {
                    continue;
                }

                File.Delete(filePath);
                context.Summary.Removed++;
                WriteLogOnlyLine(context.ActiveLog, "Removed unexpected strict file: " + relativePath);
            }

            this.RemoveEmptyStrictDirectories(context, directoryPath, directoryPaths);
        }

        private static void CollectStrictFileSystemEntries(string rootDirectoryPath, List<string> filePaths, List<string> directoryPaths)
        {
            Stack<string> pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootDirectoryPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectoryPath = pendingDirectories.Pop();

                foreach (string filePath in Directory.EnumerateFiles(currentDirectoryPath))
                {
                    filePaths.Add(filePath);
                }

                foreach (string subDirectoryPath in Directory.EnumerateDirectories(currentDirectoryPath))
                {
                    if ((File.GetAttributes(subDirectoryPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Refusing to traverse a reparse point path: " + subDirectoryPath);
                    }

                    directoryPaths.Add(subDirectoryPath);
                    pendingDirectories.Push(subDirectoryPath);
                }
            }
        }

        private void RemoveEmptyStrictDirectories(SynchronizationContext context, string strictDirectoryPath, IEnumerable<string> directoryPaths)
        {
            foreach (string directoryPath in directoryPaths.OrderByDescending(item => item.Length))
            {
                this.RemoveEmptyStrictDirectoryIfEmpty(context, directoryPath);
            }

            this.RemoveEmptyStrictDirectoryIfEmpty(context, strictDirectoryPath);
        }

        private void RemoveEmptyStrictDirectoryIfEmpty(SynchronizationContext context, string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            this.safePathService.AssertSafeManagedPath(context.TargetDirectoryPath, directoryPath);
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, false);
            }
        }

        private string GetRelativePathFromTarget(string targetDirectoryPath, string path)
        {
            string targetRoot = PathRules.NormalizeDirectoryPath(targetDirectoryPath);
            string fullPath = this.safePathService.GetFullPath(path);
            string targetPrefix = Path.EndsInDirectorySeparator(targetRoot) ? targetRoot : targetRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(targetPrefix, PathRules.Comparison))
            {
                throw new InvalidOperationException("Refusing to touch a path outside the target folder: " + fullPath);
            }

            return this.safePathService.NormalizeRelativePath(fullPath.Substring(targetPrefix.Length));
        }

        private void PersistState(SynchronizationContext context)
        {
            List<string> sortedManifest = context.NewManifest.OrderBy(item => item, PathRules.Comparer).ToList();
            File.WriteAllLines(context.ManifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            this.syncStateStore.Export(context.StateDirectoryPath, sortedManifest, context.NewCachedFiles);
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
            return keys.OrderBy(item => item, PathRules.Comparer).ToList();
        }

        private static bool IsWithinStrictDirectory(string relativePath, string strictDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(strictDirectory))
            {
                return false;
            }

            return relativePath.StartsWith(strictDirectory.Trim('/') + "/", PathRules.Comparison);
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
