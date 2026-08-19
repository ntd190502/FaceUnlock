#include "FaceUnlockCredentialProvider.h"
#include "FaceUnlockIpcClient.h"
#include <new>
#include <vector>
#include <string>
#include <thread>
#include <mutex>
#include <strsafe.h>
#include <shlwapi.h>
#include <wincred.h>
#define SECURITY_WIN32
#include <security.h>
#include <ntsecapi.h>
#include <initguid.h>
#include <propkey.h>

// Define PKEY_Identity_QualifiedUserName if not defined
// {50d94ae0-5bc7-4b05-b8c3-edd914298d3e}, 100
DEFINE_PROPERTYKEY(PKEY_Identity_QualifiedUserName_Local, 0x50d94ae0, 0x5bc7, 0x4b05, 0xb8, 0xc3, 0xed, 0xd9, 0x14, 0x29, 0x8d, 0x3e, 100);

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

enum class WindowsAccountType {
    Local,
    MicrosoftAccount,
    Domain,
    AzureAD,
    Unknown
};

static WindowsAccountType DetectAccountType(const std::wstring& username, const std::wstring& sid) {
    if (username.find(L"MicrosoftAccount\\") == 0 || username.find(L"@") != std::string::npos) {
        return WindowsAccountType::MicrosoftAccount;
    }
    if (username.find(L"AzureAD\\") == 0) {
        return WindowsAccountType::AzureAD;
    }
    size_t slashPos = username.find(L"\\");
    if (slashPos != std::string::npos) {
        std::wstring domain = username.substr(0, slashPos);
        WCHAR machineName[MAX_COMPUTERNAME_LENGTH + 1] = { 0 };
        DWORD size = ARRAYSIZE(machineName);
        GetComputerNameW(machineName, &size);
        if (_wcsicmp(domain.c_str(), machineName) == 0 || _wcsicmp(domain.c_str(), L".") == 0) {
            return WindowsAccountType::Local;
        }
        return WindowsAccountType::Domain;
    }
    return WindowsAccountType::Local;
}

static const char* AccountTypeToString(WindowsAccountType type) {
    switch (type) {
    case WindowsAccountType::Local: return "Local";
    case WindowsAccountType::MicrosoftAccount: return "MicrosoftAccount";
    case WindowsAccountType::Domain: return "Domain";
    case WindowsAccountType::AzureAD: return "AzureAD";
    default: return "Unknown";
    }
}

// Safe minimal diagnostic logging with size rotation (max 2 MB)
static void AppendCpLog(const std::string& message) {
    try {
        WCHAR appData[MAX_PATH] = { 0 };
        if (GetEnvironmentVariableW(L"ProgramData", appData, ARRAYSIZE(appData)) == 0) {
            StringCchCopyW(appData, ARRAYSIZE(appData), L"C:\\ProgramData");
        }
        std::wstring logDir = std::wstring(appData) + L"\\FaceUnlock\\logs";
        CreateDirectoryW(logDir.c_str(), nullptr);
        std::wstring logPath = logDir + L"\\credentialprovider.log";
        std::wstring logBackupPath = logDir + L"\\credentialprovider.log.1";

        HANDLE hFile = CreateFileW(logPath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hFile != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER fileSize;
            if (GetFileSizeEx(hFile, &fileSize) && fileSize.QuadPart >= 2 * 1024 * 1024) {
                CloseHandle(hFile);
                DeleteFileW(logBackupPath.c_str());
                MoveFileW(logPath.c_str(), logBackupPath.c_str());
                hFile = CreateFileW(logPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            }
            if (hFile != INVALID_HANDLE_VALUE) {
                SetFilePointer(hFile, 0, nullptr, FILE_END);
                SYSTEMTIME st;
                GetSystemTime(&st);
                char timestamp[64];
                StringCchPrintfA(timestamp, ARRAYSIZE(timestamp), "[%04u-%02u-%02u %02u:%02u:%02u.%03uZ] ",
                    st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
                DWORD written = 0;
                WriteFile(hFile, timestamp, static_cast<DWORD>(strlen(timestamp)), &written, nullptr);
                WriteFile(hFile, message.c_str(), static_cast<DWORD>(message.length()), &written, nullptr);
                WriteFile(hFile, "\r\n", 2, &written, nullptr);
                CloseHandle(hFile);
            }
        }
    } catch (...) {}
}

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

class FaceUnlockCredential final : public ICredentialProviderCredential2 {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;
    ICredentialProviderCredentialEvents* events_ = nullptr;
    WCHAR statusMessage_[256] = L"Ready";
    WCHAR username_[256] = { 0 };
    WCHAR userSid_[128] = { 0 };
    WCHAR userQualifiedName_[256] = { 0 };
    WCHAR password_[256] = { 0 };
    WindowsAccountType accountType_ = WindowsAccountType::Local;

    std::mutex stateMutex_;
    bool faceIdApproved_ = false;
    bool authInProgress_ = false;
    std::wstring activeRequestId_;
    std::wstring approvedRequestId_;

public:
    FaceUnlockCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, PCWSTR sid, PCWSTR qualifiedUsername)
        : usage_(cpus)
    {
        if (sid) StringCchCopyW(userSid_, ARRAYSIZE(userSid_), sid);
        if (qualifiedUsername) {
            StringCchCopyW(userQualifiedName_, ARRAYSIZE(userQualifiedName_), qualifiedUsername);
            StringCchCopyW(username_, ARRAYSIZE(username_), qualifiedUsername);
        } else if (sid) {
            StringCchCopyW(username_, ARRAYSIZE(username_), sid);
        } else {
            // Fallback for scenario where no user array provided
            DWORD userLen = ARRAYSIZE(username_);
            GetUserNameW(username_, &userLen);
        }

        accountType_ = DetectAccountType(username_, userSid_);
        char logMsg[512];
        StringCchPrintfA(logMsg, ARRAYSIZE(logMsg), "Credential initialized: account_type=%s", AccountTypeToString(accountType_));
        AppendCpLog(logMsg);
    }

    ~FaceUnlockCredential() {
        CancelActiveAuth();
        SecureZeroMemory(password_, sizeof(password_));
        SecureZeroMemory(username_, sizeof(username_));
        SecureZeroMemory(userSid_, sizeof(userSid_));
        SecureZeroMemory(userQualifiedName_, sizeof(userQualifiedName_));
    }

    void CancelActiveAuth() {
        std::wstring reqToCancel;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            if (authInProgress_ && !activeRequestId_.empty()) {
                reqToCancel = activeRequestId_;
                authInProgress_ = false;
                activeRequestId_.clear();
            }
        }
        if (!reqToCancel.empty()) {
            FaceUnlockIpcClient::CancelRequest(reqToCancel, 2000);
            AppendCpLog("Cancelled active background auth request");
        }
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential) {
            *ppv = static_cast<ICredentialProviderCredential*>(this);
            AddRef();
            return S_OK;
        } else if (riid == IID_ICredentialProviderCredential2) {
            *ppv = static_cast<ICredentialProviderCredential2*>(this);
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

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override {
        if (!ppszSid) return E_POINTER;
        *ppszSid = nullptr;
        if (userSid_[0] == L'\0') return E_NOTIMPL;
        return DuplicateString(userSid_, ppszSid);
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
        AppendCpLog("Tile selected");
        return S_OK;
    }

    IFACEMETHODIMP SetDeselected() override {
        AppendCpLog("Tile deselected - releasing state and active auth");
        CancelActiveAuth();
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (!approvedRequestId_.empty()) {
            FaceUnlockIpcClient::ReleaseGrant(approvedRequestId_, 2000);
            approvedRequestId_.clear();
        }
        faceIdApproved_ = false;
        SecureZeroMemory(password_, sizeof(password_));
        StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Ready");
        return S_OK;
    }

    IFACEMETHODIMP GetFieldState(
        DWORD dwFieldID,
        CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override {
        if (!pcpfs || !pcpfis) return E_POINTER;
        std::lock_guard<std::mutex> lock(stateMutex_);
        switch (dwFieldID) {
        case FID_LARGE_TEXT:
        case FID_SMALL_TEXT:
            *pcpfs = CPFS_DISPLAY_IN_BOTH;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_SUBMIT:
            *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = faceIdApproved_ ? CPFIS_NONE : (authInProgress_ ? CPFIS_NONE : CPFIS_FOCUSED);
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
        std::lock_guard<std::mutex> lock(stateMutex_);
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
        std::lock_guard<std::mutex> lock(stateMutex_);
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
        std::lock_guard<std::mutex> lock(stateMutex_);
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
        *ppszOptionalStatusText = nullptr;
        *pcpsiOptionalStatusIcon = CPSI_NONE;

        bool isApproved = false;
        bool inProgress = false;
        std::wstring approvedReq;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            isApproved = faceIdApproved_;
            inProgress = authInProgress_;
            approvedReq = approvedRequestId_;
        }

        // STEP 1: If Face ID is not approved yet, trigger non-blocking async worker thread
        if (!isApproved) {
            if (inProgress) {
                // User clicked button while request is already running in background
                *pcpsiOptionalStatusIcon = CPSI_WARNING;
                DuplicateString(L"Waiting for iPhone Face ID approval...", ppszOptionalStatusText);
                return S_OK;
            }

            // Check account type compatibility
            if (accountType_ == WindowsAccountType::AzureAD) {
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"AzureAD account not supported yet");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"This Windows account type is not supported by FaceUnlock yet.", ppszOptionalStatusText);
                if (events_) events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                return S_OK;
            }

            // Generate unique request ID
            GUID guid;
            CoCreateGuid(&guid);
            WCHAR guidStr[64] = { 0 };
            StringFromGUID2(guid, guidStr, ARRAYSIZE(guidStr));
            std::wstring reqId = guidStr;

            {
                std::lock_guard<std::mutex> lock(stateMutex_);
                authInProgress_ = true;
                activeRequestId_ = reqId;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Waiting for iPhone Face ID...");
            }

            if (events_) {
                events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            }

            // Retain reference for async thread
            this->AddRef();

            std::wstring usageStr = (usage_ == CPUS_UNLOCK_WORKSTATION) ? L"unlock" : L"logon";
            std::wstring userStr = username_;

            std::thread([this, reqId, usageStr, userStr]() {
                AppendCpLog("Background async Face ID request started");
                FaceUnlockIpcResult ipcResult = FaceUnlockIpcClient::RequestUnlock(reqId, usageStr, userStr, 90000);

                bool success = false;

                {
                    std::lock_guard<std::mutex> lock(this->stateMutex_);
                    if (this->activeRequestId_ == reqId) {
                        this->authInProgress_ = false;
                        if (ipcResult.ok && ipcResult.status == L"approved") {
                            this->faceIdApproved_ = true;
                            this->approvedRequestId_ = reqId;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"Face ID approved. Enter Windows password.");
                            success = true;
                        } else if (ipcResult.status == L"rejected") {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"Face ID rejected");
                        } else if (ipcResult.status == L"timeout") {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"FaceUnlock request timed out");
                        } else if (ipcResult.status == L"not_paired") {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"FaceUnlock is not paired");
                        } else if (ipcResult.status == L"service_not_running") {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"FaceUnlock Service is not running");
                        } else if (ipcResult.status == L"cancelled") {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"Face ID cancelled");
                        } else {
                            this->faceIdApproved_ = false;
                            StringCchCopyW(this->statusMessage_, ARRAYSIZE(this->statusMessage_), L"FaceUnlock error");
                        }
                    }
                }

                if (this->events_) {
                    if (success) {
                        this->events_->SetFieldState(this, FID_USERNAME, CPFS_DISPLAY_IN_SELECTED_TILE);
                        this->events_->SetFieldState(this, FID_PASSWORD, CPFS_DISPLAY_IN_SELECTED_TILE);
                    }
                    this->events_->SetFieldString(this, FID_STATUS_TEXT, this->statusMessage_);
                }

                AppendCpLog("Background async Face ID request finished");
                this->Release();
            }).detach();

            return S_OK;
        }

        // STEP 2: Face ID was approved. Now user has entered Windows password.
        if (password_[0] == L'\0') {
            *pcpsiOptionalStatusIcon = CPSI_WARNING;
            DuplicateString(L"Please enter your Windows password.", ppszOptionalStatusText);
            return S_OK;
        }

        // STEP 3: Reserve the one-time short-lived grant (30s TTL)
        FaceUnlockIpcResult reserveResult = FaceUnlockIpcClient::ReserveGrant(approvedReq, 5000);
        if (!reserveResult.ok) {
            {
                std::lock_guard<std::mutex> lock(stateMutex_);
                faceIdApproved_ = false;
                approvedRequestId_.clear();
                SecureZeroMemory(password_, sizeof(password_));
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Grant expired. Please Face ID again.");
            }

            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            DuplicateString(L"Face ID approval grant expired (>30s) or invalid. Please authenticate again.", ppszOptionalStatusText);

            if (events_) {
                events_->SetFieldState(this, FID_USERNAME, CPFS_HIDDEN);
                events_->SetFieldState(this, FID_PASSWORD, CPFS_HIDDEN);
                events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            }
            return S_OK;
        }

        // STEP 4: Serialize Windows Credential using CredPackAuthenticationBufferW (Negotiate / Kerberos / NTLM)
        DWORD authPackage = 0;
        ULONG cbBuffer = 0;

        HANDLE hLsa = nullptr;
        NTSTATUS lsaStatus = LsaConnectUntrusted(&hLsa);
        if (lsaStatus == 0 && hLsa != nullptr) {
            LSA_STRING pkgName;
            pkgName.Buffer = const_cast<PCHAR>(NEGOSSP_NAME_A);
            pkgName.Length = static_cast<USHORT>(strlen(NEGOSSP_NAME_A));
            pkgName.MaximumLength = pkgName.Length + 1;
            LsaLookupAuthenticationPackage(hLsa, &pkgName, &authPackage);
            LsaDeregisterLogonProcess(hLsa);
        }

        if (authPackage == 0) {
            AppendCpLog("LsaLookupAuthenticationPackage failed to find Negotiate package");
            FaceUnlockIpcClient::ReleaseGrant(approvedReq, 3000);
            SecureZeroMemory(password_, sizeof(password_));
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            return DuplicateString(L"Failed to locate Windows Negotiate authentication package.", ppszOptionalStatusText);
        }

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

                    {
                        std::lock_guard<std::mutex> lock(stateMutex_);
                        StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Signing in...");
                    }

                    if (events_) {
                        events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                    }
                    AppendCpLog("Packed Windows credential serialization successfully");
                    return S_OK;
                } else {
                    CoTaskMemFree(rgb);
                }
            }
        }

        // Secure wipe password and release reservation on failure
        FaceUnlockIpcClient::ReleaseGrant(approvedReq, 3000);
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

        std::wstring reqId;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            reqId = approvedRequestId_;
            SecureZeroMemory(password_, sizeof(password_));
        }

        if (ntsStatus == 0) { // Windows authentication SUCCESS
            AppendCpLog("Windows authentication SUCCESS - consuming grant");
            if (!reqId.empty()) {
                FaceUnlockIpcClient::ConsumeGrant(reqId, 3000);
            }
            std::lock_guard<std::mutex> lock(stateMutex_);
            faceIdApproved_ = false;
            approvedRequestId_.clear();
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Ready");
        } else { // Windows password incorrect or auth failed
            AppendCpLog("Windows authentication FAILED - releasing grant reservation for retry");
            bool stillValid = false;
            if (!reqId.empty()) {
                FaceUnlockIpcResult rel = FaceUnlockIpcClient::ReleaseGrant(reqId, 3000);
                stillValid = (rel.ok && rel.status == L"approved");
            }

            std::lock_guard<std::mutex> lock(stateMutex_);
            if (stillValid) {
                // User can retry password without re-triggering Face ID
                faceIdApproved_ = true;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Windows password incorrect. Try again.");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"Windows password was incorrect. Please try again.", ppszOptionalStatusText);
            } else {
                // Grant expired or invalid - must Face ID again
                faceIdApproved_ = false;
                approvedRequestId_.clear();
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Face ID approval expired. Authenticate again.");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"Windows password incorrect and Face ID approval expired. Please authenticate again.", ppszOptionalStatusText);
            }
        }

        if (events_) {
            std::lock_guard<std::mutex> lock(stateMutex_);
            if (!faceIdApproved_) {
                events_->SetFieldState(this, FID_USERNAME, CPFS_HIDDEN);
                events_->SetFieldState(this, FID_PASSWORD, CPFS_HIDDEN);
            }
            events_->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
        }

        return S_OK;
    }
};

class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;
    std::vector<FaceUnlockCredential*> credentials_;

public:
    Provider() = default;

    ~Provider() {
        ClearCredentials();
    }

    void ClearCredentials() {
        for (auto c : credentials_) {
            if (c) c->Release();
        }
        credentials_.clear();
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProvider) {
            *ppv = static_cast<ICredentialProvider*>(this);
            AddRef();
            return S_OK;
        } else if (riid == IID_ICredentialProviderSetUserArray) {
            *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
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

    // ICredentialProviderSetUserArray
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* userArray) override {
        ClearCredentials();
        if (!userArray) return S_OK;

        DWORD userCount = 0;
        HRESULT hr = userArray->GetAccountOptions(nullptr); // Check interface validity
        hr = userArray->GetCount(&userCount);
        if (FAILED(hr) || userCount == 0) {
            return S_OK;
        }

        for (DWORD i = 0; i < userCount; ++i) {
            ICredentialProviderUser* pUser = nullptr;
            hr = userArray->GetAt(i, &pUser);
            if (SUCCEEDED(hr) && pUser != nullptr) {
                PWSTR sid = nullptr;
                PWSTR qualifiedName = nullptr;
                pUser->GetSid(&sid);
                pUser->GetStringValue(PKEY_Identity_QualifiedUserName_Local, &qualifiedName);

                auto cred = new(std::nothrow) FaceUnlockCredential(usage_, sid, qualifiedName);
                if (cred) {
                    credentials_.push_back(cred);
                }

                if (sid) CoTaskMemFree(sid);
                if (qualifiedName) CoTaskMemFree(qualifiedName);
                pUser->Release();
            }
        }

        return S_OK;
    }

    // ICredentialProvider
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD) override {
        if (cpus != CPUS_LOGON && cpus != CPUS_UNLOCK_WORKSTATION) {
            return E_NOTIMPL;
        }
        usage_ = cpus;
        if (credentials_.empty()) {
            auto cred = new(std::nothrow) FaceUnlockCredential(cpus, nullptr, nullptr);
            if (cred) credentials_.push_back(cred);
        }
        return S_OK;
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
        *count = static_cast<DWORD>(credentials_.size());
        *pdwDefault = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogonWithDefault = FALSE;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override {
        if (!ppcpc) return E_POINTER;
        *ppcpc = nullptr;
        if (dwIndex >= credentials_.size()) return E_INVALIDARG;

        credentials_[dwIndex]->AddRef();
        *ppcpc = credentials_[dwIndex];
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
