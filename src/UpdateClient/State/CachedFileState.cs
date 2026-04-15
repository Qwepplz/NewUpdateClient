namespace UpdateClient.State
{
    internal sealed class CachedFileState
    {
        public string path { get; set; }

        public string remote_sha { get; set; }

        public long length { get; set; }

        public long last_write_utc_ticks { get; set; }
    }
}
