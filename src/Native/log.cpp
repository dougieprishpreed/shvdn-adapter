#include "log.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>

namespace compat::log
{
    namespace
    {
        CRITICAL_SECTION g_lock;
        bool g_ready = false;
        wchar_t g_path[MAX_PATH * 2] = {};

        void Write(const wchar_t* level, const wchar_t* fmt, va_list args)
        {
            if (!g_ready)
            {
                return;
            }

            wchar_t message[2048];
            _vsnwprintf(message, _countof(message) - 1, fmt, args);
            message[_countof(message) - 1] = L'\0';

            SYSTEMTIME now;
            GetLocalTime(&now);
            wchar_t line[2200];
            int len = _snwprintf(line, _countof(line) - 3, L"[%02u:%02u:%02u] [%ls] [asi] %ls",
                now.wHour, now.wMinute, now.wSecond, level, message);
            if (len < 0)
            {
                len = static_cast<int>(_countof(line)) - 3;
            }
            line[len++] = L'\r';
            line[len++] = L'\n';
            line[len] = L'\0';

            char utf8[4400];
            int bytes = WideCharToMultiByte(CP_UTF8, 0, line, len, utf8, sizeof(utf8), nullptr, nullptr);
            if (bytes <= 0)
            {
                return;
            }

            EnterCriticalSection(&g_lock);
            // Opened per line and shared, because the managed patcher appends to the same file.
            HANDLE file = CreateFileW(g_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file != INVALID_HANDLE_VALUE)
            {
                DWORD written = 0;
                WriteFile(file, utf8, static_cast<DWORD>(bytes), &written, nullptr);
                CloseHandle(file);
            }
            LeaveCriticalSection(&g_lock);
        }
    }

    void Init(const std::wstring& path)
    {
        InitializeCriticalSection(&g_lock);
        wcsncpy(g_path, path.c_str(), _countof(g_path) - 1);

        std::wstring old = path;
        size_t dot = old.rfind(L'.');
        old = (dot == std::wstring::npos ? old : old.substr(0, dot)) + L".old.log";
        MoveFileExW(path.c_str(), old.c_str(), MOVEFILE_REPLACE_EXISTING);
        g_ready = true;
    }

#define COMPAT_LOG_IMPL(name, level)      \
    void name(const wchar_t* fmt, ...)    \
    {                                     \
        va_list args;                     \
        va_start(args, fmt);              \
        Write(level, fmt, args);          \
        va_end(args);                     \
    }

    COMPAT_LOG_IMPL(Info, L"INFO")
    COMPAT_LOG_IMPL(Warn, L"WARN")
    COMPAT_LOG_IMPL(Error, L"ERROR")
}
