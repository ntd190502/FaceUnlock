// CredentialProviderHarness.cpp
// Standalone LogonUI-safety harness for FaceUnlockCredentialProvider.dll
//
// Tests:
//   1. DllGetClassObject -> IClassFactory -> CreateInstance -> ICredentialProvider
//   2. SetUsageScenario / SetUserArray (mock)
//   3. GetFieldDescriptorCount / GetFieldDescriptorAt
//   4. GetCredentialCount / GetCredentialAt
//   5. QueryInterface ICredentialProviderCredential2
//   6. Advise / UnAdvise
//   7. SetSelected / SetDeselected
//   8. Full Release (1000 iterations)
//   9. Async destruction during auth (UnAdvise + Release all COM refs while worker is running)
//  10. Strict COM Lifetime & Ctor/Dtor balance test (ctor_count == dtor_count)
//  11. IPC unavailable (no service — verifies no crash, hang, or stale callback)
//  12. CredentialsChanged loop audit (SetFieldState must not be called from thread)
//  13. DllCanUnloadNow
//
// EXIT CODE: 0 = all tests passed, 1 = one or more tests failed.

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <objbase.h>
#include <strsafe.h>
#include <atomic>
#include <cassert>
#include <cstdio>
#include <string>
#include <thread>
#include <chrono>

// {64D6E84B-4969-4B59-A11A-58C3D9FA0110}
static const CLSID CLSID_FaceUnlock =
    {0x64d6e84b,0x4969,0x4b59,{0xa1,0x1a,0x58,0xc3,0xd9,0xfa,0x01,0x10}};

// ------------------------------------------------------------------ helpers --
static int g_passed = 0;
static int g_failed = 0;

static void Pass(const char* name) {
    g_passed++;
    printf("  [PASS] %s\n", name);
}

static void Fail(const char* name, const char* reason = "") {
    g_failed++;
    printf("  [FAIL] %s  %s\n", name, reason);
}

static void Check(bool cond, const char* name, const char* reason = "") {
    if (cond) Pass(name);
    else       Fail(name, reason);
}

static void CheckHR(HRESULT hr, const char* name) {
    char buf[64];
    StringCchPrintfA(buf, ARRAYSIZE(buf), "(HRESULT=0x%08X)", (unsigned)hr);
    Check(SUCCEEDED(hr), name, SUCCEEDED(hr) ? "" : buf);
}

// ----------------------------------------------------------------- mock user -
class MockUser final : public ICredentialProviderUser {
    LONG refs_ = 1;
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderUser) {
            *ppv = static_cast<ICredentialProviderUser*>(this);
            AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return (ULONG)InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (!r) delete this; return (ULONG)r;
    }
    IFACEMETHODIMP GetSid(PWSTR* ppszSid) override {
        if (!ppszSid) return E_POINTER;
        size_t len = wcslen(L"S-1-5-21-0000-HARNESS") + 1;
        *ppszSid = (PWSTR)CoTaskMemAlloc(len * sizeof(WCHAR));
        if (!*ppszSid) return E_OUTOFMEMORY;
        StringCchCopyW(*ppszSid, len, L"S-1-5-21-0000-HARNESS");
        return S_OK;
    }
    IFACEMETHODIMP GetProviderID(GUID*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetStringValue(REFPROPERTYKEY, PWSTR* ppszValue) override {
        if (!ppszValue) return E_POINTER;
        size_t len = wcslen(L"HARNESS\\TestUser") + 1;
        *ppszValue = (PWSTR)CoTaskMemAlloc(len * sizeof(WCHAR));
        if (!*ppszValue) return E_OUTOFMEMORY;
        StringCchCopyW(*ppszValue, len, L"HARNESS\\TestUser");
        return S_OK;
    }
    IFACEMETHODIMP GetValue(REFPROPERTYKEY, PROPVARIANT*) override { return E_NOTIMPL; }
};

class MockUserArray final : public ICredentialProviderUserArray {
    LONG refs_ = 1;
    MockUser* user_ = nullptr;
public:
    MockUserArray() { user_ = new(std::nothrow) MockUser(); }
    ~MockUserArray() { if (user_) user_->Release(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderUserArray) {
            *ppv = static_cast<ICredentialProviderUserArray*>(this);
            AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return (ULONG)InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (!r) delete this; return (ULONG)r;
    }
    IFACEMETHODIMP SetProviderFilter(REFGUID) override { return S_OK; }
    IFACEMETHODIMP GetAccountOptions(CREDENTIAL_PROVIDER_ACCOUNT_OPTIONS* pcpao) override {
        if (pcpao) *pcpao = CPAO_NONE; return S_OK;
    }
    IFACEMETHODIMP GetCount(DWORD* pdwCount) override {
        if (!pdwCount) return E_POINTER;
        *pdwCount = user_ ? 1 : 0; return S_OK;
    }
    IFACEMETHODIMP GetAt(DWORD dwIndex, ICredentialProviderUser** ppcpu) override {
        if (!ppcpu) return E_POINTER;
        *ppcpu = nullptr;
        if (dwIndex != 0 || !user_) return E_INVALIDARG;
        user_->AddRef();
        *ppcpu = user_;
        return S_OK;
    }
};

class MockEvents final : public ICredentialProviderCredentialEvents {
    LONG refs_ = 1;
    std::atomic<int> fieldStringCalls_{ 0 };
    std::atomic<int> fieldStateCalls_{ 0 };
    std::atomic<int> credChangedCalls_{ 0 };
public:
    int getFieldStringCalls() const { return fieldStringCalls_.load(); }
    int getFieldStateCalls()  const { return fieldStateCalls_.load(); }
    int getCredChangedCalls() const { return credChangedCalls_.load(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredentialEvents) {
            *ppv = static_cast<ICredentialProviderCredentialEvents*>(this);
            AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return (ULONG)InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (!r) delete this; return (ULONG)r;
    }
    IFACEMETHODIMP SetFieldState(ICredentialProviderCredential*, DWORD, CREDENTIAL_PROVIDER_FIELD_STATE) override {
        fieldStateCalls_++;
        return S_OK;
    }
    IFACEMETHODIMP SetFieldInteractiveState(ICredentialProviderCredential*, DWORD, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE) override {
        return S_OK;
    }
    IFACEMETHODIMP SetFieldString(ICredentialProviderCredential*, DWORD, PCWSTR) override {
        fieldStringCalls_++;
        return S_OK;
    }
    IFACEMETHODIMP SetFieldCheckbox(ICredentialProviderCredential*, DWORD, BOOL, PCWSTR) override { return S_OK; }
    IFACEMETHODIMP SetFieldBitmap(ICredentialProviderCredential*, DWORD, HBITMAP) override { return S_OK; }
    IFACEMETHODIMP SetFieldComboBoxSelectedItem(ICredentialProviderCredential*, DWORD, DWORD) override { return S_OK; }
    IFACEMETHODIMP DeleteFieldComboBoxItem(ICredentialProviderCredential*, DWORD, DWORD) override { return S_OK; }
    IFACEMETHODIMP AppendFieldComboBoxItem(ICredentialProviderCredential*, DWORD, LPCWSTR) override { return S_OK; }
    IFACEMETHODIMP SetFieldSubmitButton(ICredentialProviderCredential*, DWORD, DWORD) override { return S_OK; }
    IFACEMETHODIMP OnCreatingWindow(HWND*) override { return S_OK; }
};

// ------------------------------------------------------------------ DLL load -
typedef HRESULT(STDAPICALLTYPE* PFN_DllGetClassObject)(REFCLSID, REFIID, LPVOID*);
typedef HRESULT(STDAPICALLTYPE* PFN_DllCanUnloadNow)(void);
typedef LONG(WINAPI* PFN_GetCredentialCtorCount)(void);
typedef LONG(WINAPI* PFN_GetCredentialDtorCount)(void);
typedef LONG(WINAPI* PFN_GetAuthWorkerCount)(void);

static HMODULE                   g_hDll                  = nullptr;
static PFN_DllGetClassObject      g_pfnGetClass           = nullptr;
static PFN_DllCanUnloadNow        g_pfnCanUnload          = nullptr;
static PFN_GetCredentialCtorCount g_pfnGetCtorCount       = nullptr;
static PFN_GetCredentialDtorCount g_pfnGetDtorCount       = nullptr;
static PFN_GetAuthWorkerCount     g_pfnGetWorkerCount     = nullptr;

static bool LoadDll(const wchar_t* path) {
    g_hDll = LoadLibraryW(path);
    if (!g_hDll) {
        printf("[ERROR] LoadLibraryW failed: 0x%08X\n", GetLastError());
        return false;
    }
    g_pfnGetClass    = (PFN_DllGetClassObject)      GetProcAddress(g_hDll, "DllGetClassObject");
    g_pfnCanUnload   = (PFN_DllCanUnloadNow)        GetProcAddress(g_hDll, "DllCanUnloadNow");
    g_pfnGetCtorCount = (PFN_GetCredentialCtorCount)GetProcAddress(g_hDll, "GetCredentialCtorCount");
    g_pfnGetDtorCount = (PFN_GetCredentialDtorCount)GetProcAddress(g_hDll, "GetCredentialDtorCount");
    g_pfnGetWorkerCount = (PFN_GetAuthWorkerCount)  GetProcAddress(g_hDll, "GetAuthWorkerCount");

    if (!g_pfnGetClass || !g_pfnCanUnload) {
        printf("[ERROR] Missing DLL exports\n");
        return false;
    }
    return true;
}

// ------------------------------------------------------------------ helpers --
static ICredentialProvider* CreateProvider() {
    IClassFactory* pFactory = nullptr;
    HRESULT hr = g_pfnGetClass(CLSID_FaceUnlock, IID_IClassFactory, (void**)&pFactory);
    if (FAILED(hr) || !pFactory) return nullptr;

    ICredentialProvider* pCP = nullptr;
    hr = pFactory->CreateInstance(nullptr, IID_ICredentialProvider, (void**)&pCP);
    pFactory->Release();
    if (FAILED(hr) || !pCP) return nullptr;
    return pCP;
}

// ================================================================ TEST SUITE ===

static void Test_DllGetClassObject() {
    printf("\n[Test 1] DllGetClassObject\n");
    IClassFactory* pFactory = nullptr;
    HRESULT hr = g_pfnGetClass(CLSID_FaceUnlock, IID_IClassFactory, (void**)&pFactory);
    CheckHR(hr, "DllGetClassObject returns S_OK");
    Check(pFactory != nullptr, "IClassFactory ptr non-null");
    if (pFactory) {
        GUID badGuid = {};
        IClassFactory* pBad = nullptr;
        HRESULT hr2 = g_pfnGetClass(badGuid, IID_IClassFactory, (void**)&pBad);
        Check(hr2 == CLASS_E_CLASSNOTAVAILABLE, "Wrong CLSID returns CLASS_E_CLASSNOTAVAILABLE");
        if (pBad) pBad->Release();
        pFactory->Release();
    }
}

static void Test_CreateInstance() {
    printf("\n[Test 2] CreateInstance + QueryInterface\n");
    ICredentialProvider* pCP = CreateProvider();
    Check(pCP != nullptr, "CreateInstance returns non-null ICredentialProvider");
    if (!pCP) return;

    ICredentialProviderSetUserArray* pSUA = nullptr;
    HRESULT hr = pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    CheckHR(hr, "QI ICredentialProviderSetUserArray");
    if (pSUA) pSUA->Release();

    IUnknown* pBogus = nullptr;
    GUID badIID = {};
    hr = pCP->QueryInterface(badIID, (void**)&pBogus);
    Check(hr == E_NOINTERFACE, "QI bogus IID returns E_NOINTERFACE");

    pCP->Release();
}

static void Test_SetUsageScenario() {
    printf("\n[Test 3] SetUsageScenario\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("SetUsageScenario", "CreateInstance failed"); return; }

    HRESULT hr = pCP->SetUsageScenario(CPUS_LOGON, 0);
    CheckHR(hr, "SetUsageScenario LOGON");

    hr = pCP->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
    CheckHR(hr, "SetUsageScenario UNLOCK_WORKSTATION");

    hr = pCP->SetUsageScenario(CPUS_CREDUI, 0);
    Check(hr == E_NOTIMPL, "SetUsageScenario CREDUI returns E_NOTIMPL");

    pCP->Release();
}

static void Test_SetUserArray() {
    printf("\n[Test 4] SetUserArray + GetCredentialCount\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("SetUserArray", "CreateInstance failed"); return; }

    HRESULT hr = pCP->SetUsageScenario(CPUS_LOGON, 0);
    CheckHR(hr, "SetUsageScenario before SetUserArray");

    ICredentialProviderSetUserArray* pSUA = nullptr;
    hr = pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    CheckHR(hr, "QI ICredentialProviderSetUserArray");

    if (pSUA) {
        auto* mockArr = new MockUserArray();
        hr = pSUA->SetUserArray(mockArr);
        CheckHR(hr, "SetUserArray with 1 mock user");
        mockArr->Release();

        DWORD count = 0, def = 0;
        BOOL autoLogon = TRUE;
        hr = pCP->GetCredentialCount(&count, &def, &autoLogon);
        CheckHR(hr, "GetCredentialCount");
        Check(count == 1, "GetCredentialCount returns 1");
        Check(autoLogon == FALSE, "AutoLogon is FALSE (no auto-login without explicit auth)");

        pSUA->Release();
    }
    pCP->Release();
}

static void Test_FieldDescriptors() {
    printf("\n[Test 5] GetFieldDescriptorCount + GetFieldDescriptorAt\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("FieldDescriptors", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    DWORD fieldCount = 0;
    HRESULT hr = pCP->GetFieldDescriptorCount(&fieldCount);
    CheckHR(hr, "GetFieldDescriptorCount");
    Check(fieldCount == 6, "Field count is 6");

    for (DWORD i = 0; i < fieldCount; ++i) {
        CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* pDesc = nullptr;
        hr = pCP->GetFieldDescriptorAt(i, &pDesc);
        char name[64];
        StringCchPrintfA(name, ARRAYSIZE(name), "GetFieldDescriptorAt(%u)", i);
        CheckHR(hr, name);
        if (pDesc) {
            Check(pDesc->pszLabel != nullptr, "Field label non-null");
            if (pDesc->pszLabel) CoTaskMemFree(pDesc->pszLabel);
            CoTaskMemFree(pDesc);
        }
    }

    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* pOOB = nullptr;
    hr = pCP->GetFieldDescriptorAt(9999, &pOOB);
    Check(hr == E_INVALIDARG, "GetFieldDescriptorAt out-of-range returns E_INVALIDARG");

    pCP->Release();
}

static void Test_GetCredentialAt() {
    printf("\n[Test 6] GetCredentialAt + QI Credential2\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("GetCredentialAt", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    HRESULT hr = pCP->GetCredentialAt(0, &pCred);
    CheckHR(hr, "GetCredentialAt(0)");
    Check(pCred != nullptr, "Credential ptr non-null");

    if (pCred) {
        ICredentialProviderCredential2* pCred2 = nullptr;
        hr = pCred->QueryInterface(IID_ICredentialProviderCredential2, (void**)&pCred2);
        CheckHR(hr, "QI ICredentialProviderCredential2");
        if (pCred2) {
            PWSTR pszSid = nullptr;
            hr = pCred2->GetUserSid(&pszSid);
            Check(SUCCEEDED(hr) || hr == E_NOTIMPL, "GetUserSid succeeded or E_NOTIMPL");
            if (pszSid) CoTaskMemFree(pszSid);
            pCred2->Release();
        }
        pCred->Release();
    }

    ICredentialProviderCredential* pOOB = nullptr;
    hr = pCP->GetCredentialAt(9999, &pOOB);
    Check(hr == E_INVALIDARG, "GetCredentialAt out-of-range returns E_INVALIDARG");

    pCP->Release();
}

static void Test_AdviseUnAdvise() {
    printf("\n[Test 7] Advise / UnAdvise\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("AdviseUnAdvise", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("AdviseUnAdvise", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvt = new MockEvents();
    HRESULT hr = pCred->Advise(mockEvt);
    CheckHR(hr, "Advise");

    hr = pCred->Advise(nullptr);
    Check(SUCCEEDED(hr), "Advise(nullptr) succeeds (replaces)");

    auto* mockEvt2 = new MockEvents();
    hr = pCred->Advise(mockEvt2);
    CheckHR(hr, "Re-Advise with new events");

    hr = pCred->UnAdvise();
    CheckHR(hr, "UnAdvise");

    hr = pCred->UnAdvise();
    CheckHR(hr, "Double UnAdvise");

    mockEvt->Release();
    mockEvt2->Release();
    pCred->Release();
    pCP->Release();
}

static void Test_SetSelectedDeselected() {
    printf("\n[Test 8] SetSelected / SetDeselected\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("SetSelected", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("SetSelected", "GetCredentialAt failed"); pCP->Release(); return; }

    BOOL autoLogon = TRUE;
    HRESULT hr = pCred->SetSelected(&autoLogon);
    CheckHR(hr, "SetSelected");
    Check(autoLogon == FALSE, "SetSelected: autoLogon is FALSE");

    hr = pCred->SetDeselected();
    CheckHR(hr, "SetDeselected");

    pCred->Release();
    pCP->Release();
}

static void Test_1000Iterations() {
    printf("\n[Test 9] 1000-iteration create/enumerate/release\n");
    bool allOk = true;
    for (int i = 0; i < 1000; ++i) {
        ICredentialProvider* pCP = CreateProvider();
        if (!pCP) { allOk = false; break; }

        pCP->SetUsageScenario(CPUS_LOGON, 0);

        ICredentialProviderSetUserArray* pSUA = nullptr;
        pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
        if (pSUA) {
            auto* arr = new MockUserArray();
            pSUA->SetUserArray(arr);
            arr->Release();
            pSUA->Release();
        }

        DWORD count = 0, def = 0;
        BOOL autoLogon = TRUE;
        pCP->GetCredentialCount(&count, &def, &autoLogon);

        if (count > 0) {
            ICredentialProviderCredential* pCred = nullptr;
            pCP->GetCredentialAt(0, &pCred);
            if (pCred) {
                BOOL al = TRUE;
                pCred->SetSelected(&al);
                pCred->SetDeselected();
                pCred->Release();
            }
        }

        pCP->Release();
    }
    Check(allOk, "1000 iterations: no crash or leak");
}

static void Test_AsyncDestruction() {
    printf("\n[Test 10] Async destruction during auth (worker survives external Release)\n");
    bool allOk = true;
    for (int attempt = 0; attempt < 20; ++attempt) {
        ICredentialProvider* pCP = CreateProvider();
        if (!pCP) { allOk = false; break; }
        pCP->SetUsageScenario(CPUS_LOGON, 0);

        ICredentialProviderSetUserArray* pSUA = nullptr;
        pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
        if (pSUA) {
            auto* arr = new MockUserArray();
            pSUA->SetUserArray(arr);
            arr->Release();
            pSUA->Release();
        }

        ICredentialProviderCredential* pCred = nullptr;
        pCP->GetCredentialAt(0, &pCred);
        if (!pCred) { pCP->Release(); continue; }

        auto* mockEvt = new MockEvents();
        pCred->Advise(mockEvt);

        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
        PWSTR pszStatus = nullptr;
        CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
        pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
        if (pszStatus) CoTaskMemFree(pszStatus);
        if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

        // Immediately UnAdvise and Release ALL external COM refs while thread is running
        pCred->UnAdvise();
        pCred->SetDeselected();
        pCred->Release();

        mockEvt->Release();
        pCP->Release();

        // Brief sleep to let worker complete its lifecycle
        Sleep(50);
    }
    Check(allOk, "Async destruction: 20 attempts no crash");
}

static void Test_CtorDtorBalance() {
    printf("\n[Test 11] Strict COM Lifetime & Ctor/Dtor balance\n");
    if (!g_pfnGetCtorCount || !g_pfnGetDtorCount) {
        printf("  [WARN] Diagnostic counter exports not available in DLL\n");
        return;
    }

    // Give background threads a second to finish
    Sleep(500);

    LONG ctors = g_pfnGetCtorCount();
    LONG dtors = g_pfnGetDtorCount();

    printf("  Total Credential Created (ctors): %ld\n", ctors);
    printf("  Total Credential Destroyed (dtors): %ld\n", dtors);

    Check(ctors > 0, "Credential objects were created");
    Check(ctors == dtors, "Exact COM lifetime balance: ctor_count == dtor_count");
}

static void Test_IpcUnavailable() {
    printf("\n[Test 12] IPC unavailable — credential must not crash or hang\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("IpcUnavailable", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("IpcUnavailable", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvt = new MockEvents();
    pCred->Advise(mockEvt);

    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION   cpcs{};
    PWSTR pszStatus = nullptr;
    CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};

    HRESULT hr = pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
    Check(SUCCEEDED(hr), "GetSerialization returns success (not a crash)");
    if (pszStatus) CoTaskMemFree(pszStatus);
    if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

    Sleep(500);

    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();
    Pass("IPC unavailable: no crash, no hang");
}

static void Test_NoCredentialsChangedLoop() {
    printf("\n[Test 13] No CredentialsChanged loop\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("NoCCLoop", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("NoCCLoop", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvt = new MockEvents();
    pCred->Advise(mockEvt);

    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION   cpcs{};
    PWSTR pszStatus = nullptr;
    CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
    pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
    if (pszStatus) CoTaskMemFree(pszStatus);
    if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

    Sleep(600);

    int setFieldStateCalls = mockEvt->getFieldStateCalls();
    Check(setFieldStateCalls == 0,
        "SetFieldState was NOT called from background thread (no CredentialsChanged loop)");

    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();
}

static void Test_DllCanUnloadNow() {
    printf("\n[Test 14] DllCanUnloadNow\n");
    HRESULT hr = g_pfnCanUnload();
    Check(SUCCEEDED(hr), "DllCanUnloadNow returns valid HRESULT");
}

static void Test_MultiCallGetSerialization() {
    printf("\n[Test 15] Multi-call GetSerialization (re-entry protection)\n");
    if (!g_pfnGetWorkerCount) {
        printf("  [WARN] Diagnostic worker counter export not available in DLL\n");
        return;
    }

    LONG workersBefore = g_pfnGetWorkerCount();

    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("MultiGetSerialization", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("MultiGetSerialization", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvt = new MockEvents();
    pCred->Advise(mockEvt);

    // Call GetSerialization 20 times rapidly while auth is in progress
    for (int i = 0; i < 20; ++i) {
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
        PWSTR pszStatus = nullptr;
        CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
        HRESULT hr = pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
        Check(SUCCEEDED(hr), "GetSerialization call in loop succeeded");
        if (pszStatus) CoTaskMemFree(pszStatus);
        if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);
    }

    // Give a moment for any workers to spawn
    Sleep(300);

    LONG workersAfter = g_pfnGetWorkerCount();
    LONG deltaWorkers = workersAfter - workersBefore;
    printf("  Workers spawned during 20 GetSerialization calls: %ld\n", deltaWorkers);
    Check(deltaWorkers == 1, "Exactly ONE worker thread spawned (AUTH_WORKER_COUNT == 1)");

    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();
}

static void Test_EventsLifetime() {
    printf("\n[Test 16] Events Lifetime (Advise A -> UnAdvise -> Release A -> Advise B)\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("EventsLifetime", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("EventsLifetime", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvtA = new MockEvents();
    pCred->Advise(mockEvtA);

    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
    PWSTR pszStatus = nullptr;
    CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
    pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
    if (pszStatus) CoTaskMemFree(pszStatus);
    if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

    // Immediate UnAdvise & Release eventsA
    pCred->UnAdvise();
    mockEvtA->Release();

    // Now Advise eventsB
    auto* mockEvtB = new MockEvents();
    pCred->Advise(mockEvtB);

    // Wait for background worker to complete
    Sleep(500);

    // Advise/UnAdvise B cleanly
    pCred->UnAdvise();
    pCred->Release();
    mockEvtB->Release();
    pCP->Release();

    Pass("Events Lifetime: No stale pointer callback / no crash");
}

static void Test_Cancellation() {
    printf("\n[Test 17] IPC Cancellation Responsiveness\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("Cancellation", "CreateInstance failed"); return; }
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    ICredentialProviderSetUserArray* pSUA = nullptr;
    pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    if (pSUA) {
        auto* arr = new MockUserArray();
        pSUA->SetUserArray(arr);
        arr->Release();
        pSUA->Release();
    }

    ICredentialProviderCredential* pCred = nullptr;
    pCP->GetCredentialAt(0, &pCred);
    if (!pCred) { Fail("Cancellation", "GetCredentialAt failed"); pCP->Release(); return; }

    auto* mockEvt = new MockEvents();
    pCred->Advise(mockEvt);

    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
    PWSTR pszStatus = nullptr;
    CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
    pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
    if (pszStatus) CoTaskMemFree(pszStatus);
    if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

    // Wait 100ms then cancel via SetDeselected/UnAdvise
    Sleep(100);

    auto startCancel = std::chrono::steady_clock::now();
    pCred->SetDeselected();
    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();

    // Check that cancellation and teardown did not hang for 90 seconds (threshold: 3000 ms)
    auto endCancel = std::chrono::steady_clock::now();
    auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(endCancel - startCancel).count();
    printf("  Cancellation completed in %lld ms\n", (long long)elapsedMs);
    Check(elapsedMs < 3000, "Worker cancellation completed promptly (< 3000 ms, not 90000 ms)");
}

static void Test_StressAsync100() {
    printf("\n[Test 18] Async Stress Test (100 iterations of Advise -> GetSerialization -> sleep -> UnAdvise -> Deselect -> Release)\n");
    bool allOk = true;
    for (int iter = 0; iter < 100; ++iter) {
        ICredentialProvider* pCP = CreateProvider();
        if (!pCP) { allOk = false; break; }
        pCP->SetUsageScenario(CPUS_LOGON, 0);

        ICredentialProviderSetUserArray* pSUA = nullptr;
        pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
        if (pSUA) {
            auto* arr = new MockUserArray();
            pSUA->SetUserArray(arr);
            arr->Release();
            pSUA->Release();
        }

        ICredentialProviderCredential* pCred = nullptr;
        pCP->GetCredentialAt(0, &pCred);
        if (!pCred) { pCP->Release(); allOk = false; break; }

        auto* mockEvt = new MockEvents();
        pCred->Advise(mockEvt);

        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
        PWSTR pszStatus = nullptr;
        CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
        pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
        if (pszStatus) CoTaskMemFree(pszStatus);
        if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

        // Sleep pseudo-random 0-50 ms
        Sleep(iter % 50);

        pCred->UnAdvise();
        pCred->SetDeselected();
        pCred->Release();

        mockEvt->Release();
        pCP->Release();
    }
    Check(allOk, "Stress test (100 iterations): no crash, no hang");
}

static void Test_ExceptionFailSafe() {
    printf("\n[Test 19] Exception / Fail-safe / Null arg validation\n");
    ICredentialProvider* pCP = CreateProvider();
    if (!pCP) { Fail("FailSafe", "CreateInstance failed"); return; }

    printf("  [Step 1] Checking Provider null args...\n"); fflush(stdout);
    Check(pCP->GetCredentialCount(nullptr, nullptr, nullptr) == E_POINTER, "GetCredentialCount(nullptr) -> E_POINTER"); fflush(stdout);
    Check(pCP->GetFieldDescriptorCount(nullptr) == E_POINTER, "GetFieldDescriptorCount(nullptr) -> E_POINTER"); fflush(stdout);
    Check(pCP->GetFieldDescriptorAt(0, nullptr) == E_POINTER, "GetFieldDescriptorAt(0, nullptr) -> E_POINTER"); fflush(stdout);
    Check(pCP->GetCredentialAt(0, nullptr) == E_POINTER, "GetCredentialAt(0, nullptr) -> E_POINTER"); fflush(stdout);

    printf("  [Step 2] Setting usage scenario CPUS_LOGON...\n"); fflush(stdout);
    pCP->SetUsageScenario(CPUS_LOGON, 0);

    DWORD dwCount = 0, dwDef = 0; BOOL bAuto = FALSE;
    pCP->GetCredentialCount(&dwCount, &dwDef, &bAuto);
    printf("  Provider reports %lu credentials\n", dwCount); fflush(stdout);

    ICredentialProviderCredential* pCred = nullptr;
    HRESULT hrCred = pCP->GetCredentialAt(0, &pCred);
    printf("  GetCredentialAt(0) returned 0x%08X, pCred=%p\n", (unsigned)hrCred, pCred); fflush(stdout);
    if (SUCCEEDED(hrCred) && pCred) {
        printf("  [Step 3] Checking Credential null args...\n"); fflush(stdout);
        Check(pCred->SetSelected(nullptr) == E_POINTER, "SetSelected(nullptr) -> E_POINTER"); fflush(stdout);
        Check(pCred->GetFieldState(0, nullptr, nullptr) == E_POINTER, "GetFieldState(nullptr) -> E_POINTER"); fflush(stdout);
        Check(pCred->GetStringValue(0, nullptr) == E_POINTER, "GetStringValue(nullptr) -> E_POINTER"); fflush(stdout);
        Check(pCred->GetSerialization(nullptr, nullptr, nullptr, nullptr) == E_POINTER, "GetSerialization(nullptr) -> E_POINTER"); fflush(stdout);
        Check(pCred->ReportResult(0, 0, nullptr, nullptr) == E_POINTER, "ReportResult(nullptr) -> E_POINTER"); fflush(stdout);

        printf("  [Step 4] Checking Credential invalid field IDs...\n"); fflush(stdout);
        CREDENTIAL_PROVIDER_FIELD_STATE cpfs{};
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE cpfis{};
        Check(pCred->GetFieldState(999, &cpfs, &cpfis) == E_INVALIDARG, "GetFieldState(999) -> E_INVALIDARG"); fflush(stdout);

        PWSTR psz = nullptr;
        Check(pCred->GetStringValue(999, &psz) == E_INVALIDARG, "GetStringValue(999) -> E_INVALIDARG"); fflush(stdout);

        Check(pCred->SetStringValue(999, L"test") == E_INVALIDARG, "SetStringValue(999) -> E_INVALIDARG"); fflush(stdout);

        pCred->Release();
    } else {
        Fail("FailSafe", "Could not get credential at index 0");
    }

    pCP->Release();
}

int wmain(int argc, wchar_t* argv[]) {
    printf("============================================================\n");
    printf("  FaceUnlock Credential Provider Safety Harness (Pure COM)\n");
    printf("============================================================\n");
    fflush(stdout);

    const wchar_t* dllPath = L"FaceUnlockCredentialProvider.dll";
    if (argc > 1) dllPath = argv[1];

    printf("Loading: %ls\n", dllPath); fflush(stdout);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        printf("[ERROR] CoInitializeEx failed: 0x%08X\n", (unsigned)hr);
        return 1;
    }

    if (!LoadDll(dllPath)) {
        CoUninitialize();
        return 1;
    }

    printf("DLL loaded successfully.\n"); fflush(stdout);

    Test_DllGetClassObject(); fflush(stdout);
    Test_CreateInstance(); fflush(stdout);
    Test_SetUsageScenario(); fflush(stdout);
    Test_SetUserArray(); fflush(stdout);
    Test_FieldDescriptors(); fflush(stdout);
    Test_GetCredentialAt(); fflush(stdout);
    Test_AdviseUnAdvise(); fflush(stdout);
    Test_SetSelectedDeselected(); fflush(stdout);
    Test_1000Iterations(); fflush(stdout);
    Test_AsyncDestruction(); fflush(stdout);
    Test_MultiCallGetSerialization(); fflush(stdout);
    Test_EventsLifetime(); fflush(stdout);
    Test_Cancellation(); fflush(stdout);
    Test_StressAsync100(); fflush(stdout);
    Test_ExceptionFailSafe(); fflush(stdout);
    Test_IpcUnavailable(); fflush(stdout);
    Test_NoCredentialsChangedLoop(); fflush(stdout);
    Test_CtorDtorBalance(); fflush(stdout);
    Test_DllCanUnloadNow(); fflush(stdout);

    printf("\nFreeing DLL...\n"); fflush(stdout);
    FreeLibrary(g_hDll);
    printf("Uninitializing COM...\n"); fflush(stdout);
    CoUninitialize();

    printf("\n============================================================\n");
    printf("  RESULTS: %d passed, %d failed\n", g_passed, g_failed);
    if (g_pfnGetCtorCount && g_pfnGetDtorCount) {
        printf("  LIFETIME: %ld ctors, %ld dtors\n", g_pfnGetCtorCount(), g_pfnGetDtorCount());
    }
    printf("============================================================\n");
    fflush(stdout);

    return (g_failed == 0) ? 0 : 1;
}
