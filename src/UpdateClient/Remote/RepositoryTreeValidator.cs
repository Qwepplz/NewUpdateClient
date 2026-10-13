using System;
using System.Collections.Generic;
using System.IO;
using UpdateClient.FileSystem;
using UpdateClient.Remote.Models;

namespace UpdateClient.Remote
{
    internal static class RepositoryTreeValidator
    {
        public static void Validate(IEnumerable<TreeEntry> entries)
        {
            if (entries == null) throw new InvalidOperationException("Invalid repository tree: entries are missing.");
            var paths = new Dictionary<string, bool>(PathRules.Comparer);
            foreach (TreeEntry entry in entries)
            {
                if (entry == null) throw Invalid("<null>", "entry is missing");
                string path = entry.path;
                if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
                    || path.IndexOf('\\') >= 0
                    || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
                    throw Invalid(path, "path must be a safe relative Git path");
                foreach (string segment in path.Split('/'))
                {
                    if (segment.Length == 0 || segment == "." || segment == ".."
                        || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw Invalid(path, "invalid path segment");
                    if (OperatingSystem.IsWindows()
                        && (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)
                            || IsWindowsDeviceName(segment)))
                        throw Invalid(path, "Windows path alias or device name");
                }
                bool isBlob = string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase);
                if (isBlob && entry.mode == "120000") throw Invalid(path, "symbolic links are not supported");
                if (isBlob && !string.IsNullOrEmpty(entry.mode) && entry.mode != "100644" && entry.mode != "100755")
                    throw Invalid(path, "unsupported blob mode " + entry.mode);
                if (!paths.TryAdd(path, isBlob)) throw Invalid(path, "duplicate path on this platform");
            }
            foreach (string path in paths.Keys)
            {
                for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                {
                    if (paths.TryGetValue(path.Substring(0, slash), out bool ancestorIsFile) && ancestorIsFile)
                        throw Invalid(path, "file conflicts with a child path");
                }
            }
        }

        private static bool IsWindowsDeviceName(string segment)
        {
            string name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL"
                || name == "CONIN$" || name == "CONOUT$") return true;
            return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal)
                || name.StartsWith("LPT", StringComparison.Ordinal))
                && ("123456789¹²³".IndexOf(name[3]) >= 0);
        }

        private static InvalidOperationException Invalid(string path, string reason)
        {
            return new InvalidOperationException("Invalid repository tree path '" + path + "': " + reason + ".");
        }
    }
}
