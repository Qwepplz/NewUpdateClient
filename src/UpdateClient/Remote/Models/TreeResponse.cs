using System.Collections.Generic;

namespace UpdateClient.Remote.Models
{
    internal sealed class TreeResponse
    {
        public bool truncated { get; set; }

        public List<TreeEntry> tree { get; set; }
    }
}
