// End-to-end check of ShvdnLegacyCompat.asi outside the game: plays the part of GTA5.exe + SHVDN.
//   e2e_host.exe            -> loads the ASI, waits for it, then runs BindingChecker (SHVDN-like LoadFrom)
//   e2e_host.exe --no-asi   -> same without the ASI (baseline: the legacy script must fail to bind)
//   e2e_host.exe --no-wait  -> loads the ASI and binds immediately (SHVDN racing the ASI's startup)
// Expects next to itself: ShvdnLegacyCompat.asi, BindingChecker.dll, ScriptHookVDotNet{2,3}.dll,
// scripts\LegacyScriptFixture.dll.
#include "clr_host.h"

#include <windows.h>

#include <cstdio>
#include <string>

namespace
{
    std::wstring ExeDir()
    {
        wchar_t path[MAX_PATH];
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        std::wstring dir(path);
        return dir.substr(0, dir.find_last_of(L'\\'));
    }

    bool LogContains(const std::wstring& logPath, const char* needle)
    {
        HANDLE h = CreateFileW(logPath.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        std::string text(1 << 20, '\0');
        DWORD read = 0;
        ReadFile(h, text.data(), static_cast<DWORD>(text.size()), &read, nullptr);
        CloseHandle(h);
        text.resize(read);
        return text.find(needle) != std::string::npos;
    }
}

int wmain(int argc, wchar_t** argv)
{
    const bool useAsi = !(argc > 1 && wcscmp(argv[1], L"--no-asi") == 0);
    const bool waitForAsi = !(argc > 1 && wcscmp(argv[1], L"--no-wait") == 0);
    const std::wstring dir = ExeDir();
    SetCurrentDirectoryW(dir.c_str());

    if (useAsi)
    {
        // Wait conditions below look at this session's log only.
        DeleteFileW((dir + L"\\ShvdnLegacyCompat\\ShvdnLegacyCompat.log").c_str());
        if (!LoadLibraryW((dir + L"\\ShvdnLegacyCompat.asi").c_str()))
        {
            std::printf("FAIL could not load the ASI (error %lu)\n", GetLastError());
            return 2;
        }
        const std::wstring log = dir + L"\\ShvdnLegacyCompat\\ShvdnLegacyCompat.log";
        DWORD start = GetTickCount();
        while (waitForAsi && !LogContains(log, "Redirect targets ready") && !LogContains(log, "Patcher task Patch finished") && GetTickCount() - start < 300000)
        {
            Sleep(250);
        }
        std::printf("ASI ready after %lu ms\n", GetTickCount() - start);
    }

    DWORD failures = 0;
    std::wstring error;
    if (!compat::clr::ExecuteInDefaultDomain(dir + L"\\BindingChecker.dll", L"E2E.Checker", L"Run", dir, failures, error))
    {
        std::printf("FAIL could not run the checker: %ls\n", error.c_str());
        return 3;
    }
    std::printf("binding failures: %lu\n", failures);
    return static_cast<int>(failures);
}
