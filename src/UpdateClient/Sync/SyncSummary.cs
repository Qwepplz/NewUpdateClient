namespace UpdateClient.Sync
{
    internal sealed class SyncSummary
    {
        public string Branch { get; set; }

        public string Source { get; set; }

        public int Added { get; set; }

        public int Updated { get; set; }

        public int Removed { get; set; }

        public int Unchanged { get; set; }

        public int StaleArtifactsRemoved { get; set; }
    }
}
