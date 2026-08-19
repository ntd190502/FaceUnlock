#pragma once
#include "FaceUnlockAuthCommon.h"
#include <vector>
#include <string>

namespace FaceUnlockAuth {

enum class VerifyResult {
    Success,
    InvalidMagic,
    InvalidVersion,
    InvalidSize,
    BufferTooSmall,
    Expired,
    FutureTimestamp,
    ReplayedNonce,
    InvalidHmac,
    SecretUnavailable,
    InvalidUserSid,
    InvalidMachineName,
    NullPointer
};

class AuthPackageCore {
public:
    static VerifyResult VerifyTicketBuffer(
        const BYTE* pBuffer,
        DWORD cbBufferSize,
        const BYTE* pMachineSecret32,
        FACEUNLOCK_LOGON_V1* pOutLogonData
    );

    static bool LoadMachineSecretFromDpapi(
        std::vector<BYTE>& outSecret
    );

    static bool CheckAndRecordNonce(
        const BYTE* pNonce16,
        INT64 nExpiresAt
    );

    static void ClearNonceCacheForTesting();
};

} // namespace FaceUnlockAuth
