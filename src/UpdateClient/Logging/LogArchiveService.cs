using System;
using System.IO;
using UpdateClient.Compression;
using UpdateClient.Config;
using UpdateClient.FileSystem;

namespace UpdateClient.Logging
{
    internal static class LogArchiveService
    {
        private static readonly ILogArchiveCompressor Compressor = new ManagedSevenZipLogArchiveCompressor();

        internal static void TryArchivePreviousLogs(
            string targetDirectoryPath,
            string currentLogPath,
            ISafePathService safePathService,
            Action<string> writeLogLine)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)
                || string.IsNullOrWhiteSpace(currentLogPath)
                || safePathService == null
                || writeLogLine == null)
            {
                return;
            }

            string normalizedCurrentLogPath = safePathService.GetFullPath(currentLogPath);
            string logDirectoryPath = safePathService.GetLogDirectoryPath(targetDirectoryPath);
            if (!Directory.Exists(logDirectoryPath))
            {
                return;
            }

            string[] logFilePaths = Directory.GetFiles(
                logDirectoryPath,
                AppOptions.LogFilePrefix + "*" + AppOptions.LogFileExtension);

            if (logFilePaths.Length == 0)
            {
                return;
            }

            Array.Sort(logFilePaths, StringComparer.OrdinalIgnoreCase);

            int archivedCount = 0;
            foreach (string logFilePath in logFilePaths)
            {
                string fullLogPath = safePathService.GetFullPath(logFilePath);
                if (string.Equals(fullLogPath, normalizedCurrentLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryArchiveSingleLog(targetDirectoryPath, fullLogPath, safePathService, writeLogLine))
                {
                    archivedCount++;
                }
            }

            if (archivedCount > 0)
            {
                writeLogLine("Archived previous log files: " + archivedCount);
            }
        }

        private static bool TryArchiveSingleLog(
            string targetDirectoryPath,
            string logPath,
            ISafePathService safePathService,
            Action<string> writeLogLine)
        {
            string archivePath = Path.ChangeExtension(logPath, AppOptions.LogArchiveExtension);
            string tempArchivePath = archivePath + AppOptions.LogArchiveTempExtension;

            safePathService.AssertSafeManagedPath(targetDirectoryPath, logPath);
            safePathService.AssertSafeManagedPath(targetDirectoryPath, archivePath);
            safePathService.AssertSafeManagedPath(targetDirectoryPath, tempArchivePath);

            TryDeleteFile(tempArchivePath);

            try
            {
                Compressor.CompressToArchive(logPath, tempArchivePath);
                TryDeleteFile(archivePath);
                File.Move(tempArchivePath, archivePath);
                File.Delete(logPath);
                writeLogLine("Archived old log: " + Path.GetFileName(logPath) + " -> " + Path.GetFileName(archivePath));
                return true;
            }
            catch (Exception exception)
            {
                TryDeleteFile(tempArchivePath);
                writeLogLine("Old log compression failed: " + Path.GetFileName(logPath) + " => " + exception.Message);
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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
