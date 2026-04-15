using System.Collections.Generic;

namespace UpdateClient.State
{
    internal sealed class SyncState
    {
        public int version { get; set; }

        public List<string> tracked_files { get; set; }

        public List<CachedFileState> files { get; set; }
    }
}
