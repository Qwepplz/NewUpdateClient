using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpdateClient.Config;
using UpdateClient.FileSystem;
using UpdateClient.Remote.Models;

namespace UpdateClient.State
{
    internal interface ISyncStateStore
    {
        string GetTargetHash(string targetDirectoryPath);

        string GetStateDirectory(string targetDirectoryPath, string targetHash, RepositoryTarget target);

        string GetStateFilePath(string stateDirectoryPath);

        string GetLegacyManifestPath(string stateDirectoryPath);

        ImportedSyncState Import(string stateDirectoryPath);

        void Export(string stateDirectoryPath, IEnumerable<string> trackedFiles, IDictionary<string, CachedFileState> files);

        CachedFileState CreateLocalFileState(string relativePath, string filePath, string remoteSha);

        bool MatchesCachedRemote(string relativePath, string filePath, TreeEntry entry, IDictionary<string, CachedFileState> cachedFiles);
    }

    internal sealed class SyncStateStore : ISyncStateStore
    {
        private readonly ISafePathService safePathService;

        public SyncStateStore(ISafePathService safePathService)
        {
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            this.safePathService = safePathService;
        }

        public string GetTargetHash(string targetDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));

            using (SHA256 sha256 = SHA256.Create())
            {
                string fullPath = this.safePathService.GetFullPath(targetDirectoryPath);
                string hashPath = OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : PathRules.NormalizeDirectoryPath(fullPath);
                byte[] bytes = Encoding.UTF8.GetBytes(hashPath);
                byte[] hash = sha256.ComputeHash(bytes);
                return ToHexString(hash);
            }
        }

        public string GetStateDirectory(string targetDirectoryPath, string targetHash, RepositoryTarget target)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(targetHash)) throw new ArgumentException("Value cannot be empty.", nameof(targetHash));
            if (target == null) throw new ArgumentNullException(nameof(target));

            bool isGet5 = string.Equals(target.StateKey, "get5", StringComparison.OrdinalIgnoreCase);
            List<string> baseCandidates = this.BuildBaseCandidates();
            if (OperatingSystem.IsWindows())
            foreach (string baseCandidate in baseCandidates)
            {
                string existingStateDirectoryPath = Path.Combine(baseCandidate, targetHash);
                if (Directory.Exists(existingStateDirectoryPath))
                {
                    if (!isGet5)
                    {
                        return existingStateDirectoryPath;
                    }

                    string get5StatePath = Path.Combine(existingStateDirectoryPath, "get5");
                    Directory.CreateDirectory(get5StatePath);
                    return get5StatePath;
                }
            }

            Exception lastError = null;
            foreach (string baseCandidate in baseCandidates)
            {
                try
                {
                    Directory.CreateDirectory(baseCandidate);
                    string stateDirectoryPath = Path.Combine(baseCandidate, targetHash);
                    Directory.CreateDirectory(stateDirectoryPath);
                    if (isGet5)
                    {
                        stateDirectoryPath = Path.Combine(stateDirectoryPath, "get5");
                        Directory.CreateDirectory(stateDirectoryPath);
                    }
                    return stateDirectoryPath;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            throw new InvalidOperationException(
                string.Format("Cannot create sync state directory. Last error: {0}", lastError == null ? "Unknown error" : lastError.Message));
        }

        public string GetStateFilePath(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            return Path.Combine(stateDirectoryPath, AppOptions.SyncStateFileName);
        }

        public string GetLegacyManifestPath(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            return Path.Combine(stateDirectoryPath, AppOptions.LegacyManifestFileName);
        }

        public ImportedSyncState Import(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));

            ImportedSyncState importedState = new ImportedSyncState();
            string statePath = this.GetStateFilePath(stateDirectoryPath);
            string legacyManifestPath = this.GetLegacyManifestPath(stateDirectoryPath);

            if (File.Exists(statePath))
            {
                try
                {
                    JsonSerializerOptions jsonOptions = CreateJsonOptions();
                    string raw = File.ReadAllText(statePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        SyncState state = JsonSerializer.Deserialize<SyncState>(raw, jsonOptions);
                        if (state != null)
                        {
                            if (state.tracked_files != null)
                            {
                                importedState.TrackedFiles.AddRange(state.tracked_files.Where(item => !string.IsNullOrWhiteSpace(item)));
                            }

                            if (state.files != null)
                            {
                                foreach (CachedFileState file in state.files)
                                {
                                    if (file == null || string.IsNullOrWhiteSpace(file.path))
                                    {
                                        continue;
                                    }

                                    importedState.Files[this.safePathService.NormalizeRelativePath(file.path)] = file;
                                }
                            }

                            return importedState;
                        }
                    }
                }
                catch
                {
                    importedState.CacheUnreadable = true;
                }
            }

            if (File.Exists(legacyManifestPath))
            {
                importedState.TrackedFiles.AddRange(this.ReadManifest(legacyManifestPath));
            }

            return importedState;
        }

        public void Export(string stateDirectoryPath, IEnumerable<string> trackedFiles, IDictionary<string, CachedFileState> files)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(stateDirectoryPath));
            if (trackedFiles == null) throw new ArgumentNullException(nameof(trackedFiles));
            if (files == null) throw new ArgumentNullException(nameof(files));

            Directory.CreateDirectory(stateDirectoryPath);
            List<CachedFileState> fileStates = new List<CachedFileState>();
            foreach (string path in files.Keys.OrderBy(item => item, PathRules.Comparer))
            {
                CachedFileState fileState = files[path];
                fileState.path = this.safePathService.NormalizeRelativePath(path);
                fileStates.Add(fileState);
            }

            SyncState state = new SyncState
            {
                version = AppOptions.SyncStateVersion,
                tracked_files = trackedFiles.Where(item => !string.IsNullOrWhiteSpace(item)).OrderBy(item => item, PathRules.Comparer).ToList(),
                files = fileStates
            };

            JsonSerializerOptions jsonOptions = CreateJsonOptions();
            string json = JsonSerializer.Serialize(state, jsonOptions);
            File.WriteAllText(this.GetStateFilePath(stateDirectoryPath), json, new UTF8Encoding(false));
        }

        public CachedFileState CreateLocalFileState(string relativePath, string filePath, string remoteSha)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be empty.", nameof(filePath));

            FileInfo fileInfo = new FileInfo(filePath);
            return new CachedFileState
            {
                path = this.safePathService.NormalizeRelativePath(relativePath),
                remote_sha = remoteSha,
                length = fileInfo.Length,
                last_write_utc_ticks = fileInfo.LastWriteTimeUtc.Ticks
            };
        }

        public bool MatchesCachedRemote(string relativePath, string filePath, TreeEntry entry, IDictionary<string, CachedFileState> cachedFiles)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be empty.", nameof(filePath));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (cachedFiles == null) throw new ArgumentNullException(nameof(cachedFiles));
            if (!File.Exists(filePath))
            {
                return false;
            }

            CachedFileState cached;
            if (!cachedFiles.TryGetValue(relativePath, out cached) || cached == null)
            {
                return false;
            }

            if (!string.Equals(cached.remote_sha, entry.sha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo fileInfo = new FileInfo(filePath);
            return cached.length == fileInfo.Length && cached.last_write_utc_ticks == fileInfo.LastWriteTimeUtc.Ticks;
        }

        private List<string> BuildBaseCandidates()
        {
            List<string> baseCandidates = new List<string>();

            foreach (string environmentVariableName in AppOptions.StateRootEnvironmentVariables)
            {
                string environmentValue = Environment.GetEnvironmentVariable(environmentVariableName);
                if (!string.IsNullOrWhiteSpace(environmentValue))
                {
                    baseCandidates.Add(environmentValue);
                }
            }

            if (OperatingSystem.IsWindows())
            {
                this.AppendStateRootCandidates(baseCandidates, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
                this.AppendStateRootCandidates(baseCandidates, Environment.GetEnvironmentVariable("APPDATA"));
                this.AppendStateRootCandidates(baseCandidates, Path.GetTempPath());
            }
            else
            {
                baseCandidates.AddRange(GetLinuxBaseCandidates(Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath()));
            }

            return baseCandidates
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(PathRules.Comparer)
                .ToList();
        }

        internal static IReadOnlyList<string> GetLinuxBaseCandidates(string xdgStateHome, string userProfilePath, string tempPath)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(xdgStateHome) && Path.IsPathFullyQualified(xdgStateHome))
                candidates.Add(Path.Combine(xdgStateHome, "UpdateClientSync"));
            if (!string.IsNullOrWhiteSpace(userProfilePath) && Path.IsPathFullyQualified(userProfilePath))
                candidates.Add(Path.Combine(userProfilePath, ".local", "state", "UpdateClientSync"));
            if (!string.IsNullOrWhiteSpace(tempPath))
                candidates.Add(Path.Combine(tempPath, "UpdateClientSync"));
            return candidates;
        }

        private void AppendStateRootCandidates(ICollection<string> candidates, string basePath)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return;
            }

            foreach (string rootDirectoryName in AppOptions.StateRootDirectoryNames)
            {
                candidates.Add(Path.Combine(basePath, rootDirectoryName));
            }
        }

        private List<string> ReadManifest(string manifestPath)
        {
            return File.ReadAllLines(manifestPath)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(this.safePathService.NormalizeRelativePath)
                .ToList();
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 256,
                WriteIndented = true
            };
        }

        private static string ToHexString(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
