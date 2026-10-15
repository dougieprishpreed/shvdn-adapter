# ShvdnLegacyCompat

An ASI plugin for **GTA V Legacy** that lets scripts built against **older ScriptHookVDotNet (SHVDN) builds**
run on the **current SHVDN nightly** without `MissingMethodException`, `TypeLoadException` or
`MissingFieldException` errors.

It was inspired by [ScriptHook Compatibility Patch](https://www.gta5-mods.com/tools/scripthook-compatibility-patch),
which targets SHVDN *Enhanced*. This one is for the regular nightly on the Legacy game. It also works
differently: instead of renaming metadata inside your script files, it **adds the removed API members back to
SHVDN's API assembly as small forwarding shims**. Because of that, it also handles the cases a rename can't,
such as changed parameter lists or return types.

Target setup: SHVDN **v3.7.0-nightly.189** installed, with scripts built for **v3.7.0-nightly.26** or any
**stable 3.0.0–3.6.0 / 2.10.x–2.11.6** release.

## Install

Requirements: Script Hook V (with its `dinput8.dll` ASI loader), SHVDN nightly, .NET Framework 4.8.

1. Copy `dist/ShvdnLegacyCompat.asi` (and optionally `dist/ShvdnLegacyCompat.ini`) into the GTA V folder,
   next to `GTA5.exe` and `ScriptHookVDotNet.asi`.
2. Start the game. On first start, the plugin creates a `ShvdnLegacyCompat\` folder and the `.ini` (if it is
   missing).
3. Check `ShvdnLegacyCompat\ShvdnLegacyCompat.log`. It lists every member that was added, and the startup
   script scan lists any script that still uses something missing.

To uninstall, delete `ShvdnLegacyCompat.asi`, `ShvdnLegacyCompat.ini` and the `ShvdnLegacyCompat\` folder.
With the default `Mode=Redirect`, nothing else was changed.

## What gets fixed

v3 API (`ScriptHookVDotNet3.dll`). Old nightly.26 → nightly.189:

| Old member scripts call | Shim behavior on nightly.189 |
|---|---|
| type `GTA.FeedPost` | Re-added (it moved to `GTA.UI.FeedPost`): `Handle`, `Delete()`, `==`/`!=`, `Equals`, `GetHashCode`, implicit `InputArgument` |
| `GTA.FeedPost Notification.PostTicker / PostTickerForced / PostTickerWithTokens / PostMessageText / PostUnlock / PostUnlockTitleUpdate / PostUnlockTitleUpdateWithColor / PostVersusTitleUpdate / PostAward(...)` | Calls the current method and wraps the `GTA.UI.FeedPost` it returns |
| `Camera.InterpTo(Camera, int, curve, curve, EulerRotationOrder)` | Calls the 4-argument overload (the native never used the extra argument) |
| `CamViewMode GameplayCamera.SetCamViewModeForContext(...)` (now `void`) | Sets the mode, then returns the current mode |
| `TaskInvoker.PlaneChase(Vehicle, Vector3, Vector3)` | `PlaneChase(Entity, Vector3)`, which makes the same native call as before |
| `Vehicle.SetSpecialFlightModeAllowed(bool)` | Sets `Vehicle.SpecialFlightModeAllowed` |
| `WeaponCollection.IsWeaponValid(string, bool)` | `WeaponCollection.Select(string, bool)` (it was renamed, same behavior) |

v3 API. Stable 3.0.0–3.6.0 → nightly.189: `GameplayCamera.ClampYaw/ClampPitch`, `WeaponAsset.LocalizedName`,
`World.CreateRandomVehicle(Vector3, float)`.

v2 API (`ScriptHookVDotNet2.dll`). Old C++/CLI 2.10.3–2.10.10 → current: `Player.RemainingSprintTime` /
`RemainingUnderwaterTime` (int), `Quaternion / float`, `NaturalMotion.Message.SetArgument(string, double)`, the
`int` setters of the NaturalMotion helpers (`AnimSource`, `AdaptiveMode`, `MirrorMode`, `FallType`,
`RbTwistAxis`, `Torque*Mode`, `GunHandEnum`, `TurnToTarget`, `Arms2LegsSync`, `Hand`, `WeaponMode`) and
`ConfigureConstraintsHelper.BespokeBehaviour`. The 2.11.6 v2 API from your old install is identical to the
nightly's, so v2 scripts that already worked keep working.

The lists come from a member-by-member diff of the public API of your old SHVDN, every stable SHVDN v2/v3
release on NuGet, and nightly.189. The test suite checks that every public method and field of those builds
exists in the patched nightly.

Some differences are compile-time only and need no shim, for example `GameVersion.V1_0_xxxx` → `v1_0_xxxx`
and the renumbered `ParachuteLandingType` values. Enum constants are compiled into the script, and both SHVDN
builds return the game's raw value.

## How it works

```
GTA5.exe start
 └─ ShvdnLegacyCompat.asi (loaded by Script Hook V's ASI loader)
     ├─ hooks ntdll!NtCreateFile/NtOpenFile (read-only opens of <game>\ScriptHookVDotNet{2,3}.dll only)
     ├─ cache hit (same SHVDN files, same plugin build)? → redirect immediately (no .NET involved)
     ├─ otherwise runs ShvdnLegacyCompat\bin\ShvdnCompat.Patcher.exe (separate process):
     │    reads the API DLL with Mono.Cecil, clones the shims into it, writes
     │    ShvdnLegacyCompat\cache\ScriptHookVDotNet3.<sha256>.<build>.dll
     └─ when SHVDN does Assembly.LoadFrom(".\ScriptHookVDotNet3.dll"), it gets the patched copy
```

- Only members the installed API lacks are added. Existing members are never changed, and the test suite
  checks that the IL of every original method is identical after patching. When a future nightly re-adds
  something, the shim is skipped.
- The patched assembly keeps the same name and version (3.7.0.189), so scripts built for the nightly keep
  working, and SHVDN's own version checks are unaffected.
- The patcher runs out of process, so it never shares the CLR (or its loader locks) with SHVDN.
- If SHVDN loads its API while the patcher is still building the first copy, the load waits for it, up to
  `WaitTimeoutMs`. After that, the copy is served from the cache immediately.
- When you install a different SHVDN nightly, the hash changes and a new copy is built automatically.

## Settings (`ShvdnLegacyCompat.ini`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Turn the plugin off without deleting it |
| `Mode` | `Redirect` | `Redirect`: in-memory redirect, game folder untouched. `Disk`: write the patched API over `ScriptHookVDotNet2/3.dll`, keeping `*.legacycompat.bak` backups; use this if Redirect does not work on your PC. `Restore`: put the backups back. `Off`: patch nothing, only scan |
| `PatchV2` / `PatchV3` | `true` | Which API DLLs to patch |
| `ScanScripts` | `true` | Log scripts that still reference missing API members |
| `WaitTimeoutMs` | `20000` | First-start only: how long SHVDN's API load may wait for the patcher |

## Troubleshooting

- **A script still fails.** Look for `[scan]` lines in the log. They name the exact missing members, and a
  shim can be added for them (see *Adding shims*).
- **The log says the hooks could not be installed** (another tool already hooked `NtCreateFile`). The plugin
  falls back to hooking `clr.dll`'s `CreateFileW`. If that fails too, set `Mode=Disk`.
- **After updating SHVDN in Disk mode:** the new files are detected and patched again on the next start, and
  the backup is refreshed.
- **Stack traces from SHVDN's own frames lose line numbers.** The patched API has no matching `.pdb`. Your
  scripts' line numbers are unaffected.
- **Scripts that use reflection to look up `Notification.PostTicker` (and the other `Post*` methods) by name
  only.** These methods now exist twice, differing only by return type, so a name-only lookup is ambiguous.
  Compiled calls are not affected.

## Building

On Linux, WSL or MSYS2 with the .NET 8 SDK and MinGW-w64 (`x86_64-w64-mingw32-g++`, `-windres`):

```
./build.sh          # -> build/ShvdnLegacyCompat.asi
./build.sh --test   # also runs the managed tests and builds the native test programs
tests/native/run_wine_tests.sh   # hook harness + end-to-end runs under wine64 (use a prefix with `winetricks dotnet48`)
```

Layout:

- `src/Native`: the ASI (worker thread, ntdll hooks, cache fast path, patcher launch).
- `src/Managed/Patcher`: `ShvdnCompat.Patcher.exe` (Cecil shim injector, cache, script scanner).
- `src/Managed/Shims.V3`, `Shims.V2`: the shims, as ordinary C# compiled against the nightly API.
- `tests/ShvdnCompat.Tests`: API-coverage, IL-identity, ILVerify and engine tests.
- `tests/native`: the redirect harness, plus an end-to-end host that plays SHVDN and binds every member
  reference of `tests/LegacyScriptFixture`, a script compiled against the old API.
- `ref/`: the SHVDN API builds used for compiling and testing (zlib license, see `ref/SHVDN-LICENSE.txt`).

### Adding shims

Add a method to a `[ShimType("GTA.SomeType")]` class in `src/Managed/Shims.V3` (or `.V2`):

```csharp
[ShimType("GTA.Vehicle")]
internal static class VehicleShims
{
    // instance member: first parameter becomes `this` (use `ref T` for structs)
    [Shim(Instance = true)]
    public static void SetSpecialFlightModeAllowed(Vehicle self, bool allowed) => self.SpecialFlightModeAllowed = allowed;
}
```

For a removed type, use `[InjectType("GTA.Name")]` on a class. Shims may only call public API, BCL code, other
shims, or injected types. Shims that can't be mapped are skipped and logged. They never break the patch.

## Status

Tested working in-game.

The plugin is also verified outside the game: unit and IL tests on the real SHVDN binaries, plus end-to-end runs of
the compiled ASI under Wine with **the real .NET Framework 4.8 runtime** (installed with `winetricks dotnet48`)
and with wine-mono. In those runs, an SHVDN-style script domain loads the API exactly the way SHVDN does. A
script built against the old API then binds all 25 of its API member references with the plugin, and 14 of
them fail without it.
