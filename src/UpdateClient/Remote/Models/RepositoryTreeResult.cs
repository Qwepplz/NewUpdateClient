using System.Collections.Generic;

namespace UpdateClient.Remote.Models
{
    internal sealed class RepositoryTreeResult
    {
        public string Branch { get; set; }

        public string Source { get; set; }

        public List<TreeEntry> Tree { get; set; }
    }
}
