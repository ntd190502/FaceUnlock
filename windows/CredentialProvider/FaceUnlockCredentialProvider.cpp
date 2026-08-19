#include "FaceUnlockCredentialProvider.h"
#include <new>
#include <strsafe.h>
#include <shlwapi.h>

// {64D6E84B-4969-4B59-A11A-58C3D9FA0110}
const CLSID CLSID_FaceUnlockProvider = {0x64d6e84b, 0x4969, 0x4b59, {0xa1, 0x1a, 0x58, 0xc3, 0xd9, 0xfa, 0x01, 0x10}};

enum FACEUNLOCK_FIELD_ID {
    FID_LARGE_TEXT = 0,
    FID_SMALL_TEXT = 1,
    FID_SUBMIT = 2,
    FID_STATUS_TEXT = 3,
    FID_NUM_FIELDS = 4
};

static const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR s_Fields[FID_NUM_FIELDS] = {
    { FID_LARGE_TEXT, CPFT_LARGE_TEXT, L"FaceUnlock" },
    { FID_SMALL_TEXT, CPFT_SMALL_TEXT, L"Unlock with iPhone" },
    { FID_SUBMIT,     CPFT_SUBMIT_BUTTON, L"Face ID" },
    { FID_STATUS_TEXT, CPFT_SMALL_TEXT, L"Status" }
};

static HRESULT DuplicateString(PCWSTR src, PWSTR* dst) {
    if (!dst) return E_POINTER;
    *dst = nullptr;
    if (!src) return S_OK;
    size_t len = 0;
    HRESULT hr = StringCchLengthW(src, STRSAFE_MAX_CCH, &len);
    if (FAILED(hr)) return hr;
    PWSTR buf = static_cast<PWSTR>(CoTaskMemAlloc((len + 1) * sizeof(WCHAR)));
    if (!buf) return E_OUTOFMEMORY;
    hr = StringCchCopyW(buf, len + 1, src);
    if (FAILED(hr)) {
        CoTaskMemFree(buf);
        return hr;
    }
    *dst = buf;
    return S_OK;
}

class FaceUnlockCredential final : public ICredentialProviderCredential {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;
    ICredentialProviderEvents* events_ = nullptr;
    UINT_PTR adviseContext_ = 0;
    WCHAR statusMessage_[256] = L"Phase A: Ready for authentication.";

public:
    FaceUnlockCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus) : usage_(cpus) {}

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential) {
            *ppv = static_cast<ICredentialProviderCredential*>(this);
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

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderEvents* pcpEvents, UINT_PTR upAdviseContext) override {
        events_ = pcpEvents;
        adviseContext_ = upAdviseContext;
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override {
        events_ = nullptr;
        adviseContext_ = 0;
        return S_OK;
    }

    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override {
        if (!pbAutoLogon) return E_POINTER;
        *pbAutoLogon = FALSE;
        return S_OK;
    }

    IFACEMETHODIMP SetDeselected() override {
        return S_OK;
    }

    IFACEMETHODIMP GetFieldState(
        DWORD dwFieldID,
        CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override {
        if (!pcpfs || !pcpfis) return E_POINTER;
        switch (dwFieldID) {
        case FID_LARGE_TEXT:
        case FID_SMALL_TEXT:
            *pcpfs = CPFS_DISPLAY_IN_BOTH;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_SUBMIT:
            *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = CPFIS_FOCUSED;
            return S_OK;
        case FID_STATUS_TEXT:
            *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        default:
            return E_INVALIDARG;
        }
    }

    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, PWSTR* ppsz) override {
        if (!ppsz) return E_POINTER;
        *ppsz = nullptr;
        switch (dwFieldID) {
        case FID_LARGE_TEXT:
            return DuplicateString(L"FaceUnlock", ppsz);
        case FID_SMALL_TEXT:
            return DuplicateString(
                (usage_ == CPUS_UNLOCK_WORKSTATION) ? L"Unlock with iPhone Face ID" : L"Sign in with iPhone Face ID",
                ppsz);
        case FID_STATUS_TEXT:
            return DuplicateString(statusMessage_, ppsz);
        default:
            return E_INVALIDARG;
        }
    }

    IFACEMETHODIMP GetBitmapValue(DWORD, HBITMAP*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL*, PWSTR*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override {
        if (!pdwAdjacentTo) return E_POINTER;
        if (dwFieldID == FID_SUBMIT) {
            *pdwAdjacentTo = FID_STATUS_TEXT;
            return S_OK;
        }
        return E_INVALIDARG;
    }

    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetStringValue(DWORD, PCWSTR) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP CommandLinkClicked(DWORD) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetSerialization(
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
        PWSTR* ppszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override {
        if (!pcpgsr || !pcpcs || !ppszOptionalStatusText || !pcpsiOptionalStatusIcon)
            return E_POINTER;

        *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
        pcpcs->clsidCredentialProvider = CLSID_FaceUnlockProvider;
        pcpcs->rgbSerialization = nullptr;
        pcpcs->cbSerialization = 0;
        pcpcs->ulAuthenticationPackage = 0;

        *pcpsiOptionalStatusIcon = CPSI_WARNING;
        return DuplicateString(L"FaceUnlock Phase A: Credential tile active (Authentication wiring in Phase B).", ppszOptionalStatusText);
    }

    IFACEMETHODIMP ReportResult(
        NTSTATUS,
        NTSTATUS,
        PWSTR* ppszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override {
        if (!ppszOptionalStatusText || !pcpsiOptionalStatusIcon) return E_POINTER;
        *ppszOptionalStatusText = nullptr;
        *pcpsiOptionalStatusIcon = CPSI_NONE;
        return S_OK;
    }
};

class Provider final : public ICredentialProvider {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;
    FaceUnlockCredential* credential_ = nullptr;

public:
    Provider() = default;

    ~Provider() {
        if (credential_) {
            credential_->Release();
            credential_ = nullptr;
        }
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProvider) {
            *ppv = static_cast<ICredentialProvider*>(this);
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

    // ICredentialProvider
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD) override {
        if (cpus != CPUS_LOGON && cpus != CPUS_UNLOCK_WORKSTATION) {
            return E_NOTIMPL;
        }
        usage_ = cpus;
        if (credential_) {
            credential_->Release();
            credential_ = nullptr;
        }
        credential_ = new(std::nothrow) FaceUnlockCredential(cpus);
        return credential_ ? S_OK : E_OUTOFMEMORY;
    }

    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Advise(ICredentialProviderEvents*, UINT_PTR) override {
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override {
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override {
        if (!count) return E_POINTER;
        *count = FID_NUM_FIELDS;
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override {
        if (!ppcpfd) return E_POINTER;
        *ppcpfd = nullptr;
        if (dwIndex >= FID_NUM_FIELDS) return E_INVALIDARG;

        auto pDesc = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!pDesc) return E_OUTOFMEMORY;

        pDesc->dwFieldID = s_Fields[dwIndex].dwFieldID;
        pDesc->cpft = s_Fields[dwIndex].cpft;
        HRESULT hr = DuplicateString(s_Fields[dwIndex].pszLabel, &pDesc->pszLabel);
        if (FAILED(hr)) {
            CoTaskMemFree(pDesc);
            return hr;
        }

        *ppcpfd = pDesc;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault) override {
        if (!count || !pdwDefault || !pbAutoLogonWithDefault) return E_POINTER;
        *count = (credential_ != nullptr) ? 1 : 0;
        *pdwDefault = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogonWithDefault = FALSE;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override {
        if (!ppcpc) return E_POINTER;
        *ppcpc = nullptr;
        if (dwIndex != 0 || !credential_) return E_INVALIDARG;

        credential_->AddRef();
        *ppcpc = credential_;
        return S_OK;
    }
};

HRESULT CreateFaceUnlockProvider(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    auto p = new(std::nothrow) Provider();
    if (!p) return E_OUTOFMEMORY;
    auto hr = p->QueryInterface(riid, ppv);
    p->Release();
    return hr;
}
