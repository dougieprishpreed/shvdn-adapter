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
            switch (options.Task)
            {
                case EngineTask.Patch:
                    return Patch(options, log);
                case EngineTask.Scan:
                    return Scan(options, log);
                default:
                    int result = Patch(options, log);
                    Scan(options, log);
                    return result;
            }
        }

        /// <summary>Creates the patched API files and writes redirects.txt and the cache index for the ASI.</summary>
        public static int Patch(Options options, Log log)
        {
            log.Info($"ShvdnLegacyCompat patcher {Version} (build {BuildId}), mode={options.Mode}, game folder: {options.GameDir}");
            Directory.CreateDirectory(options.DataDir);
            Directory.CreateDirectory(options.CacheDir);

            var redirects = new List<string>();
            var index = new List<string>();
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
                    var redirect = ProcessApi(options, log, major, apiPath);
                    if (redirect != null)
                    {
                        redirects.Add(apiPath + "|" + redirect.Value.patched);
                        index.Add($"{major}|{redirect.Value.sha256}|{redirect.Value.patched}|{options.Fingerprint}");
                    }
                }
                catch (Exception e)
                {
                    result = 2;
                    log.Error($"ScriptHookVDotNet{major}.dll: patching failed, SHVDN will use the unmodified file. {e}");
                }
            }

            WriteAllTextAtomic(options.RedirectFile, string.Join("\n", redirects) + "\n");
            WriteAllTextAtomic(options.CacheIndexFile, string.Join("\n", index) + "\n");
            return result;
        }

        /// <summary>Checks the scripts against the API files SHVDN is going to load (patched where redirected).</summary>
        public static int Scan(Options options, Log log)
        {
            if (!options.ScanScripts) return 0;

            var redirected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(options.RedirectFile))
            {
                foreach (string line in File.ReadAllLines(options.RedirectFile))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length == 2) redirected[parts[0]] = parts[1];
                }
            }

            try
            {
                var apis = new Dictionary<int, ModuleDefinition>();
                foreach (int major in new[] { 2, 3 })
                {
                    string apiPath = Path.Combine(options.GameDir, $"ScriptHookVDotNet{major}.dll");
                    string effective = redirected.TryGetValue(apiPath, out string patched) ? patched : apiPath;
                    if (File.Exists(effective)) apis[major] = ReadModule(File.ReadAllBytes(effective));
                }
                ScriptScanner.Scan(options.GameDir, apis, log);
                return 0;
            }
            catch (Exception e)
            {
                log.Warn("Script scan failed: " + e);
                return 1;
            }
        }

        /// <summary>Returns the redirect (patched copy + hash of the original) SHVDN should get, or null for none.</summary>
        private static (string patched, string sha256)? ProcessApi(Options options, Log log, int major, string apiPath)
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
                    return null;

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
                    return null;

                case PatchMode.Disk:
                    if (alreadyPatched)
                    {
                        log.Info($"{fileName}: already patched on disk.");
                        return null;
                    }
                    string cached = GetOrCreatePatched(options, log, major, fileName, original, out _);
                    File.Copy(apiPath, backupPath, true);
                    File.Copy(cached, apiPath, true);
                    log.Info($"{fileName}: patched in place (original saved as {Path.GetFileName(backupPath)}).");
                    return null;

                default:
                    if (alreadyPatched)
                    {
                        log.Info($"{fileName}: the file on disk is already patched (left over from Mode=Disk), no redirect needed.");
                        return null;
                    }
                    string patched = GetOrCreatePatched(options, log, major, fileName, original, out string sha256);
                    log.Info($"{fileName}: SHVDN will be redirected to {patched}");
                    return (patched, sha256);
            }
        }

        private static string GetOrCreatePatched(Options options, Log log, int major, string fileName, byte[] original, out string sha256)
        {
            using (SHA256 sha = SHA256.Create()) sha256 = Hex(sha.ComputeHash(original));
            string hash = sha256.Substring(0, 16);

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
