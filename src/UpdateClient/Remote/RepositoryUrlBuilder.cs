using System;
using System.Collections.Generic;
using System.Linq;
using UpdateClient.Config;

namespace UpdateClient.Remote
{
    internal interface IRepositoryUrlBuilder
    {
        List<string> BuildRepositoryInfoUrls(RepositoryTarget target);

        List<string> BuildRepositoryTreeUrls(RepositoryTarget target, string branch);

        List<string> BuildRepositoryRawUrls(RepositoryTarget target, string branch, string relativePath);
    }

    internal sealed class RepositoryUrlBuilder : IRepositoryUrlBuilder
    {
        public List<string> BuildRepositoryInfoUrls(RepositoryTarget target)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));

            // composing upstream URLs.
            List<string> urls = new List<string>();
            urls.Add(string.Format("https://api.github.com/repos/{0}/{1}", target.GithubOwner, target.GithubRepo));

            if (target.HasMirror)
            {
                urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}", target.MirrorOwner, target.MirrorRepo));
            }

            return urls;
        }

        public List<string> BuildRepositoryTreeUrls(RepositoryTarget target, string branch)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));

            // composing upstream URLs.
            string encodedBranch = Uri.EscapeDataString(branch);
            List<string> urls = new List<string>();
            urls.Add(string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", target.GithubOwner, target.GithubRepo, encodedBranch));

            if (target.HasMirror)
            {
                urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", target.MirrorOwner, target.MirrorRepo, encodedBranch));
            }

            return urls;
        }

        public List<string> BuildRepositoryRawUrls(RepositoryTarget target, string branch, string relativePath)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Value cannot be empty.", nameof(relativePath));

            // composing upstream URLs.
            string encodedPath = ConvertToUrlPath(relativePath);
            List<string> urls = new List<string>();
            urls.Add(string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", target.GithubOwner, target.GithubRepo, Uri.EscapeDataString(branch), encodedPath));

            if (target.HasMirror)
            {
                urls.Add(string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", target.MirrorOwner, target.MirrorRepo, Uri.EscapeDataString(branch), encodedPath));
            }

            return urls;
        }

        private static string ConvertToUrlPath(string relativePath)
        {
            // normalizing path segments.
            string normalizedPath = relativePath.Replace('\\', '/');
            string[] segments = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            // escaping path segments.
            return string.Join("/", segments.Select(Uri.EscapeDataString).ToArray());
        }
    }
}
