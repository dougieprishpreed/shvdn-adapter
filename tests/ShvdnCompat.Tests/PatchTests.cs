using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace ShvdnCompat.Tests
{
    public class PatchTests
    {
        private readonly ITestOutputHelper _out;

        public PatchTests(ITestOutputHelper output) => _out = output;

        internal static byte[] Patched(int major) =>
            Engine.PatchApi(File.ReadAllBytes(TestPaths.NightlyApi(major)), major, "tests", out _);

        private static bool Visible(TypeDefinition t)
        {
            for (TypeDefinition c = t; c != null; c = c.DeclaringType)
                if (!(c.IsPublic || c.IsNestedPublic || c.IsNestedFamily || c.IsNestedFamilyOrAssembly)) return false;
            return true;
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void PatchedNightlyContainsEveryLegacyMember(int major)
        {
            ModuleDefinition patched = Engine.ReadModule(Patched(major));
            var legacyApis = new List<string> { TestPaths.LegacyApi(major) };
            legacyApis.AddRange(TestPaths.ExtraLegacyApis(major));

            var missing = new SortedSet<string>();
            foreach (string legacyPath in legacyApis)
            {
                ModuleDefinition legacy = ModuleDefinition.ReadModule(legacyPath);
                foreach (TypeDefinition t in legacy.GetTypes().Where(Visible))
                {
                    TypeDefinition pt = Signatures.FindType(patched, t.FullName);
                    if (pt == null)
                    {
                        missing.Add($"type {t.FullName}");
                        continue;
                    }

                    foreach (MethodDefinition m in t.Methods.Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
                    {
                        // C++/CLI-only artifacts of the 2.10.x builds that compiled C# code can never reference.
                        if (m.Name.StartsWith("raise_") || (m.Name == "Dispose" && m.IsFamily)) continue;
                        if (m.Parameters.Any(p => p.ParameterType.FullName.Contains("modopt(")) || m.ReturnType.FullName.Contains("modopt(")) continue;

                        if (Signatures.FindMethod(pt, Signatures.Of(m)) == null)
                            missing.Add($"{Path.GetFileName(Path.GetDirectoryName(legacyPath))}: {m.FullName}");
                    }

                    // Enum constants are compiled into scripts and value__ is never referenced, so only real public
                    // fields matter at runtime. (The C++/CLI 2.10.x builds also exposed protected storage fields
                    // of sealed helper types, which no script can reach.)
                    // PedGroup/enumerator is the C++/CLI iterator state behind foreach; scripts only see IEnumerator<Ped>.
                    if (t.FullName == "GTA.PedGroup/enumerator") continue;
                    foreach (FieldDefinition f in t.Fields.Where(f => f.IsPublic && !f.IsLiteral && f.Name != "value__"))
                    {
                        if (!pt.Fields.Any(pf => pf.Name == f.Name && Signatures.Of(pf.FieldType) == Signatures.Of(f.FieldType)))
                            missing.Add($"{Path.GetFileName(Path.GetDirectoryName(legacyPath))}: {f.FullName}");
                    }
                }
            }

            foreach (string m in missing) _out.WriteLine(m);
            Assert.Empty(missing);
        }

        [Fact]
        public void LegacyScriptBreaksOnNightlyAndResolvesOnPatched()
        {
            ModuleDefinition script = ModuleDefinition.ReadModule(TestPaths.FixtureDll);

            List<string> onNightly = ScriptScanner.FindMissing(script, new Dictionary<int, ModuleDefinition>
            {
                [3] = ModuleDefinition.ReadModule(TestPaths.NightlyApi(3)),
            });
            foreach (string m in onNightly) _out.WriteLine("nightly: " + m);
            Assert.Contains(onNightly, m => m.Contains("PostTicker"));
            Assert.Contains(onNightly, m => m.Contains("type GTA.FeedPost"));
            Assert.Contains(onNightly, m => m.Contains("SetSpecialFlightModeAllowed"));
            Assert.Contains(onNightly, m => m.Contains("IsWeaponValid"));

            List<string> onPatched = ScriptScanner.FindMissing(script, new Dictionary<int, ModuleDefinition>
            {
                [3] = Engine.ReadModule(Patched(3)),
            });
            foreach (string m in onPatched) _out.WriteLine("patched: " + m);
            Assert.NotNull(onPatched);
            Assert.Empty(onPatched);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void PatchingIsIdempotent(int major)
        {
            byte[] once = Patched(major);
            byte[] twice = Engine.PatchApi(once, major, "tests", out PatchReport report);
            Assert.Same(once, twice);
            Assert.Empty(report.Added);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void OriginalMembersAreUntouched(int major)
        {
            ModuleDefinition original = ModuleDefinition.ReadModule(TestPaths.NightlyApi(major));
            ModuleDefinition patched = Engine.ReadModule(Patched(major));

            Assert.Equal(original.Assembly.Name.FullName, patched.Assembly.Name.FullName);
            Assert.Equal(original.Architecture, patched.Architecture);
            Assert.Equal(original.Attributes, patched.Attributes);

            foreach (TypeDefinition t in original.GetTypes())
            {
                TypeDefinition pt = Signatures.FindType(patched, t.FullName);
                Assert.NotNull(pt);
                foreach (MethodDefinition m in t.Methods)
                {
                    MethodDefinition pm = Signatures.FindMethod(pt, Signatures.Of(m));
                    Assert.NotNull(pm);
                    Assert.Equal(m.Attributes, pm.Attributes);
                    if (m.HasBody)
                        Assert.Equal(Il(m), Il(pm));
                }
            }
        }

        private static string Il(MethodDefinition m) =>
            string.Join("\n", m.Body.Instructions.Select(i => i.OpCode.Name + " " + (i.Operand is Mono.Cecil.Cil.Instruction t ? t.Offset.ToString() : i.Operand?.ToString())));

        [Fact]
        public void PatchingTheLegacyBuildOnlyAddsWhatIsMissing()
        {
            byte[] legacy = File.ReadAllBytes(TestPaths.LegacyApi(3));
            byte[] patched = Engine.PatchApi(legacy, 3, "tests", out PatchReport report);
            foreach (string a in report.Added) _out.WriteLine("+ " + a);
            foreach (string s in report.Skipped) _out.WriteLine("= " + s);

            ModuleDefinition module = Engine.ReadModule(patched);
            Assert.Single(module.Types, t => t.FullName == "GTA.FeedPost");
            Assert.DoesNotContain(report.Added, a => a.Contains("PostTicker"));
            Assert.Contains(report.Added, a => a.Contains("ClampYaw"));
        }
    }
}
