using System.IO;
using System.Text;

namespace UpdateClient.Logging
{
    internal sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter primary;
        private readonly TextWriter secondary;

        public TeeTextWriter(TextWriter primaryWriter, TextWriter secondaryWriter)
        {
            this.primary = primaryWriter;
            this.secondary = secondaryWriter;
        }

        public override Encoding Encoding
        {
            get { return this.primary.Encoding; }
        }

        public override void Write(char value)
        {
            this.primary.Write(value);
            this.secondary.Write(value);
        }

        public override void Write(string value)
        {
            this.primary.Write(value);
            this.secondary.Write(value);
        }

        public override void WriteLine()
        {
            this.primary.WriteLine();
            this.secondary.WriteLine();
        }

        public override void WriteLine(string value)
        {
            this.primary.WriteLine(value);
            this.secondary.WriteLine(value);
        }

        public override void Flush()
        {
            this.primary.Flush();
            this.secondary.Flush();
        }
    }
}
