using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace ShvdnCompat
{
    public sealed class PatchReport
    {
        public List<string> Added { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>Produces patched SHVDN API assemblies and hands them to the ASI.</summary>
    public static class Engine
    {
        public const string Version = "1.0.0";
        private const string BackupSuffix = ".legacycompat.bak";

        private static string s_buildId;

        /// <summary>Changes whenever the patcher or the embedded shims change, which invalidates the cache.</summary>
        public static string BuildId
        {
            get
            {
                if (s_buildId != null) return s_buildId;
                using (SHA256 sha = SHA256.Create())
                {
                    var all = new List<byte>(Encoding.UTF8.GetBytes(Version));
                    all.AddRange(ReadShimBytes(2));
                    all.AddRange(ReadShimBytes(3));
                    s_buildId = Hex(sha.ComputeHash(all.ToArray())).Substring(0, 8);
                }
                return s_buildId;
            }
        }

        public static int Run(Options options, Log log)
        {
            log.Info($"ShvdnLegacyCompat patcher {Version} (build {BuildId}), mode={options.Mode}, game folder: {options.GameDir}");
            Directory.CreateDirectory(options.DataDir);
            Directory.CreateDirectory(options.CacheDir);

            var redirects = new List<string>();
            var scanModules = new Dictionary<int, ModuleDefinition>();
            int result = 0;

            foreach (int major in new[] { 2, 3 })
            {
                if ((major == 2 && !options.PatchV2) || (major == 3 && !options.PatchV3))
                {
                    log.Info($"ScriptHookVDotNet{major}.dll: disabled in the ini, skipped.");
                    continue;
                }

                string apiPath = Path.Combine(options.GameDir, $"ScriptHookVDotNet{major}.dll");
                try
                {
                    string effective = ProcessApi(options, log, major, apiPath, redirects);
                    if (effective != null && options.ScanScripts) scanModules[major] = ReadModule(File.ReadAllBytes(effective));
                }
                catch (Exception e)
                {
                    result = 2;
                    log.Error($"ScriptHookVDotNet{major}.dll: patching failed, SHVDN will use the unmodified file. {e}");
                }
            }

            WriteAllTextAtomic(options.RedirectFile, string.Join("\n", redirects) + "\n");

            if (options.ScanScripts)
            {
                try
                {
                    ScriptScanner.Scan(options.GameDir, scanModules, log);
                }
                catch (Exception e)
                {
                    log.Warn("Script scan failed: " + e);
                }
            }

            return result;
        }

        /// <summary>Returns the path of the API file SHVDN will effectively load, or null if none.</summary>
        private static string ProcessApi(Options options, Log log, int major, string apiPath, List<string> redirects)
        {
            string fileName = Path.GetFileName(apiPath);
            string backupPath = apiPath + BackupSuffix;

            if (!File.Exists(apiPath))
            {
                log.Info($"{fileName}: not found, nothing to do.");
                return null;
            }

            byte[] original = File.ReadAllBytes(apiPath);
            bool alreadyPatched = ShimInjector.IsPatched(ReadModule(original));

            switch (options.Mode)
            {
                case PatchMode.Off:
                    log.Info($"{fileName}: Mode=Off, left as is.");
                    return apiPath;

                case PatchMode.Restore:
                    if (alreadyPatched && File.Exists(backupPath))
                    {
                        File.Copy(backupPath, apiPath, true);
                        log.Info($"{fileName}: restored the original from {Path.GetFileName(backupPath)}.");
                    }
                    else
                    {
                        log.Info($"{fileName}: nothing to restore.");
                    }
                    return apiPath;

                case PatchMode.Disk:
                    if (alreadyPatched)
                    {
                        log.Info($"{fileName}: already patched on disk.");
                        return apiPath;
                    }
                    string cached = GetOrCreatePatched(options, log, major, fileName, original);
                    File.Copy(apiPath, backupPath, true);
                    File.Copy(cached, apiPath, true);
                    log.Info($"{fileName}: patched in place (original saved as {Path.GetFileName(backupPath)}).");
                    return apiPath;

                default:
                    if (alreadyPatched)
                    {
                        log.Info($"{fileName}: the file on disk is already patched (left over from Mode=Disk), no redirect needed.");
                        return apiPath;
                    }
                    string patched = GetOrCreatePatched(options, log, major, fileName, original);
                    redirects.Add(apiPath + "|" + patched);
                    log.Info($"{fileName}: SHVDN will be redirected to {patched}");
                    return patched;
            }
        }

        private static string GetOrCreatePatched(Options options, Log log, int major, string fileName, byte[] original)
        {
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = Hex(sha.ComputeHash(original)).Substring(0, 16);

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string cachePath = Path.Combine(options.CacheDir, $"{stem}.{hash}.{BuildId}.dll");

            if (File.Exists(cachePath))
            {
                try
                {
                    if (ShimInjector.IsPatched(ReadModule(File.ReadAllBytes(cachePath))))
                    {
                        log.Info($"{fileName}: using cached patched copy ({Path.GetFileName(cachePath)}).");
                        return cachePath;
                    }
                }
                catch (Exception e)
                {
                    log.Warn($"{fileName}: cached copy is unreadable, rebuilding. {e.Message}");
                }
            }

            log.Info($"{fileName}: building patched copy (sha256 {hash}...).");
            byte[] patched = PatchApi(original, major, $"ShvdnLegacyCompat {Version} build {BuildId}; source sha256 {hash}", out PatchReport report);
            foreach (string a in report.Added) log.Info($"  + {a}");
            foreach (string s in report.Skipped) log.Info($"  = {s}");
            log.Info($"{fileName}: {report.Added.Count} compatibility member(s) added.");

            string tmp = cachePath + ".tmp";
            File.WriteAllBytes(tmp, patched);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(tmp, cachePath);

            foreach (string stale in Directory.GetFiles(options.CacheDir, stem + ".*.dll"))
            {
                if (string.Equals(stale, cachePath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return cachePath;
        }

        /// <summary>Adds the shims for API version <paramref name="major"/> to <paramref name="original"/>.</summary>
        public static byte[] PatchApi(byte[] original, int major, string patchInfo, out PatchReport report)
        {
            report = new PatchReport();
            ModuleDefinition target = ReadModule(original);
            if (ShimInjector.IsPatched(target))
            {
                report.Skipped.Add("assembly is already patched");
                return original;
            }

            ModuleDefinition shims = ReadModule(ReadShimBytes(major));
            var injector = new ShimInjector(target, shims);
            injector.Run(patchInfo);
            report.Added.AddRange(injector.Added);
            report.Skipped.AddRange(injector.Skipped);

            using (var output = new MemoryStream())
            {
                target.Write(output);
                return output.ToArray();
            }
        }

        public static ModuleDefinition ReadModule(byte[] bytes)
        {
            return ModuleDefinition.ReadModule(new MemoryStream(bytes, false), new ReaderParameters
            {
                ReadingMode = ReadingMode.Immediate,
                InMemory = true,
                ReadSymbols = false,
            });
        }

        public static byte[] ReadShimBytes(int major)
        {
            string name = $"ShvdnCompat.Shims.V{major}.dll";
            using (Stream s = typeof(Engine).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException($"embedded resource {name} is missing");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void WriteAllTextAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
