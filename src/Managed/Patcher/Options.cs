using System;
using System.IO;

namespace ShvdnCompat
{
    public enum PatchMode
    {
        /// <summary>Write patched API copies to the cache and let the ASI redirect SHVDN to them (default).</summary>
        Redirect,
        /// <summary>Overwrite the API DLLs in the game folder (originals are backed up).</summary>
        Disk,
        /// <summary>Put the original API DLLs back from the Disk-mode backups.</summary>
        Restore,
        /// <summary>Do not patch anything; only scan scripts if enabled.</summary>
        Off,
    }

    /// <summary>Options passed from the native ASI as "key=value" lines.</summary>
    public sealed class Options
    {
        public string GameDir { get; set; }
        public string DataDir { get; set; }
        public PatchMode Mode { get; set; } = PatchMode.Redirect;
        public bool PatchV2 { get; set; } = true;
        public bool PatchV3 { get; set; } = true;
        public bool ScanScripts { get; set; } = true;
        public string LogFile { get; set; }
        public string RedirectFile { get; set; }

        public string CacheDir => Path.Combine(DataDir, "cache");

        public static Options Parse(string text)
        {
            var o = new Options();
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "gamedir": o.GameDir = value; break;
                    case "datadir": o.DataDir = value; break;
                    case "mode": o.Mode = ParseMode(value); break;
                    case "patchv2": o.PatchV2 = ParseBool(value, true); break;
                    case "patchv3": o.PatchV3 = ParseBool(value, true); break;
                    case "scanscripts": o.ScanScripts = ParseBool(value, true); break;
                    case "logfile": o.LogFile = value; break;
                    case "redirectfile": o.RedirectFile = value; break;
                }
            }
            if (string.IsNullOrEmpty(o.GameDir)) throw new ArgumentException("GameDir is required");
            if (string.IsNullOrEmpty(o.DataDir)) o.DataDir = Path.Combine(o.GameDir, "ShvdnLegacyCompat");
            if (string.IsNullOrEmpty(o.RedirectFile)) o.RedirectFile = Path.Combine(o.DataDir, "redirects.txt");
            return o;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            switch (value.ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default: return fallback;
            }
        }

        private static PatchMode ParseMode(string value)
        {
            return Enum.TryParse(value, true, out PatchMode mode) ? mode : PatchMode.Redirect;
        }
    }
}
