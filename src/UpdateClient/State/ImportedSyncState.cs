using System;
using System.Collections.Generic;
using UpdateClient.FileSystem;

namespace UpdateClient.State
{
    internal sealed class ImportedSyncState
    {
        public ImportedSyncState()
        {
            this.TrackedFiles = new List<string>();
            this.Files = new Dictionary<string, CachedFileState>(PathRules.Comparer);
        }

        public List<string> TrackedFiles { get; private set; }

        public Dictionary<string, CachedFileState> Files { get; private set; }

        public bool CacheUnreadable { get; set; }
    }
}
