#if NETFRAMEWORK
using System;
using System.IO;

namespace ShvdnCompat
{
    /// <summary>
    /// ShvdnCompat.Patcher.exe, started by ShvdnLegacyCompat.asi as a separate process so patching never
    /// shares a runtime (or its locks) with SHVDN inside GTA5.exe.
    /// Usage: ShvdnCompat.Patcher.exe &lt;options file&gt; with "key=value" lines (see <see cref="Options"/>).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Options options;
            try
            {
                if (args.Length != 1) throw new ArgumentException("expected the path of an options file");
                options = Options.Parse(File.ReadAllText(args[0]));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 64;
            }

            var log = new Log(options.LogFile);
            try
            {
                return Engine.Run(options, log);
            }
            catch (Exception e)
            {
                log.Error("Patcher failed: " + e);
                return 70;
            }
        }
    }
}
#endif
