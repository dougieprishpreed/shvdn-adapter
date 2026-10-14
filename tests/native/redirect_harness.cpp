// Exercises the file redirection hooks outside the game (runs on Windows or under Wine):
//   x86_64-w64-mingw32-g++ -std=c++17 -static -DUNICODE -D_UNICODE -Isrc/Native tests/native/redirect_harness.cpp \
//     src/Native/redirect.cpp src/Native/log.cpp -o build/redirect_harness.exe
#include "log.h"
#include "redirect.h"

#include <windows.h>
#include <winternl.h>

#include <cstdio>
#include <string>

namespace
{
    int g_failures = 0;

    void Check(bool ok, const char* what)
    {
        std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
        if (!ok)
        {
            ++g_failures;
        }
    }

    void WriteText(const std::wstring& path, const char* text)
    {
        HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        DWORD written = 0;
        WriteFile(h, text, static_cast<DWORD>(strlen(text)), &written, nullptr);
        CloseHandle(h);
    }

    std::string ReadHandle(HANDLE h)
    {
        if (h == INVALID_HANDLE_VALUE || h == nullptr)
        {
            return "<open failed>";
        }
        char buf[64] = {};
        DWORD read = 0;
        ReadFile(h, buf, sizeof(buf) - 1, &read, nullptr);
        CloseHandle(h);
        return std::string(buf, read);
    }

    std::string ReadVia(const std::wstring& path, DWORD access = GENERIC_READ)
    {
        return ReadHandle(CreateFileW(path.c_str(), access, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
    }

    std::wstring g_original;
    std::wstring g_patched;
    std::string g_earlyResult;
    std::string g_workerBefore;
    std::string g_workerAfter;
    bool g_installOk = false;
    bool g_setTargetOk = false;
    HANDLE g_go = nullptr;
    HANDLE g_installed = nullptr;

    DWORD WINAPI EarlyReader(LPVOID)
    {
        // Opens the API before Ready(): must block, then get the patched file.
        g_earlyResult = ReadVia(g_original);
        return 0;
    }
}

DWORD WINAPI InstallerThread(LPVOID)
{
    std::wstring game = g_original.substr(0, g_original.find_last_of(L'\\'));
    g_installOk = compat::redirect::Install(game, 5000);
    g_workerBefore = ReadVia(g_original);
    SetEvent(g_installed);
    WaitForSingleObject(g_go, INFINITE);
    g_setTargetOk = compat::redirect::SetTarget(g_original, g_patched);
    compat::redirect::Ready();
    g_workerAfter = ReadVia(g_original);
    return 0;
}

int wmain()
{
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring game = std::wstring(tmp) + L"shvdncompat-harness";
    CreateDirectoryW(game.c_str(), nullptr);
    CreateDirectoryW((game + L"\\cache").c_str(), nullptr);
    g_original = game + L"\\ScriptHookVDotNet3.dll";
    std::wstring patched = game + L"\\cache\\ScriptHookVDotNet3.patched.dll";
    WriteText(g_original, "ORIGINAL");
    WriteText(patched, "PATCHED");
    WriteText(game + L"\\ScriptHookVDotNet2.dll", "ORIGINAL2");

    compat::log::Init(game + L"\\harness.log");

    // The ASI installs the hooks and runs the patcher on its own worker thread; mimic that.
    g_patched = patched;
    g_go = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_installed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE worker = CreateThread(nullptr, 0, InstallerThread, nullptr, 0, nullptr);
    WaitForSingleObject(g_installed, 5000);
    Check(g_installOk, "ntdll hooks installed");
    if (!g_installOk)
    {
        return 1;
    }

    HANDLE early = CreateThread(nullptr, 0, EarlyReader, nullptr, 0, nullptr);
    Sleep(500);
    Check(WaitForSingleObject(early, 0) == WAIT_TIMEOUT, "other thread blocks until Ready");

    SetEvent(g_go); // worker: SetTarget + Ready, then reads the original itself
    WaitForSingleObject(worker, 5000);
    Check(g_setTargetOk, "SetTarget accepts the original path");
    Check(g_workerBefore == "ORIGINAL", "worker thread reads the original before Ready");
    Check(g_workerAfter == "ORIGINAL", "worker thread is never redirected (patcher must see the real file)");

    WaitForSingleObject(early, 5000);
    Check(g_earlyResult == "PATCHED", "blocked thread gets the patched file after Ready");

    Check(ReadVia(g_original) == "PATCHED", "CreateFileW(full path) is redirected");

    std::wstring upper = g_original;
    for (auto& c : upper)
    {
        c = towupper(c);
    }
    Check(ReadVia(upper) == "PATCHED", "case-insensitive match");

    SetCurrentDirectoryW(game.c_str());
    Check(ReadVia(L".\\ScriptHookVDotNet3.dll") == "PATCHED", "relative path (.\\ScriptHookVDotNet3.dll) is redirected");
    Check(ReadVia(L"\\\\?\\" + g_original) == "PATCHED", "\\\\?\\ prefixed path is redirected");

    Check(ReadVia(g_original, GENERIC_READ | GENERIC_WRITE) == "ORIGINAL", "write access is not redirected");
    Check(ReadVia(game + L"\\ScriptHookVDotNet2.dll") == "ORIGINAL2", "API without a target is untouched");
    Check(ReadVia(patched) == "PATCHED", "unrelated files open normally");

    // Raw NtOpenFile, the path the OS loader uses.
    using NtOpenFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
    auto ntOpenFile = reinterpret_cast<NtOpenFileFn>(reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtOpenFile")));
    std::wstring ntPath = L"\\??\\" + g_original;
    UNICODE_STRING us;
    us.Buffer = ntPath.data();
    us.Length = static_cast<USHORT>(ntPath.size() * sizeof(wchar_t));
    us.MaximumLength = us.Length;
    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &us, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
    IO_STATUS_BLOCK iosb;
    HANDLE h = nullptr;
    NTSTATUS st = ntOpenFile(&h, GENERIC_READ | SYNCHRONIZE, &oa, &iosb, FILE_SHARE_READ, 0x20 /* FILE_SYNCHRONOUS_IO_NONALERT */);
    Check(st == 0 && ReadHandle(h) == "PATCHED", "NtOpenFile is redirected");

    // Memory-mapped image of the redirected file has the patched bytes (how the CLR maps IL images).
    HANDLE f = CreateFileW(g_original.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    HANDLE map = CreateFileMappingW(f, nullptr, PAGE_READONLY, 0, 0, nullptr);
    const char* view = map ? static_cast<const char*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0)) : nullptr;
    Check(view && std::string(view, 7) == "PATCHED", "file mapping of the redirected handle shows the patched bytes");
    if (view)
    {
        UnmapViewOfFile(view);
    }
    if (map)
    {
        CloseHandle(map);
    }
    CloseHandle(f);

    std::printf("%s (%d failure(s))\n", g_failures ? "FAILED" : "ALL PASSED", g_failures);
    return g_failures ? 1 : 0;
}
