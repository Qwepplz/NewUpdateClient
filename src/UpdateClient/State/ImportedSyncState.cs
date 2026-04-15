using System;
using System.Collections.Generic;

namespace UpdateClient.State
{
    internal sealed class ImportedSyncState
    {
        public ImportedSyncState()
        {
            this.TrackedFiles = new List<string>();
            this.Files = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
        }

        public List<string> TrackedFiles { get; private set; }

        public Dictionary<string, CachedFileState> Files { get; private set; }

        public bool CacheUnreadable { get; set; }
    }
}
