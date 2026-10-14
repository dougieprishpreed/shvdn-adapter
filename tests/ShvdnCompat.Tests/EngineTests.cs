using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ShvdnCompat.Tests
{
    /// <summary>Runs the whole patcher against a fake game folder, the way the ASI does.</summary>
    public class EngineTests
    {
        private readonly ITestOutputHelper _out;

        public EngineTests(ITestOutputHelper output) => _out = output;

        private static string MakeGameDir()
        {
            string game = TestPaths.NewTempDir();
            File.Copy(TestPaths.NightlyApi(2), Path.Combine(game, "ScriptHookVDotNet2.dll"));
            File.Copy(TestPaths.NightlyApi(3), Path.Combine(game, "ScriptHookVDotNet3.dll"));
            Directory.CreateDirectory(Path.Combine(game, "scripts"));
            File.Copy(TestPaths.FixtureDll, Path.Combine(game, "scripts", "LegacyScriptFixture.dll"));
            return game;
        }

        private Options Run(string game, string mode)
        {
            Options o = Options.Parse($"GameDir={game}\nMode={mode}\nLogFile={Path.Combine(game, "test.log")}");
            Assert.Equal(0, Engine.Run(o, new Log(o.LogFile)));
            return o;
        }

        [Fact]
        public void RedirectModeBuildsCacheAndRedirectList()
        {
            string game = MakeGameDir();
            Options o = Run(game, "redirect");

            string[] redirects = File.ReadAllLines(o.RedirectFile).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, redirects.Length);
            foreach (string line in redirects)
            {
                string[] parts = line.Split('|');
                Assert.True(File.Exists(parts[0]));
                Assert.True(File.Exists(parts[1]));
                Assert.StartsWith(o.CacheDir, parts[1]);
            }

            // The ASI's fast path reads this index: one line per API with the original's full SHA-256.
            string[] index = File.ReadAllLines(o.CacheIndexFile).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, index.Length);
            Assert.All(index, l => Assert.Equal(64, l.Split('|')[1].Length));

            // The game folder itself is untouched.
            Assert.Equal(File.ReadAllBytes(TestPaths.NightlyApi(3)), File.ReadAllBytes(Path.Combine(game, "ScriptHookVDotNet3.dll")));

            string log = File.ReadAllText(o.LogFile);
            _out.WriteLine(log);
            Assert.Contains("all SHVDN API references resolve", log);

            // Second start: served from the cache.
            Run(game, "redirect");
            Assert.Contains("using cached patched copy", File.ReadAllText(o.LogFile));
        }

        [Fact]
        public void DiskModePatchesInPlaceAndRestoreUndoesIt()
        {
            string game = MakeGameDir();
            string api3 = Path.Combine(game, "ScriptHookVDotNet3.dll");

            Options o = Run(game, "disk");
            Assert.True(File.Exists(api3 + ".legacycompat.bak"));
            Assert.True(ShimInjector.IsPatched(Engine.ReadModule(File.ReadAllBytes(api3))));
            Assert.DoesNotContain(File.ReadAllLines(o.RedirectFile), l => l.Length > 0);

            // Redirect mode on a disk-patched install has nothing to redirect.
            Run(game, "redirect");
            Assert.DoesNotContain(File.ReadAllLines(o.RedirectFile), l => l.Length > 0);

            Run(game, "restore");
            Assert.Equal(File.ReadAllBytes(TestPaths.NightlyApi(3)), File.ReadAllBytes(api3));
        }

        [Fact]
        public void ScanReportsWhatIsStillMissing()
        {
            string game = MakeGameDir();
            Options o = Run(game, "off");
            string log = File.ReadAllText(o.LogFile);
            _out.WriteLine(log);
            Assert.Contains("LegacyScriptFixture.dll uses", log);
            Assert.Contains("PostTicker", log);
        }
    }
}
