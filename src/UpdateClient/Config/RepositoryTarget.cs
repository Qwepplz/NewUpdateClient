using System;
using System.Collections.Generic;

namespace UpdateClient.Config
{
    internal sealed class RepositoryTarget
    {
        public RepositoryTarget(string displayName, string stateKey, string githubOwner, string githubRepo, string mirrorOwner, string mirrorRepo, IReadOnlyList<string> strictDirectories)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Value cannot be empty.", nameof(displayName));
            if (string.IsNullOrWhiteSpace(stateKey)) throw new ArgumentException("Value cannot be empty.", nameof(stateKey));
            if (string.IsNullOrWhiteSpace(githubOwner)) throw new ArgumentException("Value cannot be empty.", nameof(githubOwner));
            if (string.IsNullOrWhiteSpace(githubRepo)) throw new ArgumentException("Value cannot be empty.", nameof(githubRepo));
            if (strictDirectories == null) throw new ArgumentNullException(nameof(strictDirectories));

            this.DisplayName = displayName;
            this.StateKey = stateKey;
            this.GithubOwner = githubOwner;
            this.GithubRepo = githubRepo;
            this.MirrorOwner = mirrorOwner;
            this.MirrorRepo = mirrorRepo;
            this.StrictDirectories = strictDirectories;
        }

        public string DisplayName { get; private set; }
        public string StateKey { get; private set; }
        public string GithubOwner { get; private set; }
        public string GithubRepo { get; private set; }
        public string MirrorOwner { get; private set; }
        public string MirrorRepo { get; private set; }
        public IReadOnlyList<string> StrictDirectories { get; private set; }
        public bool HasMirror { get { return !string.IsNullOrWhiteSpace(this.MirrorOwner) && !string.IsNullOrWhiteSpace(this.MirrorRepo); } }
    }
}
