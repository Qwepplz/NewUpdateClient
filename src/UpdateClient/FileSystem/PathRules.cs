using System;
using System.IO;

namespace UpdateClient.FileSystem
{
    internal static class PathRules
    {
        public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be empty.", nameof(path));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
