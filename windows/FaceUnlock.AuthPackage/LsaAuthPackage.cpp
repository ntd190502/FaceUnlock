#ifndef SECURITY_WIN32
#define SECURITY_WIN32
#endif
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <security.h>
#include <ntsecapi.h>
#include <sddl.h>
#include <strsafe.h>
#include "FaceUnlockAuthCommon.h"
#include "AuthPackageCore.h"

#ifndef RPC_C_AUTHN_NONE
#define RPC_C_AUTHN_NONE 0
#endif

// Global LSA dispatch table and package ID
static LSA_DISPATCH_TABLE g_LsaDispatchTable;
static ULONG_PTR g_PackageId = 0;
static std::vector<BYTE> g_MachineSecret;

static void LogLsa(const char* msg) {
    OutputDebugStringA(msg);
}

// -------------------------------------------------------------------------
// LsaApInitializePackage (LsaApInitializePackage)
// -------------------------------------------------------------------------
extern "C" __declspec(dllexport) NTSTATUS NTAPI LsaApInitializePackage(
    ULONG_PTR PackageId,
    PLSA_DISPATCH_TABLE FunctionTable,
    PLSA_STRING Database,
    PLSA_STRING Confidentiality,
    PLSA_STRING* PackageName
) {
    g_PackageId = PackageId;
    if (FunctionTable) {
        memcpy_s(&g_LsaDispatchTable, sizeof(g_LsaDispatchTable), FunctionTable, sizeof(LSA_DISPATCH_TABLE));
    }

    if (PackageName) {
        auto* pName = static_cast<PLSA_STRING>(
            g_LsaDispatchTable.AllocateLsaHeap
                ? g_LsaDispatchTable.AllocateLsaHeap(sizeof(LSA_STRING))
                : LocalAlloc(LMEM_ZEROINIT, sizeof(LSA_STRING))
        );
        if (pName) {
            static const char kName[] = FACEUNLOCK_AUTHPACKAGE_NAME_A;
            pName->Length = static_cast<USHORT>(strlen(kName));
            pName->MaximumLength = pName->Length + 1;
            pName->Buffer = static_cast<PCHAR>(
                g_LsaDispatchTable.AllocateLsaHeap
                    ? g_LsaDispatchTable.AllocateLsaHeap(pName->MaximumLength)
                    : LocalAlloc(LMEM_ZEROINIT, pName->MaximumLength)
            );
            if (pName->Buffer) {
                memcpy_s(pName->Buffer, pName->MaximumLength, kName, pName->MaximumLength);
            }
            *PackageName = pName;
        }
    }

    FaceUnlockAuth::AuthPackageCore::LoadMachineSecretFromDpapi(g_MachineSecret);
    LogLsa("[FaceUnlockAuthPackage] LsaApInitializePackage completed\n");
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
// LsaApLogonUserEx2
// -------------------------------------------------------------------------
extern "C" __declspec(dllexport) NTSTATUS NTAPI LsaApLogonUserEx2(
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

    LogLsa("[FaceUnlockAuthPackage] LsaApLogonUserEx2 succeeded with valid token info\n");
    return STATUS_SUCCESS;
}

// -------------------------------------------------------------------------
// Other LSA AP entry points
// -------------------------------------------------------------------------
extern "C" __declspec(dllexport) NTSTATUS NTAPI LsaApCallPackage(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferSize,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferSize,
    PNTSTATUS ProtocolStatus
) {
    if (ProtocolStatus) *ProtocolStatus = STATUS_NOT_SUPPORTED;
    return STATUS_NOT_SUPPORTED;
}

extern "C" __declspec(dllexport) NTSTATUS NTAPI LsaApCallPackageUntrusted(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferSize,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferSize,
    PNTSTATUS ProtocolStatus
) {
    if (ProtocolStatus) *ProtocolStatus = STATUS_NOT_SUPPORTED;
    return STATUS_NOT_SUPPORTED;
}

extern "C" __declspec(dllexport) NTSTATUS NTAPI LsaApCallPackagePassthrough(
    PLSA_CLIENT_REQUEST ClientRequest,
    PVOID ProtocolSubmitBuffer,
    PVOID ClientBufferBase,
    ULONG SubmitBufferSize,
    PVOID* ProtocolReturnBuffer,
    PULONG ReturnBufferSize,
    PNTSTATUS ProtocolStatus
) {
    if (ProtocolStatus) *ProtocolStatus = STATUS_NOT_SUPPORTED;
    return STATUS_NOT_SUPPORTED;
}

extern "C" __declspec(dllexport) VOID NTAPI LsaApLogonTerminated(
    PLUID LogonId
) {
}
