#include "redirect.h"

#include "log.h"

#include <windows.h>
#include <winternl.h>

#include <cstdint>
#include <cstring>

namespace compat::redirect
{
    namespace
    {
        constexpr size_t kMaxPath = 1024;

        struct Target
        {
            wchar_t original[kMaxPath];  // DOS path, e.g. C:\Games\GTAV\ScriptHookVDotNet3.dll
            UNICODE_STRING originalUs;
            wchar_t patchedNt[kMaxPath]; // NT path, e.g. \??\C:\Games\GTAV\ShvdnLegacyCompat\cache\....dll
            UNICODE_STRING patchedNtUs;
            wchar_t patchedDos[kMaxPath];
            volatile LONG active;
            volatile LONG reported;
        };

        Target g_targets[2];
        HANDLE g_readyEvent = nullptr;
        volatile LONG g_ready = 0;
        DWORD g_installerThread = 0;
        DWORD g_waitTimeoutMs = 60000;
        bool g_ntHooks = false;

        using NtCreateFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
            PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
        using NtOpenFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
        using NtQueryAttributesFileFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES, PVOID);
        using RtlEqualUnicodeStringFn = BOOLEAN(NTAPI*)(const UNICODE_STRING*, const UNICODE_STRING*, BOOLEAN);
        using CreateFileWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);

        NtCreateFileFn g_origNtCreateFile = nullptr;
        NtOpenFileFn g_origNtOpenFile = nullptr;
        NtQueryAttributesFileFn g_origNtQueryAttributesFile = nullptr;
        NtQueryAttributesFileFn g_origNtQueryFullAttributesFile = nullptr;
        volatile LONG g_traceBudget = 64;
        RtlEqualUnicodeStringFn g_rtlEqualUnicodeString = nullptr;
        CreateFileWFn g_origCreateFileW = nullptr;

        constexpr ACCESS_MASK kWriteAccess = GENERIC_WRITE | GENERIC_ALL | FILE_WRITE_DATA | FILE_APPEND_DATA |
            FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA | DELETE | WRITE_DAC | WRITE_OWNER;
        constexpr ULONG kFileOpen = 0x00000001; // FILE_OPEN

#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Warray-bounds" // GCC misreads the gs: segment access in NtCurrentTeb()
        bool LoaderLockHeldByThisThread()
        {
            // PEB->LoaderLock (x64 offset 0x110). Never block while holding it: the patcher may need it.
            auto* peb = reinterpret_cast<uint8_t*>(NtCurrentTeb()->ProcessEnvironmentBlock);
            auto* loaderLock = *reinterpret_cast<RTL_CRITICAL_SECTION**>(peb + 0x110);
            return loaderLock && reinterpret_cast<DWORD_PTR>(loaderLock->OwningThread) == GetCurrentThreadId();
        }
#pragma GCC diagnostic pop

        void WaitForPatcher()
        {
            if (g_ready || LoaderLockHeldByThisThread())
            {
                return;
            }
            log::Info(L"SHVDN is loading its API before the patcher finished; waiting up to %lu ms.", g_waitTimeoutMs);
            if (WaitForSingleObject(g_readyEvent, g_waitTimeoutMs) != WAIT_OBJECT_0)
            {
                log::Warn(L"Timed out waiting for the patcher; SHVDN gets the unmodified API this session.");
            }
        }

        // Returns the target whose original path `name` refers to, or nullptr. Must stay allocation-free:
        // it runs for every file open in the process.
        Target* Match(const UNICODE_STRING* name)
        {
            if (!name || !name->Buffer || name->Length < 8)
            {
                return nullptr;
            }

            UNICODE_STRING view = *name;
            const wchar_t* b = view.Buffer;
            // \??\C:\... and \\?\C:\... both name the DOS path that follows.
            if ((b[0] == L'\\' && b[1] == L'?' && b[2] == L'?' && b[3] == L'\\') ||
                (b[0] == L'\\' && b[1] == L'\\' && b[2] == L'?' && b[3] == L'\\'))
            {
                view.Buffer += 4;
                view.Length -= 8;
                view.MaximumLength = view.Length;
            }

            for (Target& t : g_targets)
            {
                if (t.originalUs.Length == view.Length && g_rtlEqualUnicodeString(&view, &t.originalUs, TRUE))
                {
                    return &t;
                }
            }
            return nullptr;
        }

        Target* MatchForRead(POBJECT_ATTRIBUTES attributes, ACCESS_MASK access)
        {
            // The ASI's own worker (which runs the patcher) always sees the real files.
            if (!attributes || attributes->RootDirectory || (access & kWriteAccess) || GetCurrentThreadId() == g_installerThread)
            {
                return nullptr;
            }
            Target* t = Match(attributes->ObjectName);
            if (!t)
            {
                return nullptr;
            }
            WaitForPatcher();
            if (!t->active)
            {
                return nullptr;
            }
            if (InterlockedExchange(&t->reported, 1) == 0)
            {
                log::Info(L"Redirected SHVDN's load of %ls to %ls", t->original, t->patchedDos);
            }
            return t;
        }

        // Logs the first operations on the API paths, so an in-game failure shows exactly what the CLR did.
        void Trace(const wchar_t* op, const Target* t, ACCESS_MASK access, ULONG disposition, ULONG options, NTSTATUS status)
        {
            if (InterlockedDecrement(&g_traceBudget) < 0)
            {
                return;
            }
            log::Info(L"[trace] %ls %ls access=0x%08lX disposition=%lu options=0x%08lX -> NTSTATUS 0x%08lX%ls",
                op, t->original, static_cast<unsigned long>(access), disposition, options, static_cast<unsigned long>(status),
                t->active ? L" (redirected)" : L"");
        }

        NTSTATUS NTAPI HookNtQueryAttributesFile(POBJECT_ATTRIBUTES attributes, PVOID info)
        {
            if (Target* t = MatchForRead(attributes, 0))
            {
                OBJECT_ATTRIBUTES redirected = *attributes;
                redirected.ObjectName = &t->patchedNtUs;
                NTSTATUS st = g_origNtQueryAttributesFile(&redirected, info);
                Trace(L"NtQueryAttributesFile", t, 0, 0, 0, st);
                return st;
            }
            return g_origNtQueryAttributesFile(attributes, info);
        }

        NTSTATUS NTAPI HookNtQueryFullAttributesFile(POBJECT_ATTRIBUTES attributes, PVOID info)
        {
            if (Target* t = MatchForRead(attributes, 0))
            {
                OBJECT_ATTRIBUTES redirected = *attributes;
                redirected.ObjectName = &t->patchedNtUs;
                NTSTATUS st = g_origNtQueryFullAttributesFile(&redirected, info);
                Trace(L"NtQueryFullAttributesFile", t, 0, 0, 0, st);
                return st;
            }
            return g_origNtQueryFullAttributesFile(attributes, info);
        }

        NTSTATUS NTAPI HookNtCreateFile(PHANDLE handle, ACCESS_MASK access, POBJECT_ATTRIBUTES attributes,
            PIO_STATUS_BLOCK iosb, PLARGE_INTEGER allocationSize, ULONG fileAttributes, ULONG shareAccess,
            ULONG disposition, ULONG options, PVOID eaBuffer, ULONG eaLength)
        {
            if (disposition == kFileOpen)
            {
                if (Target* t = MatchForRead(attributes, access))
                {
                    OBJECT_ATTRIBUTES redirected = *attributes;
                    redirected.ObjectName = &t->patchedNtUs;
                    NTSTATUS st = g_origNtCreateFile(handle, access, &redirected, iosb, allocationSize, fileAttributes,
                        shareAccess, disposition, options, eaBuffer, eaLength);
                    Trace(L"NtCreateFile", t, access, disposition, options, st);
                    return st;
                }
            }
            return g_origNtCreateFile(handle, access, attributes, iosb, allocationSize, fileAttributes, shareAccess,
                disposition, options, eaBuffer, eaLength);
        }

        NTSTATUS NTAPI HookNtOpenFile(PHANDLE handle, ACCESS_MASK access, POBJECT_ATTRIBUTES attributes,
            PIO_STATUS_BLOCK iosb, ULONG shareAccess, ULONG options)
        {
            if (Target* t = MatchForRead(attributes, access))
            {
                OBJECT_ATTRIBUTES redirected = *attributes;
                redirected.ObjectName = &t->patchedNtUs;
                NTSTATUS st = g_origNtOpenFile(handle, access, &redirected, iosb, shareAccess, options);
                Trace(L"NtOpenFile", t, access, kFileOpen, options, st);
                return st;
            }
            return g_origNtOpenFile(handle, access, attributes, iosb, shareAccess, options);
        }

        HANDLE WINAPI HookCreateFileW(LPCWSTR fileName, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa,
            DWORD disposition, DWORD flags, HANDLE templateFile)
        {
            if (fileName && disposition == OPEN_EXISTING && !(access & kWriteAccess) && GetCurrentThreadId() != g_installerThread)
            {
                wchar_t full[kMaxPath];
                DWORD len = GetFullPathNameW(fileName, kMaxPath, full, nullptr);
                if (len > 0 && len < kMaxPath)
                {
                    UNICODE_STRING us;
                    us.Buffer = full;
                    us.Length = static_cast<USHORT>(len * sizeof(wchar_t));
                    us.MaximumLength = us.Length;
                    if (Target* t = Match(&us))
                    {
                        WaitForPatcher();
                        if (t->active)
                        {
                            if (InterlockedExchange(&t->reported, 1) == 0)
                            {
                                log::Info(L"Redirected SHVDN's load of %ls to %ls", t->original, t->patchedDos);
                            }
                            return g_origCreateFileW(t->patchedDos, access, share, sa, disposition, flags, templateFile);
                        }
                    }
                }
            }
            return g_origCreateFileW(fileName, access, share, sa, disposition, flags, templateFile);
        }

        // ---------------------------------------------------------------------------------------------
        // ntdll syscall stub hooking
        //
        // x64 stubs start with `mov r10, rcx` (4C 8B D1) + `mov eax, <id>` (B8 xx xx xx xx); everything after
        // is position independent. The first 8 bytes are swapped atomically for a 5-byte jump to a relay
        // allocated within +-2 GB, and the trampoline replays those 8 bytes before jumping back to stub+8.
        // ---------------------------------------------------------------------------------------------

        uint8_t* g_hookPage = nullptr;
        size_t g_hookPageUsed = 0;

        uint8_t* AllocNear(uint8_t* target)
        {
            SYSTEM_INFO si;
            GetSystemInfo(&si);
            const uintptr_t gran = si.dwAllocationGranularity;
            const uintptr_t origin = reinterpret_cast<uintptr_t>(target) & ~(gran - 1);
            const uintptr_t range = 0x70000000;

            for (int direction = -1; direction <= 1; direction += 2)
            {
                for (uintptr_t delta = gran; delta < range; delta += gran)
                {
                    uintptr_t addr = direction < 0 ? origin - delta : origin + delta;
                    if (direction < 0 && origin < delta + 0x10000)
                    {
                        break;
                    }
                    MEMORY_BASIC_INFORMATION mbi;
                    if (!VirtualQuery(reinterpret_cast<void*>(addr), &mbi, sizeof(mbi)))
                    {
                        break;
                    }
                    if (mbi.State != MEM_FREE)
                    {
                        continue;
                    }
                    void* p = VirtualAlloc(reinterpret_cast<void*>(addr), si.dwPageSize, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
                    if (p)
                    {
                        return static_cast<uint8_t*>(p);
                    }
                }
            }
            return nullptr;
        }

        uint8_t* EmitAbsJump(uint8_t* at, const void* destination)
        {
            // jmp qword ptr [rip+0]; dq destination
            at[0] = 0xFF;
            at[1] = 0x25;
            memset(at + 2, 0, 4);
            uint64_t dest = reinterpret_cast<uint64_t>(destination);
            memcpy(at + 6, &dest, 8);
            return at + 14;
        }

        bool HookSyscallStub(const char* name, void* hook, void** original)
        {
            auto* stub = reinterpret_cast<uint8_t*>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), name));
            if (!stub)
            {
                log::Warn(L"ntdll!%hs not found.", name);
                return false;
            }
            if (!(stub[0] == 0x4C && stub[1] == 0x8B && stub[2] == 0xD1 && stub[3] == 0xB8))
            {
                log::Warn(L"ntdll!%hs has an unexpected prologue (%02X %02X %02X %02X), probably hooked by another tool.",
                    name, stub[0], stub[1], stub[2], stub[3]);
                return false;
            }

            if (!g_hookPage)
            {
                g_hookPage = AllocNear(stub);
                if (!g_hookPage)
                {
                    log::Warn(L"No memory near ntdll for the hook relay.");
                    return false;
                }
            }
            if (g_hookPageUsed + 64 > 4096)
            {
                return false;
            }

            uint8_t* relay = g_hookPage + g_hookPageUsed;
            uint8_t* trampoline = relay + 16;
            g_hookPageUsed += 64;

            EmitAbsJump(relay, hook);
            memcpy(trampoline, stub, 8);
            EmitAbsJump(trampoline + 8, stub + 8);
            FlushInstructionCache(GetCurrentProcess(), g_hookPage, 4096);

            int64_t rel = reinterpret_cast<int64_t>(relay) - reinterpret_cast<int64_t>(stub + 5);
            if (rel < INT32_MIN || rel > INT32_MAX)
            {
                return false;
            }

            uint8_t patch[8];
            memcpy(patch, stub, 8);
            patch[0] = 0xE9;
            int32_t rel32 = static_cast<int32_t>(rel);
            memcpy(patch + 1, &rel32, 4);

            *original = trampoline;

            DWORD oldProtect;
            if (!VirtualProtect(stub, 8, PAGE_EXECUTE_READWRITE, &oldProtect))
            {
                return false;
            }
            if ((reinterpret_cast<uintptr_t>(stub) & 7) == 0)
            {
                int64_t value;
                memcpy(&value, patch, 8);
                InterlockedExchange64(reinterpret_cast<volatile LONG64*>(stub), value);
            }
            else
            {
                memcpy(stub, patch, 8);
            }
            VirtualProtect(stub, 8, oldProtect, &oldProtect);
            FlushInstructionCache(GetCurrentProcess(), stub, 8);
            return true;
        }

        void InitTarget(Target& t, const std::wstring& path)
        {
            wcsncpy(t.original, path.c_str(), kMaxPath - 1);
            t.original[kMaxPath - 1] = L'\0';
            t.originalUs.Buffer = t.original;
            t.originalUs.Length = static_cast<USHORT>(wcslen(t.original) * sizeof(wchar_t));
            t.originalUs.MaximumLength = t.originalUs.Length;
            t.active = 0;
            t.reported = 0;
        }
    }

    bool Install(const std::wstring& gameDir, unsigned long waitTimeoutMs)
    {
        g_installerThread = GetCurrentThreadId();
        g_waitTimeoutMs = waitTimeoutMs;
        g_readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        g_rtlEqualUnicodeString = reinterpret_cast<RtlEqualUnicodeStringFn>(
            reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlEqualUnicodeString")));
        if (!g_readyEvent || !g_rtlEqualUnicodeString)
        {
            return false;
        }

        InitTarget(g_targets[0], gameDir + L"\\ScriptHookVDotNet2.dll");
        InitTarget(g_targets[1], gameDir + L"\\ScriptHookVDotNet3.dll");

        bool createHooked = HookSyscallStub("NtCreateFile", reinterpret_cast<void*>(&HookNtCreateFile),
            reinterpret_cast<void**>(&g_origNtCreateFile));
        bool openHooked = createHooked && HookSyscallStub("NtOpenFile", reinterpret_cast<void*>(&HookNtOpenFile),
            reinterpret_cast<void**>(&g_origNtOpenFile));
        if (createHooked && !openHooked)
        {
            log::Warn(L"NtOpenFile could not be hooked; only NtCreateFile is redirected.");
        }
        // Path-based attribute queries must describe the same file the opens return, or the CLR sees a file
        // whose size/timestamps change between its checks and its open.
        if (createHooked &&
            !(HookSyscallStub("NtQueryAttributesFile", reinterpret_cast<void*>(&HookNtQueryAttributesFile),
                  reinterpret_cast<void**>(&g_origNtQueryAttributesFile)) &&
              HookSyscallStub("NtQueryFullAttributesFile", reinterpret_cast<void*>(&HookNtQueryFullAttributesFile),
                  reinterpret_cast<void**>(&g_origNtQueryFullAttributesFile))))
        {
            log::Warn(L"Attribute queries could not be hooked; the CLR may reject the redirected file.");
        }
        g_ntHooks = createHooked;
        return createHooked;
    }

    bool SetTarget(const std::wstring& originalPath, const std::wstring& patchedPath)
    {
        for (Target& t : g_targets)
        {
            if (_wcsicmp(t.original, originalPath.c_str()) != 0)
            {
                continue;
            }
            std::wstring nt = L"\\??\\" + patchedPath;
            if (nt.size() >= kMaxPath || patchedPath.size() >= kMaxPath)
            {
                return false;
            }
            wcscpy(t.patchedNt, nt.c_str());
            wcscpy(t.patchedDos, patchedPath.c_str());
            t.patchedNtUs.Buffer = t.patchedNt;
            t.patchedNtUs.Length = static_cast<USHORT>(nt.size() * sizeof(wchar_t));
            t.patchedNtUs.MaximumLength = t.patchedNtUs.Length;
            MemoryBarrier();
            InterlockedExchange(&t.active, 1);
            return true;
        }
        return false;
    }

    bool IsReady()
    {
        return g_ready != 0;
    }

    void Ready()
    {
        InterlockedExchange(&g_ready, 1);
        if (g_readyEvent)
        {
            SetEvent(g_readyEvent);
        }
    }

    bool NtHooksInstalled()
    {
        return g_ntHooks;
    }

    bool InstallClrImportHook()
    {
        auto* clr = reinterpret_cast<uint8_t*>(GetModuleHandleW(L"clr.dll"));
        if (!g_rtlEqualUnicodeString)
        {
            return false;
        }
        if (!clr)
        {
            log::Warn(L"clr.dll is not loaded; cannot install the CreateFileW fallback hook.");
            return false;
        }

        void* candidates[] = {
            reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "CreateFileW")),
            reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), "CreateFileW")),
        };
        g_origCreateFileW = reinterpret_cast<CreateFileWFn>(candidates[1] ? candidates[1] : candidates[0]);

        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(clr);
        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(clr + dos->e_lfanew);
        const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress)
        {
            return false;
        }

        int patched = 0;
        for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(clr + dir.VirtualAddress); imp->Name; ++imp)
        {
            for (auto* thunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(clr + imp->FirstThunk); thunk->u1.Function; ++thunk)
            {
                void* fn = reinterpret_cast<void*>(thunk->u1.Function);
                if (fn != candidates[0] && fn != candidates[1])
                {
                    continue;
                }
                DWORD oldProtect;
                if (VirtualProtect(&thunk->u1.Function, sizeof(thunk->u1.Function), PAGE_READWRITE, &oldProtect))
                {
                    InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&thunk->u1.Function),
                        static_cast<LONG64>(reinterpret_cast<uintptr_t>(&HookCreateFileW)));
                    VirtualProtect(&thunk->u1.Function, sizeof(thunk->u1.Function), oldProtect, &oldProtect);
                    ++patched;
                }
            }
        }
        return patched > 0;
    }
}
