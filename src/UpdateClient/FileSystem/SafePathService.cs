using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UpdateClient.Config;

namespace UpdateClient.FileSystem
{
    internal interface ISafePathService
    {
        string GetFullPath(string path);

        string NormalizeRelativePath(string path);

        string GetTargetPathFromRelative(string targetDirectoryPath, string relativePath);

        string GetExecutablePath();

        string GetLogDirectoryPath(string targetDirectoryPath);

        HashSet<string> BuildProtectedPathSet(string targetDirectoryPath);

        int RemoveStaleUpdaterArtifacts(string targetDirectoryPath, ISet<string> protectedPaths);

        void AssertNoDirectoryConflict(string path);

        IEnumerable<string> EnumerateFilesSafely(string rootDirectoryPath);

        void AssertSafeManagedPath(string targetDirectoryPath, string path);

        bool IsPathWithinTarget(string targetRoot, string fullPath);

        void RemoveEmptyParentDirectories(string filePath, string stopAtDirectoryPath);
    }

    internal sealed class SafePathService : ISafePathService
    {
        private static string GetValidatedFullPath(string path, string paramName)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be empty.", paramName);
            return Path.GetFullPath(path);
        }

        public string GetFullPath(string path)
        {
            return GetValidatedFullPath(path, nameof(path));
        }

        public string NormalizeRelativePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        public string GetTargetPathFromRelative(string targetDirectoryPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));

            string windowsRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(targetDirectoryPath, windowsRelativePath);
        }

        public string GetExecutablePath()
        {
            try
            {
                Assembly entryAssembly = Assembly.GetEntryAssembly();
                if (entryAssembly == null || string.IsNullOrWhiteSpace(entryAssembly.Location))
                {
                    return string.Empty;
                }

                return this.GetFullPath(entryAssembly.Location);
            }
            catch
            {
                return string.Empty;
            }
        }

        public string GetLogDirectoryPath(string targetDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            return this.GetFullPath(Path.Combine(targetDirectoryPath, AppOptions.LogDirectoryName));
        }

        public HashSet<string> BuildProtectedPathSet(string targetDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));

            HashSet<string> protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string executablePath = this.GetExecutablePath();
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                protectedPaths.Add(this.GetFullPath(executablePath));
            }

            foreach (string helperFileName in AppOptions.ProtectedHelperFileNames)
            {
                string helperPath = this.GetFullPath(Path.Combine(targetDirectoryPath, helperFileName));
                if (File.Exists(helperPath))
                {
                    protectedPaths.Add(helperPath);
                }
            }

            string logDirectoryPath = this.GetLogDirectoryPath(targetDirectoryPath);
            try
            {
                if (Directory.Exists(logDirectoryPath) && (File.GetAttributes(logDirectoryPath) & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (string logFilePath in this.EnumerateFilesSafely(logDirectoryPath))
                    {
                        protectedPaths.Add(this.GetFullPath(logFilePath));
                    }
                }
            }
            catch
            {
            }

            return protectedPaths;
        }

        public void AssertNoDirectoryConflict(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be empty.", nameof(path));
            if (Directory.Exists(path))
            {
                throw new InvalidOperationException("Cannot place file because a directory exists at: " + path);
            }
        }

        public int RemoveStaleUpdaterArtifacts(string targetDirectoryPath, ISet<string> protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (protectedPaths == null) throw new ArgumentNullException(nameof(protectedPaths));

            List<string> artifactPaths = new List<string>();
            foreach (string path in this.EnumerateFilesSafely(targetDirectoryPath))
            {
                string fileName = Path.GetFileName(path);
                if (AppOptions.ArtifactMarkers.Any(marker => fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    artifactPaths.Add(path);
                }
            }

            int removedCount = 0;
            foreach (string artifactPath in artifactPaths.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                string fullPath = this.GetFullPath(artifactPath);
                if (protectedPaths.Contains(fullPath))
                {
                    continue;
                }

                this.AssertSafeManagedPath(targetDirectoryPath, fullPath);
                File.Delete(fullPath);
                this.RemoveEmptyParentDirectories(fullPath, targetDirectoryPath);
                removedCount++;
            }

            return removedCount;
        }

        public void RemoveEmptyParentDirectories(string filePath, string stopAtDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be empty.", nameof(filePath));

            string stopFullPath = GetValidatedFullPath(stopAtDirectoryPath, nameof(stopAtDirectoryPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentDirectoryPath = Path.GetDirectoryName(filePath);

            while (!string.IsNullOrEmpty(currentDirectoryPath))
            {
                string currentFullPath = this.GetFullPath(currentDirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (currentFullPath.Length <= stopFullPath.Length)
                {
                    break;
                }

                if (Directory.EnumerateFileSystemEntries(currentFullPath).Any())
                {
                    break;
                }

                Directory.Delete(currentFullPath, false);
                currentDirectoryPath = Path.GetDirectoryName(currentFullPath);
            }
        }

        public IEnumerable<string> EnumerateFilesSafely(string rootDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(rootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(rootDirectoryPath));

            Stack<string> pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootDirectoryPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectoryPath = pendingDirectories.Pop();

                IEnumerable<string> files = Enumerable.Empty<string>();
                try
                {
                    files = Directory.EnumerateFiles(currentDirectoryPath);
                }
                catch
                {
                }

                foreach (string filePath in files)
                {
                    yield return filePath;
                }

                IEnumerable<string> subDirectoryPaths = Enumerable.Empty<string>();
                try
                {
                    subDirectoryPaths = Directory.EnumerateDirectories(currentDirectoryPath);
                }
                catch
                {
                }

                foreach (string subDirectoryPath in subDirectoryPaths)
                {
                    try
                    {
                        if ((File.GetAttributes(subDirectoryPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        pendingDirectories.Push(subDirectoryPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void AssertSafeManagedPath(string targetDirectoryPath, string path)
        {
            string targetRoot = GetValidatedFullPath(targetDirectoryPath, nameof(targetDirectoryPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = GetValidatedFullPath(path, nameof(path));

            if (!this.IsPathWithinTarget(targetRoot, fullPath))
            {
                throw new InvalidOperationException("Refusing to touch a path outside the target folder: " + fullPath);
            }

            string currentPath = fullPath;
            while (!string.IsNullOrEmpty(currentPath))
            {
                bool exists = Directory.Exists(currentPath) || File.Exists(currentPath);
                if (exists)
                {
                    FileAttributes attributes = File.GetAttributes(currentPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Refusing to touch a reparse point path: " + currentPath);
                    }
                }

                string trimmedCurrentPath = currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(trimmedCurrentPath, targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                currentPath = Path.GetDirectoryName(trimmedCurrentPath);
            }
        }

        public bool IsPathWithinTarget(string targetRoot, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) throw new ArgumentException("Value cannot be empty.", nameof(targetRoot));
            if (string.IsNullOrWhiteSpace(fullPath)) throw new ArgumentException("Value cannot be empty.", nameof(fullPath));

            string normalizedTarget = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedTarget, normalizedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = normalizedTarget + Path.DirectorySeparatorChar;
            return normalizedFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
