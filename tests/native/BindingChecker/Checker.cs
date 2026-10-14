using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Mimics what SHVDN does at startup (new AppDomain, Assembly.LoadFrom of .\ScriptHookVDotNet3.dll, then
// LoadFrom of the scripts) and asks the runtime to bind every MemberRef of a legacy script. Run by
// e2e_host.exe through ExecuteInDefaultAppDomain; results go to <gameDir>\binding-result.txt.
namespace E2E
{
public static class Checker
{
    public static int Run(string gameDir)
    {
        // Same setup as SHVDN.ScriptDomain.Load.
        string scripts = Path.Combine(gameDir, "scripts");
        var domain = AppDomain.CreateDomain("SHVDN_ScriptDomain_Test", null, new AppDomainSetup
        {
            ShadowCopyFiles = "true",
            ShadowCopyDirectories = scripts,
            ApplicationBase = scripts,
        });
        try
        {
            var remote = (Remote)domain.CreateInstanceFromAndUnwrap(typeof(Checker).Assembly.Location, typeof(Remote).FullName);
            return remote.Check(gameDir);
        }
        finally
        {
            AppDomain.Unload(domain);
        }
    }
}

public sealed class Remote : MarshalByRefObject
{
    public int Check(string gameDir)
    {
        var report = new StringBuilder();
        // Same call SHVDN makes; the host sets the current directory to the game folder like GTA5.exe does.
        Assembly api = Assembly.LoadFrom(@".\ScriptHookVDotNet3.dll");
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            new AssemblyName(e.Name).Name == "ScriptHookVDotNet3" ? api : null;

        report.AppendLine("api: " + api.FullName);
        report.AppendLine("api has GTA.FeedPost: " + (api.GetType("GTA.FeedPost") != null));
        report.AppendLine("api has compat marker: " + (api.GetType("ShvdnLegacyCompat.PatchInfo") != null));

        Assembly script = Assembly.LoadFrom(Path.Combine(gameDir, "scripts", "LegacyScriptFixture.dll"));
        int failures = 0, resolved = 0;
        for (int row = 1; ; row++)
        {
            int token = 0x0A000000 | row; // MemberRef table
            MemberInfo member;
            try
            {
                member = script.ManifestModule.ResolveMember(token);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }
            catch (Exception e)
            {
                failures++;
                report.AppendLine($"UNRESOLVED 0x{token:X8}: {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (member.Module.Assembly == api)
            {
                resolved++;
                report.AppendLine($"ok {member.DeclaringType}::{member}");
            }
        }
        report.AppendLine($"resolved API members: {resolved}, failures: {failures}");
        File.WriteAllText(Path.Combine(gameDir, "binding-result.txt"), report.ToString());
        return failures;
    }
}
}
