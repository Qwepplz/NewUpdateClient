using System;
using System.IO;
using System.Text;

namespace UpdateClient.Logging
{
    internal sealed class TimestampedFileWriter : TextWriter
    {
        private readonly TextWriter innerWriter;
        private bool isLineStart = true;

        public TimestampedFileWriter(TextWriter writer)
        {
            this.innerWriter = writer;
        }

        public override Encoding Encoding
        {
            get { return this.innerWriter.Encoding; }
        }

        public override void Write(char value)
        {
            if (this.isLineStart && value != '\r' && value != '\n')
            {
                this.innerWriter.Write("[");
                this.innerWriter.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                this.innerWriter.Write("] ");
                this.isLineStart = false;
            }

            this.innerWriter.Write(value);

            if (value == '\n')
            {
                this.isLineStart = true;
            }
            else if (value != '\r')
            {
                this.isLineStart = false;
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (char character in value)
            {
                this.Write(character);
            }
        }

        public override void WriteLine()
        {
            this.innerWriter.WriteLine();
            this.isLineStart = true;
        }

        public override void WriteLine(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                this.Write(value);
            }

            this.innerWriter.WriteLine();
            this.isLineStart = true;
        }

        public override void Flush()
        {
            this.innerWriter.Flush();
        }
    }
}
