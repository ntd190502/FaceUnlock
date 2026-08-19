#include "FaceUnlockCredentialProvider.h"
#include "FaceUnlockIpcClient.h"
#include <new>
#include <strsafe.h>
#include <shlwapi.h>
#include <wincred.h>
#define SECURITY_WIN32
#include <security.h>
#include <ntsecapi.h>

// {64D6E84B-4969-4B59-A11A-58C3D9FA0110}
const CLSID CLSID_FaceUnlockProvider = {0x64d6e84b, 0x4969, 0x4b59, {0xa1, 0x1a, 0x58, 0xc3, 0xd9, 0xfa, 0x01, 0x10}};

enum FACEUNLOCK_FIELD_ID {
    FID_LARGE_TEXT = 0,
    FID_SMALL_TEXT = 1,
    FID_SUBMIT = 2,
    FID_STATUS_TEXT = 3,
    FID_USERNAME = 4,
    FID_PASSWORD = 5,
    FID_NUM_FIELDS = 6
};

struct FieldDef {
    DWORD dwFieldID;
    CREDENTIAL_PROVIDER_FIELD_TYPE cpft;
    PCWSTR pszLabel;
};

static const FieldDef s_Fields[FID_NUM_FIELDS] = {
    { FID_LARGE_TEXT, CPFT_LARGE_TEXT,     L"FaceUnlock" },
    { FID_SMALL_TEXT, CPFT_SMALL_TEXT,     L"Unlock with iPhone" },
    { FID_SUBMIT,     CPFT_SUBMIT_BUTTON,  L"Face ID" },
    { FID_STATUS_TEXT, CPFT_SMALL_TEXT,    L"Status" },
    { FID_USERNAME,   CPFT_EDIT_TEXT,      L"Username" },
    { FID_PASSWORD,   CPFT_PASSWORD_TEXT,  L"Windows Password" }
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
    ICredentialProviderCredentialEvents* events_ = nullptr;
    WCHAR statusMessage_[256] = L"Ready";
    WCHAR username_[256] = { 0 };
    WCHAR password_[256] = { 0 };
    bool faceIdApproved_ = false;
    std::wstring approvedRequestId_;

public:
    FaceUnlockCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus) : usage_(cpus) {
        DWORD userLen = ARRAYSIZE(username_);
        GetUserNameW(username_, &userLen);
    }

    ~FaceUnlockCredential() {
        SecureZeroMemory(password_, sizeof(password_));
        SecureZeroMemory(username_, sizeof(username_));
    }

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
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override {
        events_ = pcpce;
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override {
        events_ = nullptr;
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
            *pcpfis = faceIdApproved_ ? CPFIS_NONE : CPFIS_FOCUSED;
            return S_OK;
        case FID_STATUS_TEXT:
            *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_USERNAME:
            *pcpfs = faceIdApproved_ ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_PASSWORD:
            *pcpfs = faceIdApproved_ ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN;
            *pcpfis = faceIdApproved_ ? CPFIS_FOCUSED : CPFIS_NONE;
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
        case FID_USERNAME:
            return DuplicateString(username_, ppsz);
        case FID_PASSWORD:
            return DuplicateString(L"", ppsz);
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
            *pdwAdjacentTo = faceIdApproved_ ? FID_PASSWORD : FID_STATUS_TEXT;
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

    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR psz) override {
        if (dwFieldID == FID_USERNAME) {
            if (psz) StringCchCopyW(username_, ARRAYSIZE(username_), psz);
            else username_[0] = L'\0';
            return S_OK;
        } else if (dwFieldID == FID_PASSWORD) {
            if (psz) StringCchCopyW(password_, ARRAYSIZE(password_), psz);
            else password_[0] = L'\0';
            return S_OK;
        }
        return E_INVALIDARG;
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

        // Step 1: If Face ID is not yet approved, trigger Face ID gate authentication via Service
        if (!faceIdApproved_) {
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Waiting for iPhone Face ID...");
            if (events_) {
                events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            }

            GUID guid;
            CoCreateGuid(&guid);
            WCHAR guidStr[64] = { 0 };
            StringFromGUID2(guid, guidStr, ARRAYSIZE(guidStr));

            std::wstring requestId = guidStr;
            std::wstring usageStr = (usage_ == CPUS_UNLOCK_WORKSTATION) ? L"unlock" : L"logon";
            std::wstring usernameStr = username_;

            FaceUnlockIpcResult ipcResult = FaceUnlockIpcClient::RequestUnlock(requestId, usageStr, usernameStr, 90000);

            if (ipcResult.ok && ipcResult.status == L"approved") {
                faceIdApproved_ = true;
                approvedRequestId_ = requestId;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Face ID approved. Enter Windows password.");
                *pcpsiOptionalStatusIcon = CPSI_SUCCESS;
                DuplicateString(L"Face ID approved. Please enter your Windows password to unlock.", ppszOptionalStatusText);

                // Update field visibility: reveal Username and Password fields
                if (events_) {
                    events_->SetFieldState(this, FID_USERNAME, CPFS_DISPLAY_IN_SELECTED_TILE);
                    events_->SetFieldState(this, FID_PASSWORD, CPFS_DISPLAY_IN_SELECTED_TILE);
                    events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                }
            } else if (ipcResult.status == L"rejected") {
                faceIdApproved_ = false;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Face ID rejected");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"Face ID authentication was rejected on iPhone.", ppszOptionalStatusText);
            } else if (ipcResult.status == L"timeout") {
                faceIdApproved_ = false;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"FaceUnlock request timed out");
                *pcpsiOptionalStatusIcon = CPSI_WARNING;
                DuplicateString(L"FaceUnlock request timed out waiting for iPhone.", ppszOptionalStatusText);
            } else if (ipcResult.status == L"not_paired") {
                faceIdApproved_ = false;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"FaceUnlock is not paired");
                *pcpsiOptionalStatusIcon = CPSI_WARNING;
                DuplicateString(L"Please open FaceUnlock Agent on Windows to pair an iPhone first.", ppszOptionalStatusText);
            } else if (ipcResult.status == L"service_not_running") {
                faceIdApproved_ = false;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"FaceUnlock Service is not running");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"FaceUnlock Service is not running. Please start the service.", ppszOptionalStatusText);
            } else {
                faceIdApproved_ = false;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"FaceUnlock error");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(
                    ipcResult.message.empty() ? L"FaceUnlock authentication encountered an error." : ipcResult.message.c_str(),
                    ppszOptionalStatusText
                );
            }

            if (events_) {
                events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            }

            return S_OK;
        }

        // Step 2: Face ID was approved. Now user has entered Windows password.
        if (password_[0] == L'\0') {
            *pcpsiOptionalStatusIcon = CPSI_WARNING;
            DuplicateString(L"Please enter your Windows password.", ppszOptionalStatusText);
            return S_OK;
        }

        // Step 3: Consume the one-time short-lived grant (30s TTL)
        FaceUnlockIpcResult consumeResult = FaceUnlockIpcClient::ConsumeGrant(approvedRequestId_, 5000);
        if (!consumeResult.ok) {
            faceIdApproved_ = false;
            approvedRequestId_.clear();
            SecureZeroMemory(password_, sizeof(password_));

            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Grant expired. Please Face ID again.");
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            DuplicateString(L"Face ID approval grant expired (>30s) or invalid. Please authenticate again.", ppszOptionalStatusText);

            if (events_) {
                events_->SetFieldState(this, FID_USERNAME, CPFS_HIDDEN);
                events_->SetFieldState(this, FID_PASSWORD, CPFS_HIDDEN);
                events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            }
            return S_OK;
        }

        // Step 4: Serialize Windows Credential using CredPackAuthenticationBufferW (Negotiate / Kerberos / NTLM)
        DWORD authPackage = 0;
        ULONG cbBuffer = 0;

        // Retrieve Negotiate authentication package ID via LSA
        HANDLE hLsa = nullptr;
        LSA_STRING lsaProcessName;
        lsaProcessName.Buffer = const_cast<PCHAR>("FaceUnlockProvider");
        lsaProcessName.Length = static_cast<USHORT>(strlen("FaceUnlockProvider"));
        lsaProcessName.MaximumLength = lsaProcessName.Length + 1;

        LSA_OPERATIONAL_MODE mode = 0;
        NTSTATUS status = LsaRegisterLogonProcess(&lsaProcessName, &hLsa, &mode);
        if (status != 0 || hLsa == nullptr) {
            status = LsaConnectUntrusted(&hLsa);
        }

        if (status == 0 && hLsa != nullptr) {
            LSA_STRING pkgName;
            pkgName.Buffer = const_cast<PCHAR>(NEGOSSP_NAME_A);
            pkgName.Length = static_cast<USHORT>(strlen(NEGOSSP_NAME_A));
            pkgName.MaximumLength = pkgName.Length + 1;
            LsaLookupAuthenticationPackage(hLsa, &pkgName, &authPackage);
            LsaDeregisterLogonProcess(hLsa);
        }

        // Pack authentication buffer
        CredPackAuthenticationBufferW(
            0,
            username_,
            password_,
            nullptr,
            &cbBuffer
        );

        if (cbBuffer > 0) {
            auto rgb = static_cast<PBYTE>(CoTaskMemAlloc(cbBuffer));
            if (rgb) {
                if (CredPackAuthenticationBufferW(0, username_, password_, rgb, &cbBuffer)) {
                    pcpcs->clsidCredentialProvider = CLSID_FaceUnlockProvider;
                    pcpcs->ulAuthenticationPackage = authPackage;
                    pcpcs->cbSerialization = cbBuffer;
                    pcpcs->rgbSerialization = rgb;
                    *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;

                    // Immediately wipe in-memory plaintext password
                    SecureZeroMemory(password_, sizeof(password_));

                    StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Signing in...");
                    if (events_) {
                        events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                    }
                    return S_OK;
                } else {
                    CoTaskMemFree(rgb);
                }
            }
        }

        // Secure wipe password on failure
        SecureZeroMemory(password_, sizeof(password_));
        *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
        *pcpsiOptionalStatusIcon = CPSI_ERROR;
        return DuplicateString(L"Failed to pack Windows credentials.", ppszOptionalStatusText);
    }

    IFACEMETHODIMP ReportResult(
        NTSTATUS ntsStatus,
        NTSTATUS ntsSubstatus,
        PWSTR* ppszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override {
        if (!ppszOptionalStatusText || !pcpsiOptionalStatusIcon) return E_POINTER;
        *ppszOptionalStatusText = nullptr;
        *pcpsiOptionalStatusIcon = CPSI_NONE;

        // Reset state after logon attempt result
        faceIdApproved_ = false;
        approvedRequestId_.clear();
        SecureZeroMemory(password_, sizeof(password_));

        if (ntsStatus != 0) { // Non-success NTSTATUS
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Windows authentication failed");
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            DuplicateString(L"Windows password or authentication was incorrect.", ppszOptionalStatusText);
        } else {
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Ready");
        }

        if (events_) {
            events_->SetFieldState(this, FID_USERNAME, CPFS_HIDDEN);
            events_->SetFieldState(this, FID_PASSWORD, CPFS_HIDDEN);
            events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
        }

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
