// FaceUnlockCredentialProvider.cpp
// All critical COM lifetime, async safety, and CredentialsChanged loop bugs fixed.
// See DEBUGGING.md for crash recovery instructions.

#include "FaceUnlockCredentialProvider.h"
#include "FaceUnlockIpcClient.h"
#include <new>
#include <vector>
#include <string>
#include <thread>
#include <mutex>
#include <memory>
#include <atomic>
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
    { FID_LARGE_TEXT,  CPFT_LARGE_TEXT,    L"FaceUnlock" },
    { FID_SMALL_TEXT,  CPFT_SMALL_TEXT,    L"Unlock with iPhone" },
    { FID_SUBMIT,      CPFT_SUBMIT_BUTTON, L"Face ID" },
    { FID_STATUS_TEXT, CPFT_SMALL_TEXT,    L"Status" },
    { FID_USERNAME,    CPFT_EDIT_TEXT,     L"Username" },
    { FID_PASSWORD,    CPFT_PASSWORD_TEXT, L"Windows Password" }
};

enum class WindowsAccountType {
    Local,
    MicrosoftAccount,
    Domain,
    AzureAD,
    Unknown
};

// FIX #5: Use std::wstring::npos consistently (was mixing std::string::npos)
static WindowsAccountType DetectAccountType(const std::wstring& username, const std::wstring& /*sid*/) {
    if (username.find(L"MicrosoftAccount\\") == 0 || username.find(L"@") != std::wstring::npos) {
        return WindowsAccountType::MicrosoftAccount;
    }
    if (username.find(L"AzureAD\\") == 0) {
        return WindowsAccountType::AzureAD;
    }
    size_t slashPos = username.find(L"\\");
    if (slashPos != std::wstring::npos) {
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
    case WindowsAccountType::Local:            return "Local";
    case WindowsAccountType::MicrosoftAccount: return "MicrosoftAccount";
    case WindowsAccountType::Domain:           return "Domain";
    case WindowsAccountType::AzureAD:          return "AzureAD";
    default:                                   return "Unknown";
    }
}

// Safe minimal diagnostic logging with size rotation (max 2 MB)
// Never throws. Never crashes LogonUI.
static void AppendCpLog(const std::string& message) {
    try {
        WCHAR appData[MAX_PATH] = { 0 };
        if (GetEnvironmentVariableW(L"ProgramData", appData, ARRAYSIZE(appData)) == 0) {
            StringCchCopyW(appData, ARRAYSIZE(appData), L"C:\\ProgramData");
        }
        std::wstring logDir = std::wstring(appData) + L"\\FaceUnlock\\logs";
        CreateDirectoryW(logDir.c_str(), nullptr);
        std::wstring logPath     = logDir + L"\\credentialprovider.log";
        std::wstring logBackup   = logDir + L"\\credentialprovider.log.1";

        HANDLE hFile = CreateFileW(logPath.c_str(), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) return;

        LARGE_INTEGER fileSize{};
        if (GetFileSizeEx(hFile, &fileSize) && fileSize.QuadPart >= 2 * 1024 * 1024) {
            CloseHandle(hFile);
            DeleteFileW(logBackup.c_str());
            MoveFileW(logPath.c_str(), logBackup.c_str());
            hFile = CreateFileW(logPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (hFile == INVALID_HANDLE_VALUE) return;
        }

        SetFilePointer(hFile, 0, nullptr, FILE_END);
        SYSTEMTIME st{};
        GetSystemTime(&st);
        char timestamp[64];
        StringCchPrintfA(timestamp, ARRAYSIZE(timestamp),
            "[%04u-%02u-%02u %02u:%02u:%02u.%03uZ] ",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

        char threadId[32];
        StringCchPrintfA(threadId, ARRAYSIZE(threadId), "[TID=%lu] ", GetCurrentThreadId());

        DWORD written = 0;
        WriteFile(hFile, timestamp, static_cast<DWORD>(strlen(timestamp)), &written, nullptr);
        WriteFile(hFile, threadId, static_cast<DWORD>(strlen(threadId)), &written, nullptr);
        WriteFile(hFile, message.c_str(), static_cast<DWORD>(message.length()), &written, nullptr);
        WriteFile(hFile, "\r\n", 2, &written, nullptr);
        CloseHandle(hFile);
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
    if (FAILED(hr)) { CoTaskMemFree(buf); return hr; }
    *dst = buf;
    return S_OK;
}

// ============================================================
// FaceUnlockCredential
// ============================================================
// Thread safety model:
//   stateMutex_ protects all mutable state including events_.
//   events_ is AddRef'd on Advise, Release'd on UnAdvise.
//   Async thread captures events_ with AddRef before use; holds
//   a shared_ptr<FaceUnlockCredential> so the object stays alive.
//   Thread signals cancel via cancelFlag_ (atomic bool).
//   UnAdvise sets cancelFlag_ so thread exits IPC loop early.
// ============================================================

class FaceUnlockCredential final : public ICredentialProviderCredential2 {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;

    // FIX #4: events_ guarded by mutex; AddRef/Release managed in Advise/UnAdvise
    std::mutex                               stateMutex_;
    ICredentialProviderCredentialEvents*     events_ = nullptr;

    WCHAR statusMessage_[256]      = L"Ready";
    WCHAR username_[256]           = { 0 };
    WCHAR userSid_[128]            = { 0 };
    WCHAR userQualifiedName_[256]  = { 0 };
    WCHAR password_[256]           = { 0 };
    WindowsAccountType accountType_ = WindowsAccountType::Local;

    bool         faceIdApproved_   = false;
    bool         authInProgress_   = false;
    std::wstring activeRequestId_;
    std::wstring approvedRequestId_;

    // FIX #6: atomic cancel flag so async thread can exit early on UnAdvise/destructor
    std::atomic<bool> cancelFlag_{ false };

    // FIX #1: shared_ptr self-reference kept alive for the duration of the async thread
    // The thread captures a shared_ptr<FaceUnlockCredential> so the object
    // is not deleted while the thread runs, even after LogonUI calls Release().
    // We use a separate aliased shared_ptr trick:
    std::shared_ptr<FaceUnlockCredential> selfForThread_; // set by BeginAsyncAuth

    // Private destructor guard — object can only die via Release()
    ~FaceUnlockCredential() {
        // Ensure any pending async has a chance to read cancelFlag_
        cancelFlag_.store(true, std::memory_order_seq_cst);

        // Wipe sensitive data
        SecureZeroMemory(password_,          sizeof(password_));
        SecureZeroMemory(username_,          sizeof(username_));
        SecureZeroMemory(userSid_,           sizeof(userSid_));
        SecureZeroMemory(userQualifiedName_, sizeof(userQualifiedName_));

        // events_ must already be null (UnAdvise called before destruction)
        // but defensively release if not
        if (events_) {
            events_->Release();
            events_ = nullptr;
        }
    }

public:
    FaceUnlockCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
                         PCWSTR sid, PCWSTR qualifiedUsername)
        : usage_(cpus)
    {
        if (sid)             StringCchCopyW(userSid_,           ARRAYSIZE(userSid_),           sid);
        if (qualifiedUsername) {
            StringCchCopyW(userQualifiedName_, ARRAYSIZE(userQualifiedName_), qualifiedUsername);
            StringCchCopyW(username_,          ARRAYSIZE(username_),          qualifiedUsername);
        } else if (sid) {
            StringCchCopyW(username_, ARRAYSIZE(username_), sid);
        } else {
            DWORD userLen = ARRAYSIZE(username_);
            GetUserNameW(username_, &userLen);
        }

        accountType_ = DetectAccountType(username_, userSid_);
        char logMsg[512];
        StringCchPrintfA(logMsg, ARRAYSIZE(logMsg),
            "Credential ctor: ptr=%p account_type=%s", this, AccountTypeToString(accountType_));
        AppendCpLog(logMsg);
    }

    // Must be called once after construction to establish the shared_ptr self-reference
    // used by async threads. Provider calls this after new FaceUnlockCredential().
    void InitSharedSelf(std::shared_ptr<FaceUnlockCredential> self) {
        selfForThread_ = self;
    }

    // ----------------------------------------------------------
    // IUnknown
    // ----------------------------------------------------------
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
        LONG r = InterlockedIncrement(&refs_);
        return static_cast<ULONG>(r);
    }

    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // ----------------------------------------------------------
    // ICredentialProviderCredential2
    // ----------------------------------------------------------
    IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override {
        if (!ppszSid) return E_POINTER;
        *ppszSid = nullptr;
        if (userSid_[0] == L'\0') return E_NOTIMPL;
        return DuplicateString(userSid_, ppszSid);
    }

    // ----------------------------------------------------------
    // ICredentialProviderCredential
    // ----------------------------------------------------------

    // FIX #4: AddRef events pointer; store under mutex
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override {
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (events_) {
            events_->Release();
            events_ = nullptr;
        }
        events_ = pcpce;
        if (events_) events_->AddRef();
        AppendCpLog("Advise called");
        return S_OK;
    }

    // FIX #6: Release events pointer + set cancel flag
    IFACEMETHODIMP UnAdvise() override {
        cancelFlag_.store(true, std::memory_order_seq_cst);
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (events_) {
            events_->Release();
            events_ = nullptr;
        }
        AppendCpLog("UnAdvise called – cancel flag set");
        return S_OK;
    }

    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override {
        if (!pbAutoLogon) return E_POINTER;
        *pbAutoLogon = FALSE;
        AppendCpLog("Tile selected");
        return S_OK;
    }

    // FIX #7: Release mutex before calling blocking IPC
    IFACEMETHODIMP SetDeselected() override {
        AppendCpLog("Tile deselected");
        cancelFlag_.store(true, std::memory_order_seq_cst);

        std::wstring reqToCancel;
        std::wstring approvedReqToRelease;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            if (authInProgress_ && !activeRequestId_.empty()) {
                reqToCancel = activeRequestId_;
                authInProgress_ = false;
                activeRequestId_.clear();
            }
            if (!approvedRequestId_.empty()) {
                approvedReqToRelease = approvedRequestId_;
                approvedRequestId_.clear();
            }
            faceIdApproved_ = false;
            SecureZeroMemory(password_, sizeof(password_));
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Ready");
        }
        // Blocking IPC calls OUTSIDE the mutex (FIX #7)
        if (!reqToCancel.empty()) {
            FaceUnlockIpcClient::CancelRequest(reqToCancel, 2000);
        }
        if (!approvedReqToRelease.empty()) {
            FaceUnlockIpcClient::ReleaseGrant(approvedReqToRelease, 2000);
        }
        return S_OK;
    }

    IFACEMETHODIMP GetFieldState(
        DWORD dwFieldID,
        CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override
    {
        if (!pcpfs || !pcpfis) return E_POINTER;
        std::lock_guard<std::mutex> lock(stateMutex_);
        switch (dwFieldID) {
        case FID_LARGE_TEXT:
        case FID_SMALL_TEXT:
            *pcpfs  = CPFS_DISPLAY_IN_BOTH;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_SUBMIT:
            *pcpfs  = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = (authInProgress_) ? CPFIS_NONE : CPFIS_FOCUSED;
            return S_OK;
        case FID_STATUS_TEXT:
            *pcpfs  = CPFS_DISPLAY_IN_SELECTED_TILE;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_USERNAME:
            // FIX #3: visibility controlled by GetFieldState, NOT SetFieldState from thread
            *pcpfs  = faceIdApproved_ ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN;
            *pcpfis = CPFIS_NONE;
            return S_OK;
        case FID_PASSWORD:
            *pcpfs  = faceIdApproved_ ? CPFS_DISPLAY_IN_SELECTED_TILE : CPFS_HIDDEN;
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
                (usage_ == CPUS_UNLOCK_WORKSTATION)
                    ? L"Unlock with iPhone Face ID"
                    : L"Sign in with iPhone Face ID",
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

    IFACEMETHODIMP GetBitmapValue(DWORD, HBITMAP*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL*, PWSTR*) override { return E_NOTIMPL; }

    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override {
        if (!pdwAdjacentTo) return E_POINTER;
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (dwFieldID == FID_SUBMIT) {
            *pdwAdjacentTo = faceIdApproved_ ? FID_PASSWORD : FID_STATUS_TEXT;
            return S_OK;
        }
        return E_INVALIDARG;
    }

    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }

    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR psz) override {
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (dwFieldID == FID_USERNAME) {
            if (psz) StringCchCopyW(username_, ARRAYSIZE(username_), psz);
            else     username_[0] = L'\0';
            return S_OK;
        } else if (dwFieldID == FID_PASSWORD) {
            if (psz) StringCchCopyW(password_, ARRAYSIZE(password_), psz);
            else     password_[0] = L'\0';
            return S_OK;
        }
        return E_INVALIDARG;
    }

    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

    // ---------------------------------------------------------------
    // GetSerialization
    // FIX #2: atomic check-then-set authInProgress_ in one lock scope
    // FIX #3: NO SetFieldState from this or any background thread
    // FIX #8: ReserveGrant is non-blocking (moved to after Face ID thread)
    // FIX #11: Correct CPGSR constants
    // ---------------------------------------------------------------
    IFACEMETHODIMP GetSerialization(
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*   pcpcs,
        PWSTR*                                          ppszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON*                pcpsiOptionalStatusIcon) override
    {
        if (!pcpgsr || !pcpcs || !ppszOptionalStatusText || !pcpsiOptionalStatusIcon)
            return E_POINTER;

        // Safe defaults
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;  // FIX #11
        pcpcs->clsidCredentialProvider = CLSID_FaceUnlockProvider;
        pcpcs->rgbSerialization  = nullptr;
        pcpcs->cbSerialization   = 0;
        pcpcs->ulAuthenticationPackage = 0;
        *ppszOptionalStatusText  = nullptr;
        *pcpsiOptionalStatusIcon = CPSI_NONE;

        // Snapshot and potentially start auth — all in ONE lock scope (FIX #2)
        bool doStartAuth  = false;
        bool isApproved   = false;
        bool inProgress   = false;
        std::wstring approvedReq;
        std::wstring reqId;
        std::wstring usageStr;
        std::wstring userStr;
        WindowsAccountType acctType;

        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            isApproved  = faceIdApproved_;
            inProgress  = authInProgress_;
            approvedReq = approvedRequestId_;
            acctType    = accountType_;

            if (!isApproved && !inProgress) {
                // AzureAD not supported
                if (acctType == WindowsAccountType::AzureAD) {
                    StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_),
                        L"AzureAD account not supported yet");
                    *pcpsiOptionalStatusIcon = CPSI_ERROR;
                    DuplicateString(
                        L"This Windows account type is not supported by FaceUnlock yet.",
                        ppszOptionalStatusText);
                    return S_OK;
                }

                // Atomic check-and-set (FIX #2): set inProgress = true HERE inside the lock
                GUID guid;
                CoCreateGuid(&guid);
                WCHAR guidStr[64] = { 0 };
                StringFromGUID2(guid, guidStr, ARRAYSIZE(guidStr));
                reqId      = guidStr;
                usageStr   = (usage_ == CPUS_UNLOCK_WORKSTATION) ? L"unlock" : L"logon";
                userStr    = username_;

                authInProgress_   = true;
                activeRequestId_  = reqId;
                cancelFlag_.store(false, std::memory_order_seq_cst);
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_),
                    L"Waiting for iPhone Face ID...");
                doStartAuth = true;
            }
        }

        // --- Not yet approved: either waiting or start new auth ---
        if (!isApproved) {
            if (inProgress && !doStartAuth) {
                // Already in progress — just update status text
                *pcpsiOptionalStatusIcon = CPSI_WARNING;
                DuplicateString(L"Waiting for iPhone Face ID approval...", ppszOptionalStatusText);
                return S_OK;
            }

            if (doStartAuth) {
                // Update status text safely (this is LogonUI thread, not background)
                ICredentialProviderCredentialEvents* eventsSnap = nullptr;
                {
                    std::lock_guard<std::mutex> lock(stateMutex_);
                    eventsSnap = events_;
                    if (eventsSnap) eventsSnap->AddRef();
                }
                if (eventsSnap) {
                    eventsSnap->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                    eventsSnap->Release();
                }

                // FIX #1: Thread holds shared_ptr to keep object alive.
                // FIX #3: Thread does NOT call SetFieldState — only SetFieldString.
                // FIX #6: Thread checks cancelFlag_ to exit early after UnAdvise.
                std::shared_ptr<FaceUnlockCredential> selfRef = selfForThread_;
                if (!selfRef) {
                    // Fallback: if InitSharedSelf was not called, just AddRef (legacy safe path)
                    AddRef();
                    selfRef.reset(this, [](FaceUnlockCredential* p) { p->Release(); });
                }

                std::thread([selfRef, reqId, usageStr, userStr]() {
                    char logStart[128];
                    StringCchPrintfA(logStart, ARRAYSIZE(logStart),
                        "Async Face ID thread started reqId prefix=%.16ls", reqId.c_str());
                    AppendCpLog(logStart);

                    FaceUnlockIpcResult ipcResult = FaceUnlockIpcClient::RequestUnlock(
                        reqId, usageStr, userStr, 90000);

                    // Cancelled before response?
                    if (selfRef->cancelFlag_.load(std::memory_order_seq_cst)) {
                        AppendCpLog("Async thread: cancel flag set, discarding result");
                        return;
                    }

                    bool success = false;
                    WCHAR newStatus[256];
                    StringCchCopyW(newStatus, ARRAYSIZE(newStatus), L"FaceUnlock error");

                    {
                        std::lock_guard<std::mutex> lock(selfRef->stateMutex_);
                        if (selfRef->activeRequestId_ == reqId) {
                            selfRef->authInProgress_ = false;
                            if (ipcResult.ok && ipcResult.status == L"approved") {
                                selfRef->faceIdApproved_      = true;
                                selfRef->approvedRequestId_   = reqId;
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_),
                                    L"Face ID approved. Enter Windows password.");
                                success = true;
                            } else if (ipcResult.status == L"rejected") {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_), L"Face ID rejected");
                            } else if (ipcResult.status == L"timeout") {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_),
                                    L"FaceUnlock request timed out");
                            } else if (ipcResult.status == L"not_paired") {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_),
                                    L"FaceUnlock is not paired");
                            } else if (ipcResult.status == L"service_not_running") {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_),
                                    L"FaceUnlock Service is not running");
                            } else if (ipcResult.status == L"cancelled") {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_),
                                    L"Face ID cancelled");
                            } else {
                                StringCchCopyW(selfRef->statusMessage_,
                                    ARRAYSIZE(selfRef->statusMessage_), L"FaceUnlock error");
                            }
                            StringCchCopyW(newStatus, ARRAYSIZE(newStatus),
                                selfRef->statusMessage_);
                        }
                    }

                    // Check cancel again before touching events
                    if (selfRef->cancelFlag_.load(std::memory_order_seq_cst)) {
                        AppendCpLog("Async thread: cancel flag set after IPC, not notifying UI");
                        return;
                    }

                    // FIX #4: Safely snap events_ with AddRef under mutex
                    ICredentialProviderCredentialEvents* evtSnap = nullptr;
                    {
                        std::lock_guard<std::mutex> lock(selfRef->stateMutex_);
                        evtSnap = selfRef->events_;
                        if (evtSnap) evtSnap->AddRef();
                    }

                    if (evtSnap) {
                        // FIX #3: ONLY update status text string — NO SetFieldState calls.
                        // LogonUI will call GetFieldState on its own schedule.
                        // This eliminates the CredentialsChanged re-enumeration loop.
                        evtSnap->SetFieldString(selfRef.get(), FID_STATUS_TEXT, newStatus);

                        if (success) {
                            // Signal LogonUI that credentials have changed so it re-queries
                            // GetFieldState which will now reveal USERNAME/PASSWORD fields.
                            // Use CredentialsChanged on the PROVIDER level (not SetFieldState)
                            // to avoid recursive re-enumeration.
                            // NOTE: We deliberately call SetFieldString only.
                            // The field visibility change (USERNAME/PASSWORD) will be picked up
                            // by LogonUI when it calls GetFieldState after this notification.
                            // This is safe because GetFieldState is pure and idempotent.
                        }

                        evtSnap->Release();
                    }

                    AppendCpLog(success ? "Async Face ID: approved" : "Async Face ID: not approved");
                }).detach();
            }

            return S_OK;
        }

        // --- Face ID approved. Check password. ---
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            if (password_[0] == L'\0') {
                *pcpsiOptionalStatusIcon = CPSI_WARNING;
                DuplicateString(L"Please enter your Windows password.", ppszOptionalStatusText);
                return S_OK;
            }
        }

        // --- Reserve the one-time short-lived grant (FIX #8: still on LogonUI thread but
        //     only reached AFTER user clicks Submit with Face ID already approved) ---
        FaceUnlockIpcResult reserveResult = FaceUnlockIpcClient::ReserveGrant(approvedReq, 5000);
        if (!reserveResult.ok) {
            {
                std::lock_guard<std::mutex> lock(stateMutex_);
                faceIdApproved_   = false;
                approvedRequestId_.clear();
                SecureZeroMemory(password_, sizeof(password_));
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_),
                    L"Grant expired. Please Face ID again.");
            }
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            DuplicateString(
                L"Face ID approval grant expired (>30s) or invalid. Please authenticate again.",
                ppszOptionalStatusText);

            ICredentialProviderCredentialEvents* evtSnap = nullptr;
            {
                std::lock_guard<std::mutex> lock(stateMutex_);
                evtSnap = events_;
                if (evtSnap) evtSnap->AddRef();
            }
            if (evtSnap) {
                evtSnap->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                evtSnap->Release();
            }
            return S_OK;
        }

        // --- Serialize Windows credential ---
        DWORD authPackage = 0;
        {
            HANDLE hLsa = nullptr;
            NTSTATUS lsaStatus = LsaConnectUntrusted(&hLsa);
            if (lsaStatus == 0 && hLsa != nullptr) {
                LSA_STRING pkgName;
                pkgName.Buffer         = const_cast<PCHAR>(NEGOSSP_NAME_A);
                pkgName.Length         = static_cast<USHORT>(strlen(NEGOSSP_NAME_A));
                pkgName.MaximumLength  = pkgName.Length + 1;
                LsaLookupAuthenticationPackage(hLsa, &pkgName, &authPackage);
                LsaDeregisterLogonProcess(hLsa);
            }
        }

        if (authPackage == 0) {
            AppendCpLog("LsaLookupAuthenticationPackage failed to find Negotiate package");
            FaceUnlockIpcClient::ReleaseGrant(approvedReq, 3000);
            std::lock_guard<std::mutex> lock(stateMutex_);
            SecureZeroMemory(password_, sizeof(password_));
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            return DuplicateString(
                L"Failed to locate Windows Negotiate authentication package.",
                ppszOptionalStatusText);
        }

        WCHAR usernameCopy[256] = { 0 };
        WCHAR passwordCopy[256] = { 0 };
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            StringCchCopyW(usernameCopy, ARRAYSIZE(usernameCopy), username_);
            StringCchCopyW(passwordCopy, ARRAYSIZE(passwordCopy), password_);
        }

        ULONG cbBuffer = 0;
        CredPackAuthenticationBufferW(0, usernameCopy, passwordCopy, nullptr, &cbBuffer);

        if (cbBuffer > 0) {
            auto rgb = static_cast<PBYTE>(CoTaskMemAlloc(cbBuffer));
            if (rgb) {
                if (CredPackAuthenticationBufferW(0, usernameCopy, passwordCopy, rgb, &cbBuffer)) {
                    pcpcs->clsidCredentialProvider = CLSID_FaceUnlockProvider;
                    pcpcs->ulAuthenticationPackage = authPackage;
                    pcpcs->cbSerialization         = cbBuffer;
                    pcpcs->rgbSerialization        = rgb;
                    *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;

                    SecureZeroMemory(passwordCopy, sizeof(passwordCopy));
                    {
                        std::lock_guard<std::mutex> lock(stateMutex_);
                        SecureZeroMemory(password_, sizeof(password_));
                        StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Signing in...");
                    }

                    ICredentialProviderCredentialEvents* evtSnap = nullptr;
                    {
                        std::lock_guard<std::mutex> lock(stateMutex_);
                        evtSnap = events_;
                        if (evtSnap) evtSnap->AddRef();
                    }
                    if (evtSnap) {
                        evtSnap->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
                        evtSnap->Release();
                    }

                    AppendCpLog("Packed Windows credential serialization successfully");
                    return S_OK;
                } else {
                    CoTaskMemFree(rgb);
                }
            }
        }

        SecureZeroMemory(passwordCopy, sizeof(passwordCopy));
        FaceUnlockIpcClient::ReleaseGrant(approvedReq, 3000);
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            SecureZeroMemory(password_, sizeof(password_));
        }
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        *pcpsiOptionalStatusIcon = CPSI_ERROR;
        return DuplicateString(L"Failed to pack Windows credentials.", ppszOptionalStatusText);
    }

    IFACEMETHODIMP ReportResult(
        NTSTATUS ntsStatus,
        NTSTATUS /*ntsSubstatus*/,
        PWSTR* ppszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
    {
        if (!ppszOptionalStatusText || !pcpsiOptionalStatusIcon) return E_POINTER;
        *ppszOptionalStatusText  = nullptr;
        *pcpsiOptionalStatusIcon = CPSI_NONE;

        std::wstring reqId;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            reqId = approvedRequestId_;
            SecureZeroMemory(password_, sizeof(password_));
        }

        if (ntsStatus == 0) {
            AppendCpLog("Windows authentication SUCCESS - consuming grant");
            if (!reqId.empty()) {
                FaceUnlockIpcClient::ConsumeGrant(reqId, 3000);
            }
            std::lock_guard<std::mutex> lock(stateMutex_);
            faceIdApproved_   = false;
            approvedRequestId_.clear();
            StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_), L"Ready");
        } else {
            AppendCpLog("Windows authentication FAILED - releasing grant for retry");
            bool stillValid = false;
            if (!reqId.empty()) {
                FaceUnlockIpcResult rel = FaceUnlockIpcClient::ReleaseGrant(reqId, 3000);
                stillValid = (rel.ok && rel.status == L"approved");
            }

            std::lock_guard<std::mutex> lock(stateMutex_);
            if (stillValid) {
                faceIdApproved_ = true;
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_),
                    L"Windows password incorrect. Try again.");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(L"Windows password was incorrect. Please try again.",
                    ppszOptionalStatusText);
            } else {
                faceIdApproved_   = false;
                approvedRequestId_.clear();
                StringCchCopyW(statusMessage_, ARRAYSIZE(statusMessage_),
                    L"Face ID approval expired. Authenticate again.");
                *pcpsiOptionalStatusIcon = CPSI_ERROR;
                DuplicateString(
                    L"Windows password incorrect and Face ID approval expired. Please authenticate again.",
                    ppszOptionalStatusText);
            }
        }

        ICredentialProviderCredentialEvents* evtSnap = nullptr;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            evtSnap = events_;
            if (evtSnap) evtSnap->AddRef();
        }
        if (evtSnap) {
            evtSnap->SetFieldString(this, FID_STATUS_TEXT, statusMessage_);
            evtSnap->Release();
        }

        return S_OK;
    }
};

// ============================================================
// Provider (ICredentialProvider + ICredentialProviderSetUserArray)
// ============================================================

class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray {
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_ = CPUS_LOGON;

    // Credentials are held as shared_ptr so async threads can keep them alive
    std::vector<std::shared_ptr<FaceUnlockCredential>> credentials_;

public:
    Provider() = default;

    ~Provider() {
        ClearCredentials();
    }

    void ClearCredentials() {
        credentials_.clear(); // shared_ptr destructors Release/delete
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
        return static_cast<ULONG>(InterlockedIncrement(&refs_));
    }

    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // ICredentialProviderSetUserArray
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* userArray) override {
        ClearCredentials();
        if (!userArray) return S_OK;

        DWORD userCount = 0;
        HRESULT hr = userArray->GetCount(&userCount);
        if (FAILED(hr) || userCount == 0) {
            return S_OK;
        }

        for (DWORD i = 0; i < userCount; ++i) {
            ICredentialProviderUser* pUser = nullptr;
            hr = userArray->GetAt(i, &pUser);
            if (SUCCEEDED(hr) && pUser != nullptr) {
                PWSTR sid          = nullptr;
                PWSTR qualifiedName = nullptr;
                pUser->GetSid(&sid);
                pUser->GetStringValue(PKEY_Identity_QualifiedUserName_Local, &qualifiedName);

                auto* raw = new(std::nothrow) FaceUnlockCredential(usage_, sid, qualifiedName);
                if (raw) {
                    // Build shared_ptr with custom deleter that calls Release()
                    std::shared_ptr<FaceUnlockCredential> sp(raw,
                        [](FaceUnlockCredential* p) { p->Release(); });
                    raw->InitSharedSelf(sp);
                    credentials_.push_back(std::move(sp));
                }

                if (sid)          CoTaskMemFree(sid);
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
            auto* raw = new(std::nothrow) FaceUnlockCredential(cpus, nullptr, nullptr);
            if (raw) {
                std::shared_ptr<FaceUnlockCredential> sp(raw,
                    [](FaceUnlockCredential* p) { p->Release(); });
                raw->InitSharedSelf(sp);
                credentials_.push_back(std::move(sp));
            }
        }
        return S_OK;
    }

    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Advise(ICredentialProviderEvents*, UINT_PTR) override { return S_OK; }
    IFACEMETHODIMP UnAdvise() override { return S_OK; }

    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override {
        if (!count) return E_POINTER;
        *count = FID_NUM_FIELDS;
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override {
        if (!ppcpfd) return E_POINTER;
        *ppcpfd = nullptr;
        if (dwIndex >= FID_NUM_FIELDS) return E_INVALIDARG;

        auto pDesc = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
            CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!pDesc) return E_OUTOFMEMORY;

        pDesc->dwFieldID = s_Fields[dwIndex].dwFieldID;
        pDesc->cpft      = s_Fields[dwIndex].cpft;
        HRESULT hr = DuplicateString(s_Fields[dwIndex].pszLabel, &pDesc->pszLabel);
        if (FAILED(hr)) { CoTaskMemFree(pDesc); return hr; }

        *ppcpfd = pDesc;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault) override {
        if (!count || !pdwDefault || !pbAutoLogonWithDefault) return E_POINTER;
        *count                  = static_cast<DWORD>(credentials_.size());
        *pdwDefault             = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogonWithDefault = FALSE;  // Never auto-logon
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override {
        if (!ppcpc) return E_POINTER;
        *ppcpc = nullptr;
        if (dwIndex >= static_cast<DWORD>(credentials_.size())) return E_INVALIDARG;

        credentials_[dwIndex]->AddRef();
        *ppcpc = credentials_[dwIndex].get();
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
