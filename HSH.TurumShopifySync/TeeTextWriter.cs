using System;
using System.IO;
using System.Text;

namespace HSH.TurumShopifySync
{
    public class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;

        public TeeTextWriter(TextWriter console, TextWriter file)
        {
            _console = console;
            _file = file;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string value)
        {
            var lvl = "INFO";

            if (!string.IsNullOrWhiteSpace(value))
            {
                var v = value.ToLowerInvariant();

                if (v.Contains("exception") ||
                    v.Contains("failed") ||
                    v.Contains("error") ||
                    v.Contains("422") ||
                    v.Contains("500"))
                {
                    lvl = "ERROR";
                }
                else if (v.Contains("skip") ||
                         v.Contains("warning") ||
                         v.Contains("retry") ||
                         v.Contains("429"))
                {
                    lvl = "WARN";
                }
            }

            var line = DateTime.Now.ToString("HH:mm:ss") + " | " +
                       lvl.PadRight(5) + " | " +
                       value;

            _console.WriteLine(line);
            _file.WriteLine(line);
            _file.Flush();
        }

        public override void Write(char value)
        {
            _console.Write(value);
            _file.Write(value);
        }
    }
}
