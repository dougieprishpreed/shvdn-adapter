using System;
using System.IO;
using System.Text;

namespace ShvdnCompat
{
    /// <summary>Appends to the same log file the native ASI writes to.</summary>
    public sealed class Log
    {
        private readonly string _path;
        private readonly TextWriter _echo;

        public Log(string path, TextWriter echo = null)
        {
            _path = path;
            _echo = echo;
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] [patcher] {message}";
            _echo?.WriteLine(line);
            if (string.IsNullOrEmpty(_path)) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        sw.WriteLine(line);
                    }
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(20);
                }
            }
        }
    }
}
