using System;
using System.IO;

namespace UpdateClient.ConsoleUi
{
    internal sealed class ProgressDisplay : IDisposable
    {
        private readonly TextWriter outputWriter;
        private bool canRefresh;
        private bool initialized;
        private bool completed;
        private bool fallbackHeaderWritten;
        private int firstLineTop;

        public ProgressDisplay(TextWriter writer, bool refresh)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            this.outputWriter = writer;
            this.canRefresh = refresh;
        }

        public static bool CanRefresh()
        {
            try
            {
                return Environment.UserInteractive && !Console.IsOutputRedirected;
            }
            catch
            {
                return false;
            }
        }

        public void Update(string status, string detail)
        {
            if (this.completed)
            {
                return;
            }

            if (!this.canRefresh)
            {
                this.WriteFallbackHeader(status);
                return;
            }

            if (!this.EnsureInitialized())
            {
                this.WriteFallbackHeader(status);
                return;
            }

            this.Render(status, detail);
        }

        public void Complete(string status, string detail)
        {
            if (this.completed)
            {
                return;
            }

            this.completed = true;

            if (!this.canRefresh)
            {
                this.WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    this.outputWriter.WriteLine("       " + detail);
                    this.outputWriter.Flush();
                }

                return;
            }

            if (!this.EnsureInitialized())
            {
                this.WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    this.outputWriter.WriteLine("       " + detail);
                    this.outputWriter.Flush();
                }

                return;
            }

            this.Render(status, detail);
            this.MoveCursorBelowStatus();
        }

        public void Dispose()
        {
            if (!this.completed && this.initialized && this.canRefresh)
            {
                this.MoveCursorBelowStatus();
            }
        }

        private bool EnsureInitialized()
        {
            if (this.initialized)
            {
                return true;
            }

            try
            {
                this.outputWriter.WriteLine();
                this.outputWriter.WriteLine();
                this.outputWriter.Flush();
                this.firstLineTop = Math.Max(0, Console.CursorTop - 2);
                this.initialized = true;
                return true;
            }
            catch
            {
                this.canRefresh = false;
                return false;
            }
        }

        private void Render(string status, string detail)
        {
            try
            {
                this.WriteStatusLine(this.firstLineTop, status);
                this.WriteStatusLine(this.firstLineTop + 1, detail);
                Console.SetCursorPosition(0, this.firstLineTop + 1);
                this.outputWriter.Flush();
            }
            catch
            {
                this.canRefresh = false;
            }
        }

        private void WriteStatusLine(int top, string text)
        {
            Console.SetCursorPosition(0, top);
            this.outputWriter.Write(this.FitConsoleLine(text));
        }

        private string FitConsoleLine(string text)
        {
            string normalizedText = NormalizeStatusText(text);
            int width = GetConsoleLineWidth();
            if (normalizedText.Length > width)
            {
                normalizedText = width <= 3
                    ? normalizedText.Substring(0, width)
                    : normalizedText.Substring(0, width - 3) + "...";
            }

            return normalizedText.PadRight(width);
        }

        private static string NormalizeStatusText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static int GetConsoleLineWidth()
        {
            try
            {
                return Math.Max(1, Console.BufferWidth - 1);
            }
            catch
            {
                return 79;
            }
        }

        private void MoveCursorBelowStatus()
        {
            try
            {
                Console.SetCursorPosition(0, this.firstLineTop + 2);
                this.outputWriter.Flush();
            }
            catch
            {
                this.canRefresh = false;
            }
        }

        private void WriteFallbackHeader(string status)
        {
            if (this.fallbackHeaderWritten || string.IsNullOrEmpty(status))
            {
                return;
            }

            this.outputWriter.WriteLine(status);
            this.outputWriter.Flush();
            this.fallbackHeaderWritten = true;
        }
    }
}
