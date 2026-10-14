using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ShvdnCompat.Tests
{
    internal static class TestPaths
    {
        private static string Meta(string key) =>
            typeof(TestPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value;

        public static string RepoRoot => Meta("RepoRoot");
        public static string NetFxRefDir => Meta("NetFxRefDir");
        public static string FixtureDll => Meta("FixtureDll");

        public static string NightlyApi(int major) => Path.Combine(RepoRoot, "ref", "nightly", $"ScriptHookVDotNet{major}.dll");
        public static string LegacyApi(int major) => Path.Combine(RepoRoot, "ref", "legacy", $"ScriptHookVDotNet{major}.dll");
        public static string ShvdnAsi => Path.Combine(RepoRoot, "ref", "nightly", "ScriptHookVDotNet.asi");

        /// <summary>
        /// Optional extra legacy API builds (e.g. every stable release from NuGet) to check against.
        /// Point SHVDN_EXTRA_LEGACY_APIS at a folder; every ScriptHookVDotNet{2,3}.dll below it is used.
        /// </summary>
        public static string[] ExtraLegacyApis(int major)
        {
            string dir = Environment.GetEnvironmentVariable("SHVDN_EXTRA_LEGACY_APIS");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, $"ScriptHookVDotNet{major}.dll", SearchOption.AllDirectories);
        }

        public static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "shvdncompat-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
