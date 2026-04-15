using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UpdateClient.Remote.Models;

namespace UpdateClient.Security
{
    internal interface IGitBlobHasher
    {
        string ComputeForFile(string path);

        bool MatchesRemoteBlob(string path, TreeEntry entry);
    }

    internal sealed class GitBlobHasher : IGitBlobHasher
    {
        public string ComputeForFile(string path)
        {
            // validating input.
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be empty.", nameof(path));

            // hashing file content.
            FileInfo fileInfo = new FileInfo(path);
            byte[] prefixBytes = Encoding.ASCII.GetBytes("blob " + fileInfo.Length + "\0");

            using (SHA1 sha1 = SHA1.Create())
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                sha1.TransformBlock(prefixBytes, 0, prefixBytes.Length, null, 0);
                byte[] buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
                }

                sha1.TransformFinalBlock(new byte[0], 0, 0);
                return ToHexString(sha1.Hash);
            }
        }

        public bool MatchesRemoteBlob(string path, TreeEntry entry)
        {
            // validating input.
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value cannot be empty.", nameof(path));
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // checking file existence and size.
            if (!File.Exists(path))
            {
                return false;
            }

            FileInfo fileInfo = new FileInfo(path);
            if (entry.size > 0 && fileInfo.Length != entry.size)
            {
                return false;
            }

            // comparing blob hash.
            string localSha = this.ComputeForFile(path);
            return string.Equals(localSha, entry.sha, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToHexString(byte[] bytes)
        {
            // formatting digest.
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
