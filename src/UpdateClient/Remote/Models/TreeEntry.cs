namespace UpdateClient.Remote.Models
{
    internal sealed class TreeEntry
    {
        public string path { get; set; }

        public string type { get; set; }

        public string sha { get; set; }

        public long size { get; set; }
    }
}
