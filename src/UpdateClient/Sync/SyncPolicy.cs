using System;

namespace UpdateClient.Sync
{
    internal static class SyncPolicy
    {
        internal static bool IsExcludedRootFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || relativePath.IndexOf('/') >= 0
                || relativePath.IndexOf('\\') >= 0)
            {
                return false;
            }

            return MatchesName(relativePath, "LICENSE")
                || MatchesName(relativePath, ".gitattributes")
                || MatchesName(relativePath, "README");
        }

        private static bool MatchesName(string fileName, string name)
        {
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = name + ".";
            return fileName.Length > prefix.Length
                && fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
