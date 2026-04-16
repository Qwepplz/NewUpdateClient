using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using UpdateClient.Config;
using UpdateClient.Remote.Models;
using UpdateClient.Security;

namespace UpdateClient.Remote
{
    internal interface IRemoteRepositoryClient
    {
        RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string tempDirectoryPath, RepositoryRemoteKind remoteKind);

        string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind);
    }

    internal sealed class RemoteRepositoryClient : IRemoteRepositoryClient
    {
        private readonly IRepositoryUrlBuilder urlBuilder;
        private readonly IGitBlobHasher gitBlobHasher;

        public RemoteRepositoryClient(IRepositoryUrlBuilder urlBuilder, IGitBlobHasher gitBlobHasher)
        {
            if (urlBuilder == null) throw new ArgumentNullException(nameof(urlBuilder));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            this.urlBuilder = urlBuilder;
            this.gitBlobHasher = gitBlobHasher;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));

            RepositoryTreeResult treeResult = this.GetRemoteTree(target, remoteKind);
            this.ProbeRawAccess(target, treeResult, tempDirectoryPath, remoteKind);
            return treeResult;
        }

        public string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
            if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only blob entries can be downloaded.");

            Directory.CreateDirectory(tempDirectoryPath);
            string url = this.urlBuilder.BuildRepositoryRawUrl(target, branch, entry.path, remoteKind);
            string tempPath = Path.Combine(tempDirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                this.DownloadToFile(url, tempPath);
                string actualSha = this.gitBlobHasher.ComputeForFile(tempPath);
                if (!string.Equals(actualSha, entry.sha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("Downloaded file SHA mismatch. Expected {0}, got {1}.", entry.sha, actualSha));
                }

                return tempPath;
            }
            catch (Exception exception)
            {
                this.TryDeleteFile(tempPath);
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private string GetDefaultBranch(RepositoryTarget target, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            try
            {
                RemoteJsonResponse<RepoInfo> response = this.RequestJsonFromUrl<RepoInfo>(this.urlBuilder.BuildRepositoryInfoUrl(target, remoteKind));
                if (response.Value != null && !string.IsNullOrWhiteSpace(response.Value.default_branch))
                {
                    return response.Value.default_branch;
                }
            }
            catch
            {
            }

            return AppOptions.CommonBranchNames[0];
        }

        private RepositoryTreeResult GetRemoteTree(RepositoryTarget target, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            List<string> branchCandidates = new List<string>();
            branchCandidates.Add(this.GetDefaultBranch(target, remoteKind));
            branchCandidates.AddRange(AppOptions.CommonBranchNames);

            return this.GetRemoteTree(target, branchCandidates, remoteKind);
        }

        private RepositoryTreeResult GetRemoteTree(RepositoryTarget target, IEnumerable<string> branchCandidates, RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (branchCandidates == null) throw new ArgumentNullException(nameof(branchCandidates));

            List<string> errors = new List<string>();
            HashSet<string> seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string branch in branchCandidates)
            {
                if (string.IsNullOrWhiteSpace(branch) || !seenBranches.Add(branch))
                {
                    continue;
                }

                try
                {
                    string url = this.urlBuilder.BuildRepositoryTreeUrl(target, branch, remoteKind);
                    RemoteJsonResponse<TreeResponse> response = this.RequestJsonFromUrl<TreeResponse>(url);
                    TreeResponse tree = response.Value;
                    if (tree == null || tree.tree == null)
                    {
                        throw new InvalidOperationException("Repository API returned no file tree.");
                    }

                    if (tree.truncated)
                    {
                        throw new InvalidOperationException("Repository API returned a truncated tree. Refusing to continue because deletion would be unsafe.");
                    }

                    return new RepositoryTreeResult
                    {
                        Branch = branch,
                        Source = response.Url,
                        Tree = tree.tree
                    };
                }
                catch (Exception exception)
                {
                    errors.Add(branch + " => " + exception.Message);
                }
            }

            throw new InvalidOperationException(
                "Cannot read repository tree." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        private void ProbeRawAccess(RepositoryTarget target, RepositoryTreeResult treeResult, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            if (treeResult == null) throw new ArgumentNullException(nameof(treeResult));

            TreeEntry probeEntry = treeResult.Tree
                .Where(item =>
                    item != null
                    && string.Equals(item.type, "blob", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(item.path))
                .OrderBy(item => item.size)
                .FirstOrDefault();

            if (probeEntry == null)
            {
                return;
            }

            string tempPath = null;
            try
            {
                tempPath = this.DownloadVerifiedFileToTemporaryPath(target, treeResult.Branch, probeEntry, tempDirectoryPath, remoteKind);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    this.TryDeleteFile(tempPath);
                }
            }
        }

        private RemoteJsonResponse<T> RequestJsonFromUrl<T>(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Value cannot be empty.", nameof(url));

            try
            {
                string content = this.DownloadString(url, AppOptions.ApiAcceptHeader);
                JavaScriptSerializer serializer = CreateSerializer();
                T value = serializer.Deserialize<T>(content);
                return new RemoteJsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private string DownloadString(string url, string accept)
        {
            HttpWebRequest request = this.CreateRequest(url, accept);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(EnsureStream(stream), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private void DownloadToFile(string url, string destination)
        {
            HttpWebRequest request = this.CreateRequest(url, AppOptions.BinaryAcceptHeader);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                EnsureStream(stream).CopyTo(fileStream);
            }
        }

        private HttpWebRequest CreateRequest(string url, string accept)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = AppOptions.RemoteUserAgent;
            request.Accept = accept;
            request.Timeout = AppOptions.RequestTimeoutMs;
            request.ReadWriteTimeout = AppOptions.RequestTimeoutMs;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Proxy = WebRequest.DefaultWebProxy;
            return request;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            return serializer;
        }

        private static Stream EnsureStream(Stream stream)
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Remote endpoint returned no response stream.");
            }

            return stream;
        }

        private void TryDeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
