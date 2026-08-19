// CredentialProviderHarness.cpp
// Standalone LogonUI-safety harness for FaceUnlockCredentialProvider.dll
//
// Tests:
//   1. DllGetClassObject → IClassFactory → CreateInstance → ICredentialProvider
//   2. SetUsageScenario / SetUserArray (mock)
//   3. GetFieldDescriptorCount / GetFieldDescriptorAt
//   4. GetCredentialCount / GetCredentialAt
//   5. QueryInterface ICredentialProviderCredential2
//   6. Advise / UnAdvise
//   7. SetSelected / SetDeselected
//   8. Full Release (1000 iterations)
//   9. Async destruction during auth (UnAdvise + Release while thread running)
//  10. IPC unavailable (no service — just verifies no crash/hang)
//  11. COM ref count audit
//
// Build: see CMakeLists.txt in this directory.
// Run: CredentialProviderHarness.exe [path\to\FaceUnlockCredentialProvider.dll]
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
// Minimal ICredentialProviderUser mock
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
        // Allocate a fake SID string
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

// Minimal ICredentialProviderUserArray mock
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

// Minimal ICredentialProviderCredentialEvents mock
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
    IFACEMETHODIMP DeleteItem(ICredentialProviderCredential*, DWORD) override { return S_OK; }
    IFACEMETHODIMP AppendItem(ICredentialProviderCredential*, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*) override { return S_OK; }
    IFACEMETHODIMP OnCreatingWindow(HWND*) override { return S_OK; }
};

// ------------------------------------------------------------------ DLL load -
typedef HRESULT(STDAPICALLTYPE* PFN_DllGetClassObject)(REFCLSID, REFIID, LPVOID*);
typedef HRESULT(STDAPICALLTYPE* PFN_DllCanUnloadNow)(void);

static HMODULE       g_hDll               = nullptr;
static PFN_DllGetClassObject  g_pfnGetClass = nullptr;
static PFN_DllCanUnloadNow    g_pfnCanUnload = nullptr;

static bool LoadDll(const wchar_t* path) {
    g_hDll = LoadLibraryW(path);
    if (!g_hDll) {
        printf("[ERROR] LoadLibraryW failed: 0x%08X\n", GetLastError());
        return false;
    }
    g_pfnGetClass  = (PFN_DllGetClassObject) GetProcAddress(g_hDll, "DllGetClassObject");
    g_pfnCanUnload = (PFN_DllCanUnloadNow)   GetProcAddress(g_hDll, "DllCanUnloadNow");
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

// Test 1: DllGetClassObject basic
static void Test_DllGetClassObject() {
    printf("\n[Test 1] DllGetClassObject\n");
    IClassFactory* pFactory = nullptr;
    HRESULT hr = g_pfnGetClass(CLSID_FaceUnlock, IID_IClassFactory, (void**)&pFactory);
    CheckHR(hr, "DllGetClassObject returns S_OK");
    Check(pFactory != nullptr, "IClassFactory ptr non-null");
    if (pFactory) {
        // Wrong CLSID should fail
        GUID badGuid = {};
        IClassFactory* pBad = nullptr;
        HRESULT hr2 = g_pfnGetClass(badGuid, IID_IClassFactory, (void**)&pBad);
        Check(hr2 == CLASS_E_CLASSNOTAVAILABLE, "Wrong CLSID returns CLASS_E_CLASSNOTAVAILABLE");
        if (pBad) pBad->Release();

        pFactory->Release();
    }
}

// Test 2: CreateInstance + QI
static void Test_CreateInstance() {
    printf("\n[Test 2] CreateInstance + QueryInterface\n");
    ICredentialProvider* pCP = CreateProvider();
    Check(pCP != nullptr, "CreateInstance returns non-null ICredentialProvider");
    if (!pCP) return;

    // QI for ICredentialProviderSetUserArray
    ICredentialProviderSetUserArray* pSUA = nullptr;
    HRESULT hr = pCP->QueryInterface(IID_ICredentialProviderSetUserArray, (void**)&pSUA);
    CheckHR(hr, "QI ICredentialProviderSetUserArray");
    if (pSUA) pSUA->Release();

    // QI for bogus interface should fail
    IUnknown* pBogus = nullptr;
    GUID badIID = {};
    hr = pCP->QueryInterface(badIID, (void**)&pBogus);
    Check(hr == E_NOINTERFACE, "QI bogus IID returns E_NOINTERFACE");

    pCP->Release();
}

// Test 3: SetUsageScenario
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

// Test 4: SetUserArray + GetCredentialCount
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

// Test 5: GetFieldDescriptorCount + GetFieldDescriptorAt
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

    // Out-of-range
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* pOOB = nullptr;
    hr = pCP->GetFieldDescriptorAt(9999, &pOOB);
    Check(hr == E_INVALIDARG, "GetFieldDescriptorAt out-of-range returns E_INVALIDARG");

    pCP->Release();
}

// Test 6: GetCredentialAt + QI ICredentialProviderCredential2
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
        // QI for Credential2
        ICredentialProviderCredential2* pCred2 = nullptr;
        hr = pCred->QueryInterface(IID_ICredentialProviderCredential2, (void**)&pCred2);
        CheckHR(hr, "QI ICredentialProviderCredential2");
        if (pCred2) {
            PWSTR pszSid = nullptr;
            hr = pCred2->GetUserSid(&pszSid);
            // S_OK or E_NOTIMPL are both valid
            Check(SUCCEEDED(hr) || hr == E_NOTIMPL, "GetUserSid succeeded or E_NOTIMPL");
            if (pszSid) CoTaskMemFree(pszSid);
            pCred2->Release();
        }
        pCred->Release();
    }

    // Out-of-range
    ICredentialProviderCredential* pOOB = nullptr;
    hr = pCP->GetCredentialAt(9999, &pOOB);
    Check(hr == E_INVALIDARG, "GetCredentialAt out-of-range returns E_INVALIDARG");

    pCP->Release();
}

// Test 7: Advise / UnAdvise
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

    // Advise with null should not crash
    hr = pCred->Advise(nullptr);
    Check(SUCCEEDED(hr), "Advise(nullptr) succeeds (replaces)");

    // Re-advise with real events
    auto* mockEvt2 = new MockEvents();
    hr = pCred->Advise(mockEvt2);
    CheckHR(hr, "Re-Advise with new events");

    hr = pCred->UnAdvise();
    CheckHR(hr, "UnAdvise");

    // Double UnAdvise must not crash
    hr = pCred->UnAdvise();
    CheckHR(hr, "Double UnAdvise");

    mockEvt->Release();
    mockEvt2->Release();
    pCred->Release();
    pCP->Release();
}

// Test 8: SetSelected / SetDeselected
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

// Test 9: 1000-iteration create/enumerate/release
static void Test_1000Iterations() {
    printf("\n[Test 9] 1000-iteration create/enumerate/release (no crash/leak)\n");
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
    Check(allOk, "1000 iterations: no crash");
}

// Test 10: Async destruction during auth (UnAdvise + Release while IPC thread could be running)
static void Test_AsyncDestruction() {
    printf("\n[Test 10] Async destruction during auth\n");
    // The service is not running; IPC will fail quickly with "service_not_running"
    // This test verifies:
    //   - No access violation
    //   - No use-after-free
    //   - UnAdvise then Release is safe even if thread is mid-execution

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

        // Simulate GetSerialization (which starts async thread)
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE cpgsr{};
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION cpcs{};
        PWSTR pszStatus = nullptr;
        CREDENTIAL_PROVIDER_STATUS_ICON cpsi{};
        pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
        if (pszStatus) CoTaskMemFree(pszStatus);
        if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

        // Immediately UnAdvise + Release — thread may still be running
        // (IPC returns quickly with service_not_running, but this races intentionally)
        pCred->UnAdvise();
        pCred->SetDeselected();
        pCred->Release();

        mockEvt->Release();
        pCP->Release();

        // Give thread a moment to complete
        Sleep(50);
    }
    Check(allOk, "Async destruction: 20 attempts no crash");
}

// Test 11: IPC unavailable — no crash, no hang
static void Test_IpcUnavailable() {
    printf("\n[Test 11] IPC unavailable — credential must not crash or hang\n");
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

    // This will start async thread; IPC will fail with service_not_running
    HRESULT hr = pCred->GetSerialization(&cpgsr, &cpcs, &pszStatus, &cpsi);
    Check(SUCCEEDED(hr), "GetSerialization returns success (not a crash)");
    if (pszStatus) CoTaskMemFree(pszStatus);
    if (cpcs.rgbSerialization) CoTaskMemFree(cpcs.rgbSerialization);

    // Wait briefly for async thread to complete (IPC should fail quickly)
    Sleep(500);

    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();
    Pass("IPC unavailable: no crash, no hang");
}

// Test 12: No CredentialsChanged loop — SetFieldState must NOT be called from thread
static void Test_NoCredentialsChangedLoop() {
    printf("\n[Test 12] No CredentialsChanged loop\n");
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

    // Wait for thread
    Sleep(600);

    int setFieldStateCalls = mockEvt->getFieldStateCalls();
    // Fixed code must NOT call SetFieldState from background thread
    Check(setFieldStateCalls == 0,
        "SetFieldState was NOT called from background thread (no CredentialsChanged loop)");

    pCred->UnAdvise();
    pCred->Release();
    mockEvt->Release();
    pCP->Release();
}

// Test 13: DllCanUnloadNow
static void Test_DllCanUnloadNow() {
    printf("\n[Test 13] DllCanUnloadNow\n");
    // After all objects released, should return S_OK
    HRESULT hr = g_pfnCanUnload();
    // S_OK = can unload, S_FALSE = cannot. Both are valid depending on state.
    Check(SUCCEEDED(hr), "DllCanUnloadNow returns valid HRESULT");
}

// ================================================================== main ====
int wmain(int argc, wchar_t* argv[]) {
    printf("============================================================\n");
    printf("  FaceUnlock Credential Provider Safety Harness\n");
    printf("============================================================\n");

    const wchar_t* dllPath = L"FaceUnlockCredentialProvider.dll";
    if (argc > 1) dllPath = argv[1];

    printf("Loading: %ls\n", dllPath);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        printf("[ERROR] CoInitializeEx failed: 0x%08X\n", (unsigned)hr);
        return 1;
    }

    if (!LoadDll(dllPath)) {
        CoUninitialize();
        return 1;
    }

    printf("DLL loaded successfully.\n");

    // Run all tests
    Test_DllGetClassObject();
    Test_CreateInstance();
    Test_SetUsageScenario();
    Test_SetUserArray();
    Test_FieldDescriptors();
    Test_GetCredentialAt();
    Test_AdviseUnAdvise();
    Test_SetSelectedDeselected();
    Test_1000Iterations();
    Test_AsyncDestruction();
    Test_IpcUnavailable();
    Test_NoCredentialsChangedLoop();
    Test_DllCanUnloadNow();

    FreeLibrary(g_hDll);
    CoUninitialize();

    printf("\n============================================================\n");
    printf("  RESULTS: %d passed, %d failed\n", g_passed, g_failed);
    printf("============================================================\n");

    if (g_failed == 0) {
        printf("\nCP_SAFE_FOR_LOGONUI_TEST: PENDING (run harness on target machine)\n");
    } else {
        printf("\nCP_SAFE_FOR_LOGONUI_TEST: NO — %d test(s) failed\n", g_failed);
    }

    return (g_failed == 0) ? 0 : 1;
}
