using System;
using System.Linq;
using UpdateClient.Config;

namespace UpdateClient.Remote
{
    internal interface IRepositoryUrlBuilder
    {
        string BuildRepositoryInfoUrl(RepositoryTarget target, RepositoryRemoteKind remoteKind);

        string BuildRepositoryTreeUrl(RepositoryTarget target, string branch, RepositoryRemoteKind remoteKind);

        string BuildRepositoryRawUrl(RepositoryTarget target, string branch, string relativePath, RepositoryRemoteKind remoteKind);
    }

    internal sealed class RepositoryUrlBuilder : IRepositoryUrlBuilder
    {
        public string BuildRepositoryInfoUrl(RepositoryTarget target, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            string owner = GetRepositoryOwner(target, remoteKind);
            string repo = GetRepositoryName(target, remoteKind);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}", owner, repo);
            }

            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}", owner, repo);
        }

        public string BuildRepositoryTreeUrl(RepositoryTarget target, string branch, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));

            string owner = GetRepositoryOwner(target, remoteKind);
            string repo = GetRepositoryName(target, remoteKind);
            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", owner, repo, encodedBranch);
            }

            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", owner, repo, encodedBranch);
        }

        public string BuildRepositoryRawUrl(RepositoryTarget target, string branch, string relativePath, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));

            string owner = GetRepositoryOwner(target, remoteKind);
            string repo = GetRepositoryName(target, remoteKind);
            string encodedPath = ConvertToUrlPath(relativePath);
            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", owner, repo, encodedBranch, encodedPath);
            }

            return string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", owner, repo, encodedBranch, encodedPath);
        }

        private static string GetRepositoryOwner(RepositoryTarget target, RepositoryRemoteKind remoteKind)
        {
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return target.GithubOwner;
            }

            AssertMirrorConfigured(target);
            return target.MirrorOwner;
        }

        private static string GetRepositoryName(RepositoryTarget target, RepositoryRemoteKind remoteKind)
        {
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return target.GithubRepo;
            }

            AssertMirrorConfigured(target);
            return target.MirrorRepo;
        }

        private static void AssertMirrorConfigured(RepositoryTarget target)
        {
            if (!target.HasMirror)
            {
                throw new InvalidOperationException("Repository mirror is not configured.");
            }
        }

        private static string ConvertToUrlPath(string relativePath)
        {
            string normalizedPath = relativePath.Replace('\\', '/');
            string[] segments = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("/", segments.Select(Uri.EscapeDataString).ToArray());
        }
    }
}
