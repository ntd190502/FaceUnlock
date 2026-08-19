#define SECURITY_WIN32
#include <windows.h>
#include <security.h>
#include <ntsecpkg.h>
#include <sddl.h>
#include <strsafe.h>
#include "FaceUnlockAuthCommon.h"
#include "AuthPackageCore.h"

// Global LSA dispatch table and package ID
static LSA_DISPATCH_TABLE g_LsaDispatchTable;
static ULONG_PTR g_PackageId = 0;
static std::vector<BYTE> g_MachineSecret;

static void LogLsa(const char* msg) {
    OutputDebugStringA(msg);
}

// -------------------------------------------------------------------------
// SpInitialize
// -------------------------------------------------------------------------
static NTSTATUS NTAPI SpInitialize(
    ULONG_PTR PackageId,
    PSECPKG_PARAMETERS Parameters,
    PLSA_DISPATCH_TABLE FunctionTable
) {
    g_PackageId = PackageId;
    if (FunctionTable) {
        memcpy_s(&g_LsaDispatchTable, sizeof(g_LsaDispatchTable), FunctionTable, sizeof(LSA_DISPATCH_TABLE));
    }

    // Attempt to load machine secret into memory
    FaceUnlockAuth::AuthPackageCore::LoadMachineSecretFromDpapi(g_MachineSecret);
    LogLsa("[FaceUnlockAuthPackage] SpInitialize completed\n");
    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// SpShutDown
// -------------------------------------------------------------------------
static NTSTATUS NTAPI SpShutDown() {
    if (!g_MachineSecret.empty()) {
        SecureZeroMemory(g_MachineSecret.data(), g_MachineSecret.size());
        g_MachineSecret.clear();
    }
    LogLsa("[FaceUnlockAuthPackage] SpShutDown completed\n");
    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// SpGetInfo
// -------------------------------------------------------------------------
static NTSTATUS NTAPI SpGetInfo(PSecPkgInfoW PackageInfo) {
    if (!PackageInfo) return STATUS_INVALID_PARAMETER;

    PackageInfo->fCapabilities = SECPKG_FLAG_LOGON | SECPKG_FLAG_ACCEPT_WIN32_NAME;
    PackageInfo->wVersion      = 1;
    PackageInfo->wRPCID        = RPC_C_AUTHN_NONE;
    PackageInfo->cbMaxToken    = sizeof(FACEUNLOCK_LOGON_V1);
    PackageInfo->Name          = const_cast<PWSTR>(FACEUNLOCK_AUTHPACKAGE_NAME_W);
    PackageInfo->Comment       = const_cast<PWSTR>(FACEUNLOCK_AUTHPACKAGE_COMMENT_W);

    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// SpAcceptCredentials
// -------------------------------------------------------------------------
static NTSTATUS NTAPI SpAcceptCredentials(
    SECURITY_LOGON_TYPE LogonType,
    PUNICODE_STRING AccountName,
    PSECPKG_PRIMARY_CRED PrimaryCredentials,
    PSECPKG_SUPPLEMENTAL_CRED SupplementalCredentials
) {
    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// SpLogonUserEx2
// -------------------------------------------------------------------------
static NTSTATUS NTAPI SpLogonUserEx2(
    PLSA_CLIENT_REQUEST ClientRequest,
    SECURITY_LOGON_TYPE LogonType,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferSize,
    PVOID* ProfileBuffer,
    PULONG ProfileBufferSize,
    PLUID LogonId,
    PNTSTATUS SubStatus,
    PTOKEN_SOURCE TokenSource,
    PVOID* TokenInformation,
    PULONG TokenInformationType,
    PUNICODE_STRING* AccountName,
    PUNICODE_STRING* AuthenticatingAuthority
) {
    if (SubStatus) *SubStatus = STATUS_SUCCESS;

    if (!ProtocolSubmitBuffer || SubmitBufferSize < sizeof(FACEUNLOCK_LOGON_V1)) {
        return STATUS_INVALID_PARAMETER;
    }

    // Refresh secret if not loaded
    if (g_MachineSecret.empty()) {
        FaceUnlockAuth::AuthPackageCore::LoadMachineSecretFromDpapi(g_MachineSecret);
    }

    if (g_MachineSecret.empty()) {
        LogLsa("[FaceUnlockAuthPackage] Machine secret not available\n");
        return STATUS_AUTHENTICATION_FIREWALL_FAILED;
    }

    FACEUNLOCK_LOGON_V1 logonData{};
    auto verifyRes = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(
        reinterpret_cast<const BYTE*>(ProtocolSubmitBuffer),
        SubmitBufferSize,
        g_MachineSecret.data(),
        &logonData
    );

    if (verifyRes != FaceUnlockAuth::VerifyResult::Success) {
        LogLsa("[FaceUnlockAuthPackage] Ticket validation failed\n");
        switch (verifyRes) {
            case FaceUnlockAuth::VerifyResult::Expired:
                return STATUS_PASSWORD_EXPIRED;
            case FaceUnlockAuth::VerifyResult::ReplayedNonce:
                return STATUS_ACCOUNT_RESTRICTION;
            case FaceUnlockAuth::VerifyResult::InvalidHmac:
                return STATUS_LOGON_FAILURE;
            default:
                return STATUS_LOGON_FAILURE;
        }
    }

    // Convert wszUserSid to PSID
    PSID pUserSid = nullptr;
    if (!ConvertStringSidToSidW(logonData.wszUserSid, &pUserSid)) {
        LogLsa("[FaceUnlockAuthPackage] Failed to convert User SID\n");
        return STATUS_NO_SUCH_USER;
    }

    // Allocate and populate TokenInformation (LSA_TOKEN_INFORMATION_V1)
    auto* pTokenInfo = static_cast<PLSA_TOKEN_INFORMATION_V1>(
        g_LsaDispatchTable.AllocateLsaHeap
            ? g_LsaDispatchTable.AllocateLsaHeap(sizeof(LSA_TOKEN_INFORMATION_V1))
            : LocalAlloc(LMEM_ZEROINIT, sizeof(LSA_TOKEN_INFORMATION_V1))
    );

    if (!pTokenInfo) {
        LocalFree(pUserSid);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    ZeroMemory(pTokenInfo, sizeof(LSA_TOKEN_INFORMATION_V1));

    // Expiration time (never expire local session)
    pTokenInfo->ExpirationTime.QuadPart = 0x7FFFFFFFFFFFFFFFLL;

    // User SID
    pTokenInfo->User.User.Sid = pUserSid;
    pTokenInfo->User.User.Attributes = 0;

    // Groups: Primary group (Builtin Users / None)
    pTokenInfo->Groups = nullptr;

    // Set Token Source identifier
    if (TokenSource) {
        memcpy_s(TokenSource->SourceName, sizeof(TokenSource->SourceName), "FaceUnlk", 8);
        AllocateLocallyUniqueId(&TokenSource->SourceIdentifier);
    }

    // Profile Buffer (MSV1_0_INTERACTIVE_PROFILE)
    if (ProfileBuffer && ProfileBufferSize) {
        auto* pProfile = static_cast<PMSV1_0_INTERACTIVE_PROFILE>(
            g_LsaDispatchTable.AllocateLsaHeap
                ? g_LsaDispatchTable.AllocateLsaHeap(sizeof(MSV1_0_INTERACTIVE_PROFILE))
                : LocalAlloc(LMEM_ZEROINIT, sizeof(MSV1_0_INTERACTIVE_PROFILE))
        );
        if (pProfile) {
            ZeroMemory(pProfile, sizeof(MSV1_0_INTERACTIVE_PROFILE));
            pProfile->MessageType = MsV1_0InteractiveProfile;
            *ProfileBuffer = pProfile;
            *ProfileBufferSize = sizeof(MSV1_0_INTERACTIVE_PROFILE);
        }
    }

    if (TokenInformation) {
        *TokenInformation = pTokenInfo;
    }
    if (TokenInformationType) {
        *TokenInformationType = LsaTokenInformationV1;
    }

    LogLsa("[FaceUnlockAuthPackage] SpLogonUserEx2 succeeded with valid token info\n");
    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// Package Function Tables
// -------------------------------------------------------------------------
static SECPKG_USER_FUNCTION_TABLE g_UserFunctionTable = {
    nullptr, // InstanceInit
    nullptr, // InstanceShutdown
    nullptr, // SetContextAttributes
    nullptr, // QueryContextAttributes
};

static SECPKG_FUNCTION_TABLE g_FunctionTable = {
    SpInitialize,
    SpShutDown,
    SpGetInfo,
    SpAcceptCredentials,
    nullptr, // SpAcquireCredentialsHandle
    nullptr, // SpQueryCredentialsAttributes
    nullptr, // SpFreeCredentialsHandle
    nullptr, // SpSaveCredentials
    nullptr, // SpGetCredentials
    nullptr, // SpDeleteCredentials
    nullptr, // SpInitLsaModeContext
    nullptr, // SpAcceptLsaModeContext
    nullptr, // SpDeleteContext
    nullptr, // SpApplyControlToken
    nullptr, // SpGetUserInfo
    nullptr, // SpGetExtendedInformation
    nullptr, // SpQueryContextAttributes
    nullptr, // SpAddCredentials
    nullptr, // SpSetContextAttributes
    nullptr, // SpSetCredentialsAttributes
    nullptr, // SpExportSecurityContext
    nullptr, // SpImportSecurityContext
    nullptr, // SpFormatCredentials
    nullptr, // SpMarshallSupplementalCreds
    nullptr, // SpExportHypotheticalContext
    nullptr, // SpLogonTerminated
    SpLogonUserEx2
};

// -------------------------------------------------------------------------
// SpLsaModeInitialize Export
// -------------------------------------------------------------------------
extern "C" __declspec(dllexport) NTSTATUS NTAPI SpLsaModeInitialize(
    ULONG LsaVersion,
    PULONG PackageVersion,
    PSECPKG_FUNCTION_TABLE* ppTables,
    PULONG pcTables
) {
    if (!PackageVersion || !ppTables || !pcTables) {
        return STATUS_INVALID_PARAMETER;
    }

    *PackageVersion = SECPKG_INTERFACE_VERSION;
    *ppTables = &g_FunctionTable;
    *pcTables = 1;

    return STATUS_SUCCESS;
}

extern "C" __declspec(dllexport) NTSTATUS NTAPI SpUserModeInitialize(
    ULONG LsaVersion,
    PULONG PackageVersion,
    PSECPKG_USER_FUNCTION_TABLE* ppTables,
    PULONG pcTables
) {
    if (!PackageVersion || !ppTables || !pcTables) {
        return STATUS_INVALID_PARAMETER;
    }

    *PackageVersion = SECPKG_INTERFACE_VERSION;
    *ppTables = &g_UserFunctionTable;
    *pcTables = 1;

    return STATUS_SUCCESS;
}
