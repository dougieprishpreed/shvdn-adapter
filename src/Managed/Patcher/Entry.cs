#if NETFRAMEWORK
using System;
using System.IO;

namespace ShvdnCompat
{
    /// <summary>
    /// Entry point called by ShvdnLegacyCompat.asi through ICLRRuntimeHost::ExecuteInDefaultAppDomain.
    /// The work runs in a throwaway AppDomain so Mono.Cecil and the patcher are unloaded afterwards and
    /// nothing is left behind in the default domain SHVDN itself lives in.
    /// </summary>
    public static class Entry
    {
        public static int Run(string argument)
        {
            AppDomain domain = null;
            try
            {
                string self = typeof(Entry).Assembly.Location;
                domain = AppDomain.CreateDomain("ShvdnLegacyCompat_Patcher", null, new AppDomainSetup
                {
                    ApplicationBase = Path.GetDirectoryName(self),
                });
                var worker = (Worker)domain.CreateInstanceFromAndUnwrap(self, typeof(Worker).FullName);
                return worker.Execute(argument);
            }
            catch (Exception e)
            {
                TryLogFatal(argument, e);
                return 99;
            }
            finally
            {
                if (domain != null)
                {
                    try { AppDomain.Unload(domain); } catch (Exception) { }
                }
            }
        }

        private static void TryLogFatal(string argument, Exception e)
        {
            try
            {
                new Log(Options.Parse(argument).LogFile).Error("Patcher crashed: " + e);
            }
            catch (Exception)
            {
            }
        }
    }

    public sealed class Worker : MarshalByRefObject
    {
        public int Execute(string argument)
        {
            Options options = Options.Parse(argument);
            var log = new Log(options.LogFile);
            try
            {
                return Engine.Run(options, log);
            }
            catch (Exception e)
            {
                log.Error("Patcher failed: " + e);
                return 98;
            }
        }
    }
}
#endif
