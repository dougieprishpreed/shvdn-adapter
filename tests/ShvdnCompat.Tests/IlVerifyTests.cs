using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ILVerify;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace ShvdnCompat.Tests
{
    /// <summary>Runs ILVerify over every method the patcher added, against the .NET Framework 4.8 reference set.</summary>
    public class IlVerifyTests
    {
        private readonly ITestOutputHelper _out;

        public IlVerifyTests(ITestOutputHelper output) => _out = output;

        private sealed class Resolver : IResolver
        {
            private readonly Dictionary<string, PEReader> _cache = new Dictionary<string, PEReader>();
            private readonly Dictionary<string, byte[]> _overrides;

            public Resolver(Dictionary<string, byte[]> overrides) => _overrides = overrides;

            public PEReader ResolveAssembly(AssemblyName assemblyName) => Resolve(assemblyName.Name);

            public PEReader ResolveModule(AssemblyName referencingAssembly, string fileName) => null;

            private PEReader Resolve(string name)
            {
                if (_cache.TryGetValue(name, out PEReader cached)) return cached;
                PEReader reader = null;
                if (_overrides.TryGetValue(name, out byte[] bytes))
                {
                    reader = new PEReader(new MemoryStream(bytes));
                }
                else
                {
                    foreach (string candidate in new[]
                    {
                        Path.Combine(TestPaths.NetFxRefDir, name + ".dll"),
                        Path.Combine(TestPaths.NetFxRefDir, "Facades", name + ".dll"),
                    })
                    {
                        if (File.Exists(candidate))
                        {
                            reader = new PEReader(File.OpenRead(candidate));
                            break;
                        }
                    }
                }
                _cache[name] = reader;
                return reader;
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void InjectedMethodsVerify(int major)
        {
            byte[] patched = PatchTests.Patched(major);
            ModuleDefinition original = ModuleDefinition.ReadModule(TestPaths.NightlyApi(major));
            ModuleDefinition patchedModule = Engine.ReadModule(patched);

            var injected = new List<Mono.Cecil.MethodDefinition>();
            foreach (TypeDefinition t in patchedModule.GetTypes())
            {
                TypeDefinition ot = Signatures.FindType(original, t.FullName);
                foreach (Mono.Cecil.MethodDefinition m in t.Methods)
                {
                    if (m.HasBody && (ot == null || Signatures.FindMethod(ot, Signatures.Of(m)) == null)) injected.Add(m);
                }
            }
            Assert.NotEmpty(injected);

            var resolver = new Resolver(new Dictionary<string, byte[]>
            {
                [$"ScriptHookVDotNet{major}"] = patched,
                ["ScriptHookVDotNet"] = File.ReadAllBytes(TestPaths.ShvdnAsi),
            });
            var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
            verifier.SetSystemModuleName(new AssemblyName("mscorlib"));

            PEReader reader = resolver.ResolveAssembly(new AssemblyName($"ScriptHookVDotNet{major}"));
            var errors = new List<string>();
            foreach (Mono.Cecil.MethodDefinition m in injected)
            {
                var handle = MetadataTokens.MethodDefinitionHandle((int)m.MetadataToken.RID);
                foreach (VerificationResult r in verifier.Verify(reader, handle))
                {
                    errors.Add($"{m.FullName}: {r.Code} {r.Message}");
                }
            }

            _out.WriteLine($"verified {injected.Count} injected method(s)");
            foreach (string e in errors) _out.WriteLine(e);
            Assert.Empty(errors);
        }
    }
}
