using System;
using System.IO;
using UpdateClient.Config;

namespace UpdateClient.FileSystem
{
    internal interface IAtomicFileWriter
    {
        void WriteFileAtomically(string sourcePath, string destinationPath);
    }

    internal sealed class AtomicFileWriter : IAtomicFileWriter
    {
        public void WriteFileAtomically(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Value cannot be empty.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Value cannot be empty.", nameof(destinationPath));

            string parentDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!Directory.Exists(parentDirectoryPath))
            {
                Directory.CreateDirectory(parentDirectoryPath);
            }

            string fileName = Path.GetFileName(destinationPath);
            string stagingPath = Path.Combine(parentDirectoryPath, fileName + AppOptions.StagingArtifactMarker + Guid.NewGuid().ToString("N"));
            string backupPath = Path.Combine(parentDirectoryPath, fileName + AppOptions.BackupArtifactMarker + Guid.NewGuid().ToString("N"));

            try
            {
                File.Copy(sourcePath, stagingPath, true);
                if (File.Exists(destinationPath))
                {
                    File.Replace(stagingPath, destinationPath, backupPath, true);
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                else
                {
                    File.Move(stagingPath, destinationPath);
                }
            }
            finally
            {
                this.TryDeleteFile(stagingPath);
                this.TryDeleteFile(backupPath);
            }
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
