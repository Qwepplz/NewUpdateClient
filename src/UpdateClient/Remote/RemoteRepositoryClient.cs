using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.Text.Json;
using UpdateClient.Config;
using UpdateClient.Remote.Models;
using UpdateClient.Security;
using UpdateClient.Sync;

namespace UpdateClient.Remote
{
    internal interface IRemoteRepositoryClient
    {
        RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string branch, string tempDirectoryPath, RepositoryRemoteKind remoteKind);

        RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string tempDirectoryPath, RepositoryRemoteKind remoteKind);

        string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind);
    }

    internal sealed class RemoteRepositoryClient : IRemoteRepositoryClient, IDisposable
    {
        private readonly IRepositoryUrlBuilder urlBuilder;
        private readonly IGitBlobHasher gitBlobHasher;
        private readonly HttpClient httpClient;
        private readonly int requestTimeoutMs;

        public RemoteRepositoryClient(IRepositoryUrlBuilder urlBuilder, IGitBlobHasher gitBlobHasher)
            : this(urlBuilder, gitBlobHasher, AppOptions.RequestTimeoutMs)
        {
        }

        internal RemoteRepositoryClient(IRepositoryUrlBuilder urlBuilder, IGitBlobHasher gitBlobHasher, int requestTimeoutMs)
        {
            if (urlBuilder == null) throw new ArgumentNullException(nameof(urlBuilder));
            if (gitBlobHasher == null) throw new ArgumentNullException(nameof(gitBlobHasher));

            this.urlBuilder = urlBuilder;
            this.gitBlobHasher = gitBlobHasher;

            if (requestTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(requestTimeoutMs));
            this.requestTimeoutMs = requestTimeoutMs;
            this.httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string branch, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            ValidatePrepareRepositoryTreeArguments(target, branch, tempDirectoryPath);

            RepositoryTreeResult treeResult = this.GetRemoteTree(target, new[] { branch }, remoteKind);
            this.ProbeRawAccess(target, treeResult, tempDirectoryPath, remoteKind);
            return treeResult;
        }

        public RepositoryTreeResult PrepareRepositoryTree(RepositoryTarget target, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            ValidateGetRemoteTreeArguments(target);
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));

            string defaultBranch = this.GetDefaultBranch(target, remoteKind);
            RepositoryTreeResult treeResult = this.GetRemoteTree(target, new[] { defaultBranch, "main", "master" }, remoteKind);
            this.ProbeRawAccess(target, treeResult, tempDirectoryPath, remoteKind);
            return treeResult;
        }

        public string DownloadVerifiedFileToTemporaryPath(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath, RepositoryRemoteKind remoteKind)
        {
            ValidateDownloadArguments(target, branch, entry, tempDirectoryPath);

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
            ValidateGetRemoteTreeArguments(target);

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
                Console.WriteLine("       Default branch lookup failed, trying common branch names.");
            }

            return "main";
        }

        private RepositoryTreeResult GetRemoteTree(RepositoryTarget target, IEnumerable<string> branchCandidates, RepositoryRemoteKind remoteKind)
        {
            ValidateGetRemoteTreeArguments(target, branchCandidates);

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

                    RepositoryTreeValidator.Validate(tree.tree);
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
                    && !string.IsNullOrWhiteSpace(item.path)
                    && !SyncPolicy.IsExcludedRootFile(item.path))
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
                JsonSerializerOptions jsonOptions = CreateJsonOptions();
                T value = JsonSerializer.Deserialize<T>(content, jsonOptions);
                return new RemoteJsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(url + " => " + exception.Message, exception);
            }
        }

        private string DownloadString(string url, string accept)
        {
            using (HttpResponseMessage response = this.SendRequest(url, accept))
            using (Stream stream = response.Content.ReadAsStream())
            using (MemoryStream content = new MemoryStream())
            {
                this.CopyWithIdleTimeout(stream, content);
                content.Position = 0;
                using (StreamReader reader = new StreamReader(content, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private void DownloadToFile(string url, string destination)
        {
            using (HttpResponseMessage response = this.SendRequest(url, AppOptions.BinaryAcceptHeader))
            using (Stream stream = response.Content.ReadAsStream())
            using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                this.CopyWithIdleTimeout(stream, fileStream);
            }
        }

        private HttpResponseMessage SendRequest(string url, string accept)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            using (CancellationTokenSource timeout = new CancellationTokenSource(this.requestTimeoutMs))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", AppOptions.RemoteUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", accept);
                HttpResponseMessage response = null;
                try
                {
                    response = this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                catch (OperationCanceledException exception)
                {
                    response?.Dispose();
                    throw new TimeoutException("Request headers timed out.", exception);
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
            }
        }

        private void CopyWithIdleTimeout(Stream source, Stream destination)
        {
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read;
                using (CancellationTokenSource timeout = new CancellationTokenSource(this.requestTimeoutMs))
                {
                    try
                    {
                        read = source.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException exception)
                    {
                        throw new TimeoutException("Response body read timed out.", exception);
                    }
                }
                if (read == 0) return;
                destination.Write(buffer, 0, read);
            }
        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }

        private static void ValidatePrepareRepositoryTreeArguments(RepositoryTarget target, string branch, string tempDirectoryPath)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
        }

        private static void ValidateDownloadArguments(RepositoryTarget target, string branch, TreeEntry entry, string tempDirectoryPath)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(tempDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempDirectoryPath));
            if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only blob entries can be downloaded.");
        }

        private static void ValidateGetRemoteTreeArguments(RepositoryTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
        }

        private static void ValidateGetRemoteTreeArguments(RepositoryTarget target, IEnumerable<string> branchCandidates)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (branchCandidates == null) throw new ArgumentNullException(nameof(branchCandidates));
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 256,
                WriteIndented = true
            };
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
