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

            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}", target.GithubOwner, target.GithubRepo);
            }

            AssertMirrorConfigured(target);
            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}", target.MirrorOwner, target.MirrorRepo);
        }

        public string BuildRepositoryTreeUrl(RepositoryTarget target, string branch, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));

            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", target.GithubOwner, target.GithubRepo, encodedBranch);
            }

            AssertMirrorConfigured(target);
            return string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", target.MirrorOwner, target.MirrorRepo, encodedBranch);
        }

        public string BuildRepositoryRawUrl(RepositoryTarget target, string branch, string relativePath, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));

            string encodedPath = ConvertToUrlPath(relativePath);
            string encodedBranch = Uri.EscapeDataString(branch);
            if (remoteKind == RepositoryRemoteKind.Github)
            {
                return string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", target.GithubOwner, target.GithubRepo, encodedBranch, encodedPath);
            }

            AssertMirrorConfigured(target);
            return string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", target.MirrorOwner, target.MirrorRepo, encodedBranch, encodedPath);
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
