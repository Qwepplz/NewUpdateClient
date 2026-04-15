using System;
using System.Collections.Generic;
using System.IO;
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
        string GetDefaultBranch(RepositoryTarget target);

        RepositoryTreeResult GetRemoteTree(RepositoryTarget target);

        RepositoryTreeResult GetRemoteTree(RepositoryTarget target, IEnumerable<string> branchCandidates);

        string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath);
    }

    internal sealed class RemoteRepositoryClient : IRemoteRepositoryClient
    {
        private readonly IRepositoryUrlBuilder urlBuilder;
        private readonly IGitBlobHasher gitBlobHasher;

        public RemoteRepositoryClient(IRepositoryUrlBuilder urlBuilder, IGitBlobHasher gitBlobHasher)
        {
            // validating dependencies.
            if (urlBuilder == null) throw new ArgumentNullException(nameof(urlBuilder));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            // storing dependencies.
            this.urlBuilder = urlBuilder;
            this.gitBlobHasher = gitBlobHasher;

            // enforcing transport defaults.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public string GetDefaultBranch(RepositoryTarget target)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));

            // querying repository metadata.
            try
            {
                RemoteJsonResponse<RepoInfo> response = this.RequestJsonFromUrls<RepoInfo>(this.urlBuilder.BuildRepositoryInfoUrls(target));
                if (response.Value != null && !string.IsNullOrWhiteSpace(response.Value.default_branch))
                {
                    return response.Value.default_branch;
                }
            }
            catch
            {
            }

            // falling back to common branch names.
            return AppOptions.CommonBranchNames[0];
        }

        public RepositoryTreeResult GetRemoteTree(RepositoryTarget target)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));

            // building branch candidates.
            List<string> branchCandidates = new List<string>();
            branchCandidates.Add(this.GetDefaultBranch(target));
            branchCandidates.AddRange(AppOptions.CommonBranchNames);

            return this.GetRemoteTree(target, branchCandidates);
        }

        public RepositoryTreeResult GetRemoteTree(RepositoryTarget target, IEnumerable<string> branchCandidates)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (branchCandidates == null) throw new ArgumentNullException(nameof(branchCandidates));

            // walking candidate branches.
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
                    RemoteJsonResponse<TreeResponse> response = this.RequestJsonFromUrls<TreeResponse>(this.urlBuilder.BuildRepositoryTreeUrls(target, branch));
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

        public string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath)
        {
            // validating input.
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
            if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only blob entries can be downloaded.");

            // preparing temporary storage.
            Directory.CreateDirectory(tempDirectoryPath);
            List<string> errors = new List<string>();

            foreach (string url in this.urlBuilder.BuildRepositoryRawUrls(target, branch, entry.path))
            {
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
                    errors.Add(url + " => " + exception.Message);
                    this.TryDeleteFile(tempPath);
                }
            }

            throw new InvalidOperationException(
                "All download attempts failed for " + entry.path + "." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        private RemoteJsonResponse<T> RequestJsonFromUrls<T>(IEnumerable<string> urls)
        {
            // validating input.
            if (urls == null) throw new ArgumentNullException(nameof(urls));

            // querying upstream APIs.
            List<string> errors = new List<string>();
            foreach (string url in urls)
            {
                try
                {
                    string content = this.DownloadString(url, AppOptions.ApiAcceptHeader);
                    JavaScriptSerializer serializer = CreateSerializer();
                    T value = serializer.Deserialize<T>(content);
                    return new RemoteJsonResponse<T> { Url = url, Value = value };
                }
                catch (Exception exception)
                {
                    errors.Add(url + " => " + exception.Message);
                }
            }

            throw new InvalidOperationException(
                "All upstream API requests failed." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        private string DownloadString(string url, string accept)
        {
            // preparing request.
            HttpWebRequest request = this.CreateRequest(url, accept);

            // reading response text.
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(EnsureStream(stream), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private void DownloadToFile(string url, string destination)
        {
            // preparing request.
            HttpWebRequest request = this.CreateRequest(url, AppOptions.BinaryAcceptHeader);

            // streaming response body.
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                EnsureStream(stream).CopyTo(fileStream);
            }
        }

        private HttpWebRequest CreateRequest(string url, string accept)
        {
            // creating web request.
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
            // configuring JSON serializer.
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            return serializer;
        }

        private static Stream EnsureStream(Stream stream)
        {
            // validating response stream.
            if (stream == null)
            {
                throw new InvalidOperationException("Remote endpoint returned no response stream.");
            }

            return stream;
        }

        private void TryDeleteFile(string path)
        {
            // ignoring cleanup failures.
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
