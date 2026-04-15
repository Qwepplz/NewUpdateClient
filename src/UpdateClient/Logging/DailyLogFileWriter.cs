using System;
using System.IO;
using System.Text;
using UpdateClient.Config;
using UpdateClient.FileSystem;

namespace UpdateClient.Logging
{
    internal sealed class DailyLogFileWriter : TextWriter
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly string targetDirectoryPath;
        private readonly string logDirectoryPath;
        private readonly ISafePathService safePathService;
        private StreamWriter currentWriter;
        private DateTime currentDate = DateTime.MinValue;
        private string currentPath = string.Empty;

        public DailyLogFileWriter(string targetDirectoryPath, ISafePathService safePathService)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));

            this.targetDirectoryPath = targetDirectoryPath;
            this.safePathService = safePathService;
            this.logDirectoryPath = safePathService.GetLogDirectoryPath(targetDirectoryPath);
        }

        public string CurrentPath
        {
            get
            {
                this.EnsureWriter();
                return this.currentPath;
            }
        }

        public override Encoding Encoding
        {
            get { return Utf8WithoutBom; }
        }

        public override void Write(char value)
        {
            this.EnsureWriter();
            this.currentWriter.Write(value);
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            this.EnsureWriter();
            this.currentWriter.Write(value);
        }

        public override void WriteLine()
        {
            this.EnsureWriter();
            this.currentWriter.WriteLine();
        }

        public override void WriteLine(string value)
        {
            this.EnsureWriter();
            this.currentWriter.WriteLine(value);
        }

        public override void Flush()
        {
            if (this.currentWriter != null)
            {
                this.currentWriter.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && this.currentWriter != null)
            {
                this.currentWriter.Dispose();
                this.currentWriter = null;
            }

            base.Dispose(disposing);
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.Now.Date;
            if (this.currentWriter != null && today == this.currentDate)
            {
                return;
            }

            string nextPath = this.safePathService.GetFullPath(
                Path.Combine(this.logDirectoryPath, AppOptions.LogFilePrefix + today.ToString(AppOptions.LogFileDateFormat) + AppOptions.LogFileExtension));

            this.safePathService.AssertSafeManagedPath(this.targetDirectoryPath, nextPath);
            Directory.CreateDirectory(this.logDirectoryPath);
            this.safePathService.AssertSafeManagedPath(this.targetDirectoryPath, nextPath);

            if (this.currentWriter != null)
            {
                this.currentWriter.Flush();
                this.currentWriter.Dispose();
            }

            this.currentDate = today;
            this.currentPath = nextPath;
            this.currentWriter = new StreamWriter(File.Open(this.currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Utf8WithoutBom);
            this.currentWriter.AutoFlush = true;
        }
    }
}
