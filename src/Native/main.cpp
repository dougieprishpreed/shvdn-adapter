// ShvdnLegacyCompat.asi
//
// Lets scripts built against older ScriptHookVDotNet builds run on the current SHVDN nightly (GTA V Legacy).
// On start it runs the embedded managed patcher (as a separate process, so it never shares the CLR with
// SHVDN), which writes copies of ScriptHookVDotNet2/3.dll with the removed/re-signatured API members added
// back as shims, and then redirects SHVDN's load of the API DLLs to those copies. Script files and the SHVDN
// install are never modified (unless Mode=Disk is chosen).

#include "log.h"
#include "redirect.h"
#include "resource.h"

#include <windows.h>
#include <bcrypt.h>

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace
{
    constexpr const wchar_t* kVersion = L"1.0.1";

    HMODULE g_module = nullptr;

    struct Config
    {
        bool enabled = true;
        std::wstring mode = L"Redirect";
        bool patchV2 = true;
        bool patchV3 = true;
        bool scanScripts = true;
        DWORD waitTimeoutMs = 20000;
    };

    std::wstring ModulePath(HMODULE module)
    {
        std::wstring path(MAX_PATH, L'\0');
        for (;;)
        {
            DWORD len = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
            if (len == 0)
            {
                return std::wstring();
            }
            if (len < path.size())
            {
                path.resize(len);
                return path;
            }
            path.resize(path.size() * 2);
        }
    }

    std::wstring DirectoryOf(const std::wstring& path)
    {
        size_t slash = path.find_last_of(L"\\/");
        return slash == std::wstring::npos ? std::wstring(L".") : path.substr(0, slash);
    }

    std::wstring WithoutExtension(const std::wstring& path)
    {
        size_t dot = path.find_last_of(L'.');
        size_t slash = path.find_last_of(L"\\/");
        return (dot == std::wstring::npos || (slash != std::wstring::npos && dot < slash)) ? path : path.substr(0, dot);
    }

    bool ParseBool(const wchar_t* value, bool fallback)
    {
        if (!_wcsicmp(value, L"true") || !_wcsicmp(value, L"1") || !_wcsicmp(value, L"yes") || !_wcsicmp(value, L"on"))
        {
            return true;
        }
        if (!_wcsicmp(value, L"false") || !_wcsicmp(value, L"0") || !_wcsicmp(value, L"no") || !_wcsicmp(value, L"off"))
        {
            return false;
        }
        return fallback;
    }

    const char kDefaultIni[] =
        "; ShvdnLegacyCompat - run scripts made for older ScriptHookVDotNet builds on the current nightly.\r\n"
        "; Log: ShvdnLegacyCompat\\ShvdnLegacyCompat.log (lists added members and scripts that still have problems).\r\n"
        "[ShvdnLegacyCompat]\r\n"
        "; Set to false to disable the compatibility layer without removing the .asi.\r\n"
        "Enabled=true\r\n"
        "\r\n"
        "; Redirect = patched API copies live in ShvdnLegacyCompat\\cache and SHVDN is redirected to them in memory;\r\n"
        ";            nothing in the game folder is modified (default).\r\n"
        "; Disk     = write the patched API over ScriptHookVDotNet2/3.dll (originals saved as *.legacycompat.bak).\r\n"
        ";            Use this only if Redirect does not work on your system.\r\n"
        "; Restore  = put the *.legacycompat.bak originals back (undo Disk), then switch back to Redirect or Off.\r\n"
        "; Off      = do not patch anything, only check scripts and log what they are missing.\r\n"
        "Mode=Redirect\r\n"
        "\r\n"
        "; Which API versions to patch.\r\n"
        "PatchV2=true\r\n"
        "PatchV3=true\r\n"
        "\r\n"
        "; Check every script in the SHVDN scripts folder at startup and log API members that are still missing.\r\n"
        "ScanScripts=true\r\n"
        "\r\n"
        "; Only matters on the very first start (or after updating SHVDN/this mod): how long (ms) SHVDN may be held\r\n"
        "; back if it loads its API before the patched copy is built. Later starts use the cache instantly.\r\n"
        "WaitTimeoutMs=20000\r\n";

    Config LoadConfig(const std::wstring& iniPath)
    {
        if (GetFileAttributesW(iniPath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            HANDLE file = CreateFileW(iniPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file != INVALID_HANDLE_VALUE)
            {
                DWORD written = 0;
                WriteFile(file, kDefaultIni, sizeof(kDefaultIni) - 1, &written, nullptr);
                CloseHandle(file);
            }
        }

        Config cfg;
        wchar_t buf[256];
        const wchar_t* section = L"ShvdnLegacyCompat";
        GetPrivateProfileStringW(section, L"Enabled", L"true", buf, 256, iniPath.c_str());
        cfg.enabled = ParseBool(buf, true);
        GetPrivateProfileStringW(section, L"Mode", L"Redirect", buf, 256, iniPath.c_str());
        cfg.mode = buf;
        GetPrivateProfileStringW(section, L"PatchV2", L"true", buf, 256, iniPath.c_str());
        cfg.patchV2 = ParseBool(buf, true);
        GetPrivateProfileStringW(section, L"PatchV3", L"true", buf, 256, iniPath.c_str());
        cfg.patchV3 = ParseBool(buf, true);
        GetPrivateProfileStringW(section, L"ScanScripts", L"true", buf, 256, iniPath.c_str());
        cfg.scanScripts = ParseBool(buf, true);
        cfg.waitTimeoutMs = GetPrivateProfileIntW(section, L"WaitTimeoutMs", 20000, iniPath.c_str());

        if (_wcsicmp(cfg.mode.c_str(), L"Redirect") && _wcsicmp(cfg.mode.c_str(), L"Disk") &&
            _wcsicmp(cfg.mode.c_str(), L"Restore") && _wcsicmp(cfg.mode.c_str(), L"Off"))
        {
            cfg.mode = L"Redirect";
        }
        return cfg;
    }

    // Writes an embedded RCDATA resource to disk unless an identical file is already there.
    bool ExtractResource(int id, const std::wstring& path)
    {
        HRSRC res = FindResourceW(g_module, MAKEINTRESOURCEW(id), MAKEINTRESOURCEW(10) /* RT_RCDATA */);
        HGLOBAL handle = res ? LoadResource(g_module, res) : nullptr;
        const void* data = handle ? LockResource(handle) : nullptr;
        DWORD size = res ? SizeofResource(g_module, res) : 0;
        if (!data || size == 0)
        {
            compat::log::Error(L"Embedded resource %d is missing.", id);
            return false;
        }

        HANDLE existing = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (existing != INVALID_HANDLE_VALUE)
        {
            LARGE_INTEGER existingSize{};
            bool same = false;
            if (GetFileSizeEx(existing, &existingSize) && existingSize.QuadPart == size)
            {
                std::vector<char> current(size);
                DWORD read = 0;
                same = ReadFile(existing, current.data(), size, &read, nullptr) && read == size && memcmp(current.data(), data, size) == 0;
            }
            CloseHandle(existing);
            if (same)
            {
                return true;
            }
        }

        std::wstring tmp = path + L".tmp";
        HANDLE file = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            compat::log::Error(L"Cannot write %ls (error %lu).", tmp.c_str(), GetLastError());
            return false;
        }
        DWORD written = 0;
        BOOL ok = WriteFile(file, data, size, &written, nullptr) && written == size;
        CloseHandle(file);
        if (!ok || !MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
        {
            compat::log::Error(L"Cannot write %ls (error %lu).", path.c_str(), GetLastError());
            DeleteFileW(tmp.c_str());
            return false;
        }
        return true;
    }

    // Identifies this ASI build (the embedded managed payload), so cache entries written by another build
    // are never trusted by the fast path.
    std::wstring PayloadFingerprint()
    {
        uint64_t hash = 1469598103934665603ULL; // FNV-1a 64
        for (int id : { IDR_PATCHER, IDR_CECIL })
        {
            HRSRC res = FindResourceW(g_module, MAKEINTRESOURCEW(id), MAKEINTRESOURCEW(10));
            HGLOBAL handle = res ? LoadResource(g_module, res) : nullptr;
            auto* data = static_cast<const uint8_t*>(handle ? LockResource(handle) : nullptr);
            DWORD size = res ? SizeofResource(g_module, res) : 0;
            for (DWORD i = 0; data && i < size; ++i)
            {
                hash = (hash ^ data[i]) * 1099511628211ULL;
            }
        }
        wchar_t buf[17];
        _snwprintf(buf, 17, L"%016llx", static_cast<unsigned long long>(hash));
        buf[16] = L'\0';
        return buf;
    }

    // Lower-case hex SHA-256 of a file, or an empty string on failure.
    std::wstring Sha256File(const std::wstring& path)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return std::wstring();
        }

        std::wstring result;
        BCRYPT_ALG_HANDLE alg = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0 &&
            BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) == 0)
        {
            std::vector<uint8_t> buffer(1 << 16);
            DWORD read = 0;
            bool ok = true;
            while (ok && ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr) && read > 0)
            {
                ok = BCryptHashData(hash, buffer.data(), read, 0) == 0;
            }
            uint8_t digest[32];
            if (ok && BCryptFinishHash(hash, digest, sizeof(digest), 0) == 0)
            {
                wchar_t hex[65];
                for (int i = 0; i < 32; ++i)
                {
                    _snwprintf(hex + i * 2, 3, L"%02x", digest[i]);
                }
                hex[64] = L'\0';
                result = hex;
            }
        }
        if (hash)
        {
            BCryptDestroyHash(hash);
        }
        if (alg)
        {
            BCryptCloseAlgorithmProvider(alg, 0);
        }
        CloseHandle(file);
        return result;
    }

    std::vector<std::wstring> ReadLines(const std::wstring& path)
    {
        std::vector<std::wstring> lines;
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return lines;
        }
        LARGE_INTEGER size{};
        std::string bytes;
        if (GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart < (1 << 20))
        {
            bytes.resize(static_cast<size_t>(size.QuadPart));
            DWORD read = 0;
            if (!ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr))
            {
                read = 0;
            }
            bytes.resize(read);
        }
        CloseHandle(file);

        int wlen = MultiByteToWideChar(CP_UTF8, 0, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
        std::wstring text(wlen, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, bytes.data(), static_cast<int>(bytes.size()), text.data(), wlen);

        size_t start = 0;
        while (start < text.size())
        {
            size_t end = text.find(L'\n', start);
            if (end == std::wstring::npos)
            {
                end = text.size();
            }
            std::wstring line = text.substr(start, end - start);
            if (!line.empty() && line.back() == L'\r')
            {
                line.pop_back();
            }
            if (!line.empty())
            {
                lines.push_back(line);
            }
            start = end + 1;
        }
        return lines;
    }

    std::vector<std::wstring> Split(const std::wstring& line, wchar_t sep)
    {
        std::vector<std::wstring> parts;
        size_t start = 0;
        for (;;)
        {
            size_t pos = line.find(sep, start);
            parts.push_back(line.substr(start, pos == std::wstring::npos ? std::wstring::npos : pos - start));
            if (pos == std::wstring::npos)
            {
                return parts;
            }
            start = pos + 1;
        }
    }

    // Activates cached patched copies written by an earlier start of this same ASI build, before the CLR is
    // touched at all. Returns true when every API file SHVDN will load is covered.
    bool ActivateFromCache(const std::wstring& gameDir, const std::wstring& indexPath, const std::wstring& fingerprint,
        const Config& cfg)
    {
        bool allCovered = true;
        std::vector<std::wstring> index = ReadLines(indexPath);
        for (int major : { 2, 3 })
        {
            if ((major == 2 && !cfg.patchV2) || (major == 3 && !cfg.patchV3))
            {
                continue;
            }
            std::wstring api = gameDir + L"\\ScriptHookVDotNet" + std::to_wstring(major) + L".dll";
            if (GetFileAttributesW(api.c_str()) == INVALID_FILE_ATTRIBUTES)
            {
                continue;
            }

            bool covered = false;
            std::wstring sha;
            for (const std::wstring& line : index)
            {
                std::vector<std::wstring> f = Split(line, L'|');
                if (f.size() != 4 || f[0] != std::to_wstring(major) || f[3] != fingerprint)
                {
                    continue;
                }
                if (sha.empty())
                {
                    sha = Sha256File(api);
                }
                if (!sha.empty() && _wcsicmp(f[1].c_str(), sha.c_str()) == 0 &&
                    GetFileAttributesW(f[2].c_str()) != INVALID_FILE_ATTRIBUTES && compat::redirect::SetTarget(api, f[2]))
                {
                    covered = true;
                    compat::log::Info(L"Cached patched copy activated for ScriptHookVDotNet%d.dll.", major);
                }
                break;
            }
            allCovered = allCovered && covered;
        }
        return allCovered;
    }

    DWORD WINAPI Worker(LPVOID)
    {
        const std::wstring gameDir = DirectoryOf(ModulePath(nullptr));
        const std::wstring asiPath = ModulePath(g_module);
        const std::wstring iniPath = WithoutExtension(asiPath) + L".ini";
        const std::wstring dataDir = gameDir + L"\\ShvdnLegacyCompat";
        const std::wstring binDir = dataDir + L"\\bin";
        const std::wstring logPath = dataDir + L"\\ShvdnLegacyCompat.log";
        const std::wstring redirectFile = dataDir + L"\\redirects.txt";

        CreateDirectoryW(dataDir.c_str(), nullptr);
        CreateDirectoryW(binDir.c_str(), nullptr);
        compat::log::Init(logPath);
        compat::log::Info(L"ShvdnLegacyCompat %ls loaded into %ls", kVersion, ModulePath(nullptr).c_str());

        Config cfg = LoadConfig(iniPath);
        if (!cfg.enabled)
        {
            compat::log::Info(L"Disabled in %ls.", iniPath.c_str());
            return 0;
        }
        compat::log::Info(L"Mode=%ls PatchV2=%d PatchV3=%d ScanScripts=%d", cfg.mode.c_str(), cfg.patchV2, cfg.patchV3, cfg.scanScripts);

        const bool redirectMode = _wcsicmp(cfg.mode.c_str(), L"Redirect") == 0;
        const std::wstring fingerprint = PayloadFingerprint();
        if (redirectMode)
        {
            // Hook first, so SHVDN cannot load the unpatched API while the patcher is still running.
            if (compat::redirect::Install(gameDir, cfg.waitTimeoutMs))
            {
                compat::log::Info(L"File redirection hooks installed (ntdll).");
            }
            else
            {
                compat::log::Warn(L"ntdll hooks unavailable; will fall back to hooking clr.dll's CreateFileW import.");
            }

            if (ActivateFromCache(gameDir, dataDir + L"\\cache\\index.txt", fingerprint, cfg) && compat::redirect::NtHooksInstalled())
            {
                compat::redirect::Ready();
                compat::log::Info(L"Redirect targets ready (from cache).");
            }
        }

        const std::wstring patcherPath = binDir + L"\\ShvdnCompat.Patcher.exe";
        const bool extracted = ExtractResource(IDR_PATCHER, patcherPath) && ExtractResource(IDR_CECIL, binDir + L"\\Mono.Cecil.dll");
        const std::wstring baseArgs = L"GameDir=" + gameDir + L"\nDataDir=" + dataDir + L"\nMode=" + cfg.mode +
            L"\nPatchV2=" + (cfg.patchV2 ? L"true" : L"false") + L"\nPatchV3=" + (cfg.patchV3 ? L"true" : L"false") +
            L"\nScanScripts=" + (cfg.scanScripts ? L"true" : L"false") + L"\nLogFile=" + logPath +
            L"\nRedirectFile=" + redirectFile + L"\nFingerprint=" + fingerprint + L"\n";

        auto runPatcher = [&](const wchar_t* task, DWORD timeoutMs) {
            if (!extracted)
            {
                return false;
            }

            // Options go through a file so paths with spaces or unicode need no command-line quoting.
            const std::wstring optionsFile = dataDir + L"\\patcher-" + task + L".txt";
            const std::wstring options = baseArgs + L"Task=" + task + L"\n";
            int bytes = WideCharToMultiByte(CP_UTF8, 0, options.c_str(), static_cast<int>(options.size()), nullptr, 0, nullptr, nullptr);
            std::string utf8(bytes, '\0');
            WideCharToMultiByte(CP_UTF8, 0, options.c_str(), static_cast<int>(options.size()), utf8.data(), bytes, nullptr, nullptr);
            HANDLE file = CreateFileW(optionsFile.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                compat::log::Error(L"Cannot write %ls (error %lu).", optionsFile.c_str(), GetLastError());
                return false;
            }
            DWORD written = 0;
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
            CloseHandle(file);

            std::wstring commandLine = L"\"" + patcherPath + L"\" \"" + optionsFile + L"\"";
            STARTUPINFOW si{};
            si.cb = sizeof(si);
            PROCESS_INFORMATION pi{};
            DWORD start = GetTickCount();
            if (!CreateProcessW(patcherPath.c_str(), commandLine.data(), nullptr, nullptr, FALSE,
                    CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS, nullptr, binDir.c_str(), &si, &pi))
            {
                compat::log::Error(L"Could not start the patcher (%ls): error %lu. Is .NET Framework 4.8 installed?", task, GetLastError());
                return false;
            }
            CloseHandle(pi.hThread);

            DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
            DWORD rc = STILL_ACTIVE;
            GetExitCodeProcess(pi.hProcess, &rc);
            CloseHandle(pi.hProcess);
            if (wait != WAIT_OBJECT_0)
            {
                compat::log::Warn(L"Patcher task %ls still running after %lu ms; continuing without waiting for it.", task, timeoutMs);
                return false;
            }
            compat::log::Info(L"Patcher task %ls finished with code %lu in %lu ms.", task, rc, GetTickCount() - start);
            return rc == 0;
        };

        DeleteFileW(redirectFile.c_str());
        runPatcher(L"Patch", 180000);

        if (redirectMode && !compat::redirect::IsReady())
        {
            int active = 0;
            for (const std::wstring& line : ReadLines(redirectFile))
            {
                std::vector<std::wstring> f = Split(line, L'|');
                if (f.size() == 2 && GetFileAttributesW(f[1].c_str()) != INVALID_FILE_ATTRIBUTES && compat::redirect::SetTarget(f[0], f[1]))
                {
                    ++active;
                }
                else
                {
                    compat::log::Warn(L"Ignoring redirect line: %ls", line.c_str());
                }
            }

            if (active > 0 && !compat::redirect::NtHooksInstalled())
            {
                // SHVDN's ASI normally has the CLR loaded by now; give it time if it is still starting.
                for (int i = 0; i < 6000 && !GetModuleHandleW(L"clr.dll"); ++i)
                {
                    Sleep(50);
                }
                if (compat::redirect::InstallClrImportHook())
                {
                    compat::log::Info(L"Fallback CreateFileW hook installed in clr.dll.");
                }
                else
                {
                    compat::log::Error(L"No redirection hook could be installed. Set Mode=Disk in the ini instead.");
                }
            }
            compat::redirect::Ready();
            compat::log::Info(L"Redirect targets ready: %d API file(s) will be served from patched copies.", active);
        }

        // Informational only, so it runs after SHVDN has been unblocked.
        if (cfg.scanScripts)
        {
            runPatcher(L"Scan", 600000);
        }
        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        // Everything else (file I/O, starting the patcher) must not run under the loader lock.
        HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
        if (thread)
        {
            CloseHandle(thread);
        }
    }
    return TRUE;
}
