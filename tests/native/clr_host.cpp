#include "clr_host.h"

#include <unknwn.h>

#include <cwchar>

namespace compat::clr
{
    // The CLR v4 hosting interfaces (metahost.h is not part of MinGW, mscoree.h only has some of them).
    // Only the vtable slots up to the methods used here are declared; the order matches metahost.h.
    // They must NOT live in an anonymous namespace: GCC would then see that no class in the program
    // implements them and devirtualize the calls into __cxa_pure_virtual (a null call).
    namespace hosting
    {

        const GUID kClsidClrMetaHost = { 0x9280188d, 0x0e8e, 0x4867, { 0xb3, 0x0c, 0x7f, 0xa8, 0x38, 0x84, 0xe8, 0xde } };
        const GUID kIidClrMetaHost = { 0xd332db9e, 0xb9b3, 0x4125, { 0x82, 0x07, 0xa1, 0x48, 0x84, 0xf5, 0x32, 0x16 } };
        const GUID kIidClrRuntimeInfo = { 0xbd39d1d2, 0xba2f, 0x486a, { 0x89, 0xb0, 0xb4, 0xb0, 0xcb, 0x46, 0x68, 0x91 } };
        const GUID kClsidClrRuntimeHost = { 0x90f1a06e, 0x7712, 0x4762, { 0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02 } };
        const GUID kIidClrRuntimeHost = { 0x90f1a06c, 0x7712, 0x4762, { 0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02 } };

        struct IClrMetaHost : IUnknown
        {
            virtual HRESULT STDMETHODCALLTYPE GetRuntime(LPCWSTR version, REFIID riid, void** runtime) = 0;
        };

        struct IClrRuntimeInfo : IUnknown
        {
            virtual HRESULT STDMETHODCALLTYPE GetVersionString(LPWSTR buffer, DWORD* size) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetRuntimeDirectory(LPWSTR buffer, DWORD* size) = 0;
            virtual HRESULT STDMETHODCALLTYPE IsLoaded(HANDLE process, BOOL* loaded) = 0;
            virtual HRESULT STDMETHODCALLTYPE LoadErrorString(UINT id, LPWSTR buffer, DWORD* size, LONG lcid) = 0;
            virtual HRESULT STDMETHODCALLTYPE LoadLibrary(LPCWSTR dllName, HMODULE* module) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetProcAddress(LPCSTR name, LPVOID* proc) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetInterface(REFCLSID clsid, REFIID riid, LPVOID* unk) = 0;
        };

        struct IClrRuntimeHost : IUnknown
        {
            virtual HRESULT STDMETHODCALLTYPE Start() = 0;
            virtual HRESULT STDMETHODCALLTYPE Stop() = 0;
            virtual HRESULT STDMETHODCALLTYPE SetHostControl(IUnknown* hostControl) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetCLRControl(IUnknown** clrControl) = 0;
            virtual HRESULT STDMETHODCALLTYPE UnloadAppDomain(DWORD domainId, BOOL waitUntilDone) = 0;
            virtual HRESULT STDMETHODCALLTYPE ExecuteInAppDomain(DWORD domainId, void* callback, void* cookie) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetCurrentAppDomainId(DWORD* domainId) = 0;
            virtual HRESULT STDMETHODCALLTYPE ExecuteApplication(LPCWSTR appFullName, DWORD manifestPathCount,
                LPCWSTR* manifestPaths, DWORD activationDataCount, LPCWSTR* activationData, int* returnValue) = 0;
            virtual HRESULT STDMETHODCALLTYPE ExecuteInDefaultAppDomain(LPCWSTR assemblyPath, LPCWSTR typeName,
                LPCWSTR methodName, LPCWSTR argument, DWORD* returnValue) = 0;
        };

    }

    namespace
    {
        using namespace hosting;
        using ClrCreateInstanceFn = HRESULT(WINAPI*)(REFCLSID clsid, REFIID riid, LPVOID* instance);

        template <typename T>
        struct ComPtr
        {
            T* ptr = nullptr;
            ~ComPtr()
            {
                if (ptr)
                {
                    ptr->Release();
                }
            }
            void** out() { return reinterpret_cast<void**>(&ptr); }
        };

        std::wstring Hr(const wchar_t* what, HRESULT hr)
        {
            wchar_t buf[160];
            _snwprintf(buf, _countof(buf), L"%ls failed (HRESULT 0x%08lX)", what, static_cast<unsigned long>(hr));
            return buf;
        }
    }

    bool ExecuteInDefaultDomain(const std::wstring& assemblyPath, const wchar_t* typeName, const wchar_t* methodName,
        const std::wstring& argument, DWORD& returnValue, std::wstring& error)
    {
        HMODULE mscoree = LoadLibraryW(L"mscoree.dll");
        if (!mscoree)
        {
            error = L"mscoree.dll could not be loaded (.NET Framework 4.8 missing?)";
            return false;
        }

        auto create = reinterpret_cast<ClrCreateInstanceFn>(reinterpret_cast<void*>(GetProcAddress(mscoree, "CLRCreateInstance")));
        if (!create)
        {
            error = L"CLRCreateInstance not found in mscoree.dll";
            return false;
        }

        ComPtr<IClrMetaHost> metaHost;
        HRESULT hr = create(kClsidClrMetaHost, kIidClrMetaHost, metaHost.out());
        if (FAILED(hr))
        {
            error = Hr(L"CLRCreateInstance", hr);
            return false;
        }

        ComPtr<IClrRuntimeInfo> runtimeInfo;
        hr = metaHost.ptr->GetRuntime(L"v4.0.30319", kIidClrRuntimeInfo, runtimeInfo.out());
        if (FAILED(hr))
        {
            error = Hr(L"ICLRMetaHost::GetRuntime(v4.0.30319)", hr);
            return false;
        }

        // Returns the host of the runtime SHVDN already loaded, if any; there is only one v4 runtime per process.
        ComPtr<IClrRuntimeHost> host;
        hr = runtimeInfo.ptr->GetInterface(kClsidClrRuntimeHost, kIidClrRuntimeHost, host.out());
        if (FAILED(hr))
        {
            error = Hr(L"ICLRRuntimeInfo::GetInterface(CLRRuntimeHost)", hr);
            return false;
        }

        // S_FALSE when SHVDN already started the runtime. A failure is not fatal by itself (some hosts, e.g.
        // Wine's mscoree, do not implement Start); ExecuteInDefaultAppDomain reports the real problem.
        HRESULT startHr = host.ptr->Start();

        hr = host.ptr->ExecuteInDefaultAppDomain(assemblyPath.c_str(), typeName, methodName, argument.c_str(), &returnValue);
        if (FAILED(hr))
        {
            error = Hr(L"ICLRRuntimeHost::ExecuteInDefaultAppDomain", hr);
            if (FAILED(startHr))
            {
                error += L"; " + Hr(L"ICLRRuntimeHost::Start", startHr);
            }
            return false;
        }
        return true;
    }
}
