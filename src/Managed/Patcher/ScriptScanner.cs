using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace ShvdnCompat
{
    /// <summary>
    /// Checks every script assembly against the API assemblies SHVDN will load and logs the API types and
    /// members a script uses that still do not exist. Those would surface in-game as TypeLoadException,
    /// MissingMethodException or MissingFieldException.
    /// </summary>
    public static class ScriptScanner
    {
        public static List<string> Scan(string gameDir, IDictionary<int, ModuleDefinition> apis, Log log)
        {
            string scriptsDir = GetScriptsDir(gameDir);
            var problems = new List<string>();
            if (!Directory.Exists(scriptsDir))
            {
                log.Info($"Scripts folder {scriptsDir} not found, nothing to scan.");
                return problems;
            }

            int scanned = 0, broken = 0;
            foreach (string file in Directory.GetFiles(scriptsDir, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ModuleDefinition module;
                try
                {
                    module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(file)), new ReaderParameters { ReadingMode = ReadingMode.Deferred });
                }
                catch (BadImageFormatException)
                {
                    continue; // native DLL
                }
                catch (Exception e)
                {
                    log.Warn($"[scan] {Path.GetFileName(file)}: could not be read ({e.Message}).");
                    continue;
                }

                List<string> missing = FindMissing(module, apis);
                if (missing == null) continue; // not a script (no API reference)
                scanned++;
                if (missing.Count == 0) continue;

                broken++;
                log.Warn($"[scan] {Path.GetFileName(file)} uses {missing.Count} API member(s) that the installed SHVDN does not provide, even with the compatibility layer:");
                foreach (string m in missing)
                {
                    log.Warn($"[scan]     {m}");
                    problems.Add($"{Path.GetFileName(file)}: {m}");
                }
            }

            log.Info(broken == 0
                ? $"[scan] {scanned} script assembly(ies) checked, all SHVDN API references resolve."
                : $"[scan] {scanned} script assembly(ies) checked, {broken} still reference missing API members (listed above). Please report them so shims can be added.");
            return problems;
        }

        /// <summary>Returns null when the module does not reference an SHVDN API assembly.</summary>
        public static List<string> FindMissing(ModuleDefinition script, IDictionary<int, ModuleDefinition> apis)
        {
            bool referencesApi = false;
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            foreach (TypeReference tr in script.GetTypeReferences())
            {
                int major = ApiMajor(tr);
                if (major == 0) continue;
                referencesApi = true;
                if (!apis.TryGetValue(major, out ModuleDefinition api)) continue;
                if (Signatures.FindType(api, tr) == null && !IsForwarded(api, tr))
                    missing.Add($"type {tr.FullName} [v{major}]");
            }

            foreach (MemberReference mr in script.GetMemberReferences())
            {
                TypeReference owner = mr.DeclaringType;
                if (owner is GenericInstanceType git) owner = git.ElementType;
                if (owner == null) continue;
                int major = ApiMajor(owner);
                if (major == 0 || !apis.TryGetValue(major, out ModuleDefinition api)) continue;

                TypeDefinition type = Signatures.FindType(api, owner);
                if (type == null) continue; // already reported as a missing type

                if (mr is MethodReference method)
                {
                    if (!HasMethod(type, Signatures.Of(method)))
                        missing.Add($"method {method.FullName} [v{major}]");
                }
                else if (mr is FieldReference field)
                {
                    if (!HasField(type, field))
                        missing.Add($"field {field.FullName} [v{major}]");
                }
            }

            return referencesApi ? missing.ToList() : null;
        }

        // MemberRefs are looked up on the named type first and then on its base types, like the CLR does.
        private static bool HasMethod(TypeDefinition type, string signature)
        {
            for (TypeDefinition t = type; t != null; t = BaseInSameModule(t))
            {
                if (Signatures.FindMethod(t, signature) != null) return true;
            }
            return false;
        }

        private static bool HasField(TypeDefinition type, FieldReference field)
        {
            string fieldType = Signatures.Of(field.FieldType);
            for (TypeDefinition t = type; t != null; t = BaseInSameModule(t))
            {
                if (t.Fields.Any(f => f.Name == field.Name && Signatures.Of(f.FieldType) == fieldType)) return true;
            }
            return false;
        }

        private static TypeDefinition BaseInSameModule(TypeDefinition type)
        {
            TypeReference b = type.BaseType;
            if (b == null) return null;
            if (b is GenericInstanceType git) b = git.ElementType;
            return b is TypeDefinition def ? def : (b.Scope == type.Module ? Signatures.FindType(type.Module, b) : null);
        }

        private static bool IsForwarded(ModuleDefinition api, TypeReference tr)
        {
            return api.ExportedTypes.Any(e => e.FullName == tr.FullName);
        }

        /// <summary>Mirrors SHVDN's ScriptDomain.HandleResolve: names starting with ScriptHookVDotNet map by major version, 0.0.0.0 to v2.</summary>
        private static int ApiMajor(TypeReference type)
        {
            while (type.DeclaringType != null) type = type.DeclaringType;
            if (!(type.Scope is AssemblyNameReference anr)) return 0;
            if (!anr.Name.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)) return 0;
            if (anr.Version == null || anr.Version == new Version(0, 0, 0, 0)) return 2;
            if (Signatures.IsApiAssemblyName(anr.Name, out int byName) && anr.Version.Major == 0) return byName;
            return anr.Version.Major;
        }

        private static string GetScriptsDir(string gameDir)
        {
            string location = "scripts";
            string ini = Path.Combine(gameDir, "ScriptHookVDotNet.ini");
            if (File.Exists(ini))
            {
                foreach (string line in File.ReadAllLines(ini))
                {
                    Match m = Regex.Match(line, @"^\s*ScriptsLocation\s*=\s*""?([^""]*)""?\s*$", RegexOptions.IgnoreCase);
                    if (m.Success && m.Groups[1].Value.Trim().Length > 0) location = m.Groups[1].Value.Trim();
                }
            }
            return Path.IsPathRooted(location) ? location : Path.Combine(gameDir, location);
        }
    }
}
