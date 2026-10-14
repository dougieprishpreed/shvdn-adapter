#pragma once

#include <string>

namespace compat::log
{
    // Starts a new log file for this game session (the previous one is kept as .old.log).
    void Init(const std::wstring& path);

    void Info(const wchar_t* fmt, ...);
    void Warn(const wchar_t* fmt, ...);
    void Error(const wchar_t* fmt, ...);
}
