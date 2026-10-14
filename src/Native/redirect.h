#pragma once

#include <string>

namespace compat::redirect
{
    // Hooks file opens so read-only opens of <gameDir>\ScriptHookVDotNet{2,3}.dll can be served from the
    // patched copies. Until Ready() is called, such opens wait up to `waitTimeoutMs` for the patcher, so SHVDN
    // does not pick up the unpatched file by racing it. The calling thread (which runs the patcher) is never
    // redirected and never waits.
    bool Install(const std::wstring& gameDir, unsigned long waitTimeoutMs);

    // Sets the patched file for one original path (as written by the patcher to redirects.txt).
    bool SetTarget(const std::wstring& originalPath, const std::wstring& patchedPath);

    // Releases waiting threads; from now on opens are redirected to the targets set so far.
    void Ready();
    bool IsReady();

    // Fallback when the ntdll hooks could not be installed: patches clr.dll's CreateFileW import instead.
    bool InstallClrImportHook();

    bool NtHooksInstalled();
}
