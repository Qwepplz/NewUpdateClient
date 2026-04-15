using System;
using System.IO;
using UpdateClient.FileSystem;

namespace UpdateClient.Logging
{
    internal sealed class LogSession : IDisposable
    {
        private readonly TextWriter originalOut;
        private readonly TextWriter originalError;
        private readonly DailyLogFileWriter dailyWriter;
        private readonly TimestampedFileWriter fileWriter;
        private bool attached;
        private bool disposed;

        private LogSession(TextWriter consoleOut, TextWriter consoleError, DailyLogFileWriter writer)
        {
            this.originalOut = consoleOut;
            this.originalError = consoleError;
            this.dailyWriter = writer;
            this.fileWriter = new TimestampedFileWriter(writer);
        }

        public string CurrentLogPath
        {
            get { return this.dailyWriter.CurrentPath; }
        }

        public TextWriter ConsoleWriter
        {
            get { return this.originalOut; }
        }

        public static LogSession Create(string targetDirectoryPath, ISafePathService safePathService)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));

            DailyLogFileWriter writer = new DailyLogFileWriter(targetDirectoryPath, safePathService);
            return new LogSession(Console.Out, Console.Error, writer);
        }

        public void Attach()
        {
            if (this.attached)
            {
                return;
            }

            Console.SetOut(new TeeTextWriter(this.originalOut, this.fileWriter));
            Console.SetError(new TeeTextWriter(this.originalError, this.fileWriter));
            this.attached = true;
        }

        public void WriteSessionStart(string targetDirectoryPath, string[] args)
        {
            this.WriteLogOnlyLine(string.Empty);
            this.WriteLogOnlyLine("===== Session started =====");
            this.WriteLogOnlyLine("Target folder: " + targetDirectoryPath);
            this.WriteLogOnlyLine("Arguments: " + FormatArguments(args));
        }

        public void WriteLogOnlyLine(string message)
        {
            this.fileWriter.WriteLine(message ?? string.Empty);
            this.fileWriter.Flush();
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;

            try
            {
                this.WriteLogOnlyLine("===== Session ended =====");
                this.WriteLogOnlyLine(string.Empty);
            }
            catch
            {
            }

            if (this.attached)
            {
                try
                {
                    Console.Out.Flush();
                }
                catch
                {
                }

                try
                {
                    Console.Error.Flush();
                }
                catch
                {
                }

                Console.SetOut(this.originalOut);
                Console.SetError(this.originalError);
                this.attached = false;
            }

            this.fileWriter.Dispose();
            this.dailyWriter.Dispose();
        }

        private static string FormatArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "(none)";
            }

            return string.Join(" ", args);
        }
    }
}
