#pragma once

#include <windows.h>

#include <string>

namespace compat::clr
{
    // Test helper (used by e2e_host to play SHVDN): runs `static int TypeName.MethodName(string)` in the default AppDomain of the
    // CLR v4 runtime of this process (starting it if SHVDN has not done so yet).
    // Returns false and sets `error` when the runtime could not be reached.
    bool ExecuteInDefaultDomain(const std::wstring& assemblyPath, const wchar_t* typeName, const wchar_t* methodName,
        const std::wstring& argument, DWORD& returnValue, std::wstring& error);
}
