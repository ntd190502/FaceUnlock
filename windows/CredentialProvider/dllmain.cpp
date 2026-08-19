#include "FaceUnlockCredentialProvider.h"
#include <new>

static HMODULE g_module = nullptr;
static LONG g_locks = 0;

class Factory final : public IClassFactory {
    LONG refs_ = 1;
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override {
        return InterlockedIncrement(&refs_);
    }

    IFACEMETHODIMP_(ULONG) Release() override {
        auto r = InterlockedDecrement(&refs_);
        if (!r) delete this;
        return r;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override {
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        return CreateFaceUnlockProvider(riid, ppv);
    }

    IFACEMETHODIMP LockServer(BOOL fLock) override {
        if (fLock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        g_module = hinstDLL;
        DisableThreadLibraryCalls(hinstDLL);
    }
    return TRUE;
}

_Check_return_
STDAPI DllCanUnloadNow(void) {
    return (g_locks == 0) ? S_OK : S_FALSE;
}

_Check_return_
STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv) {
    if (rclsid != CLSID_FaceUnlockProvider) return CLASS_E_CLASSNOTAVAILABLE;
    auto f = new(std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    auto hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}
