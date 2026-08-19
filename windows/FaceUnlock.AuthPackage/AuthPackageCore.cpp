#include "AuthPackageCore.h"
#include <bcrypt.h>
#include <wincrypt.h>
#include <sddl.h>
#include <mutex>
#include <unordered_map>
#include <chrono>
#include <strsafe.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")

namespace FaceUnlockAuth {

struct NonceEntry {
    INT64 expiresAt;
};

// Thread-safe in-memory cache for one-time nonce replay protection
static std::mutex g_nonceMutex;
static std::unordered_map<std::string, NonceEntry> g_consumedNonces;

static std::string NonceToHex(const BYTE* pNonce, size_t len) {
    char hex[33] = { 0 };
    for (size_t i = 0; i < len && i < 16; ++i) {
        StringCchPrintfA(hex + (i * 2), 3, "%02x", pNonce[i]);
    }
    return std::string(hex);
}

void AuthPackageCore::ClearNonceCacheForTesting() {
    std::lock_guard<std::mutex> lock(g_nonceMutex);
    g_consumedNonces.clear();
}

bool AuthPackageCore::CheckAndRecordNonce(const BYTE* pNonce16, INT64 nExpiresAt) {
    if (!pNonce16) return false;

    std::string key = NonceToHex(pNonce16, 16);
    auto now = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();

    std::lock_guard<std::mutex> lock(g_nonceMutex);

    // 1. Prune expired nonces
    for (auto it = g_consumedNonces.begin(); it != g_consumedNonces.end(); ) {
        if (it->second.expiresAt < now) {
            it = g_consumedNonces.erase(it);
        } else {
            ++it;
        }
    }

    // 2. Check if already consumed
    if (g_consumedNonces.find(key) != g_consumedNonces.end()) {
        return false; // Replayed!
    }

    // 3. Record nonce
    g_consumedNonces[key] = NonceEntry{ nExpiresAt };
    return true;
}

bool AuthPackageCore::LoadMachineSecretFromDpapi(std::vector<BYTE>& outSecret) {
    outSecret.clear();

    WCHAR appData[MAX_PATH] = { 0 };
    if (GetEnvironmentVariableW(L"ProgramData", appData, ARRAYSIZE(appData)) == 0) {
        StringCchCopyW(appData, ARRAYSIZE(appData), L"C:\\ProgramData");
    }

    std::wstring secretPath = std::wstring(appData) + L"\\FaceUnlock\\lsa_secret.dpapi";

    HANDLE hFile = CreateFileW(
        secretPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr
    );

    if (hFile == INVALID_HANDLE_VALUE) {
        return false;
    }

    DWORD fileSize = GetFileSize(hFile, nullptr);
    if (fileSize == INVALID_FILE_SIZE || fileSize == 0 || fileSize > 65536) {
        CloseHandle(hFile);
        return false;
    }

    std::vector<BYTE> encryptedData(fileSize);
    DWORD bytesRead = 0;
    BOOL readOk = ReadFile(hFile, encryptedData.data(), fileSize, &bytesRead, nullptr);
    CloseHandle(hFile);

    if (!readOk || bytesRead != fileSize) {
        return false;
    }

    // Unprotect using DPAPI LocalMachine scope with entropy "FaceUnlock-LSA-Secret-v1"
    DATA_BLOB dataIn{};
    dataIn.pbData = encryptedData.data();
    dataIn.cbData = static_cast<DWORD>(encryptedData.size());

    static const char kEntropy[] = "FaceUnlock-LSA-Secret-v1";
    DATA_BLOB entropyBlob{};
    entropyBlob.pbData = const_cast<BYTE*>(reinterpret_cast<const BYTE*>(kEntropy));
    entropyBlob.cbData = static_cast<DWORD>(strlen(kEntropy));

    DATA_BLOB dataOut{};
    if (!CryptUnprotectData(&dataIn, nullptr, &entropyBlob, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &dataOut)) {
        return false;
    }

    if (dataOut.cbData != 32 || dataOut.pbData == nullptr) {
        if (dataOut.pbData) LocalFree(dataOut.pbData);
        return false;
    }

    outSecret.assign(dataOut.pbData, dataOut.pbData + dataOut.cbData);
    SecureZeroMemory(dataOut.pbData, dataOut.cbData);
    LocalFree(dataOut.pbData);

    return true;
}

VerifyResult AuthPackageCore::VerifyTicketBuffer(
    const BYTE* pBuffer,
    DWORD cbBufferSize,
    const BYTE* pMachineSecret32,
    FACEUNLOCK_LOGON_V1* pOutLogonData
) {
    if (!pBuffer) {
        return VerifyResult::NullPointer;
    }

    if (cbBufferSize < sizeof(FACEUNLOCK_LOGON_V1)) {
        return VerifyResult::BufferTooSmall;
    }

    const auto* pTicket = reinterpret_cast<const FACEUNLOCK_LOGON_V1*>(pBuffer);

    // 1. Magic check
    if (pTicket->dwMagic != FACEUNLOCK_LOGON_MAGIC) {
        return VerifyResult::InvalidMagic;
    }

    // 2. Version check
    if (pTicket->dwVersion != FACEUNLOCK_LOGON_VERSION) {
        return VerifyResult::InvalidVersion;
    }

    // 3. Size check
    if (pTicket->cbTotalSize != sizeof(FACEUNLOCK_LOGON_V1) || cbBufferSize < pTicket->cbTotalSize) {
        return VerifyResult::InvalidSize;
    }

    // 4. Timestamp validity check
    auto now = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();

    // Check future skew (> 10 seconds in future is invalid)
    if (pTicket->nIssuedAt > now + 10) {
        return VerifyResult::FutureTimestamp;
    }

    // Check expiration (now > expires_at OR TTL > MAX_TTL)
    if (now > pTicket->nExpiresAt || pTicket->nExpiresAt < pTicket->nIssuedAt || (pTicket->nExpiresAt - pTicket->nIssuedAt) > (FACEUNLOCK_MAX_TICKET_TTL_SECONDS + 10)) {
        return VerifyResult::Expired;
    }

    // 5. Machine secret verification
    if (!pMachineSecret32) {
        return VerifyResult::SecretUnavailable;
    }

    // Compute HMAC-SHA256 over struct fields up to bNonce (excluding bHmacSignature)
    DWORD cbPayloadToVerify = sizeof(FACEUNLOCK_LOGON_V1) - sizeof(pTicket->bHmacSignature);
    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    BYTE computedHash[32] = { 0 };

    NTSTATUS status = BCryptOpenAlgorithmProvider(
        &hAlg,
        BCRYPT_SHA256_ALGORITHM,
        nullptr,
        BCRYPT_ALG_HANDLE_HMAC_FLAG
    );

    if (status != 0) {
        return VerifyResult::InvalidHmac;
    }

    status = BCryptCreateHash(
        hAlg,
        &hHash,
        nullptr,
        0,
        const_cast<PUCHAR>(pMachineSecret32),
        32,
        0
    );

    if (status != 0) {
        BCryptCloseAlgorithmProvider(hAlg, 0);
        return VerifyResult::InvalidHmac;
    }

    status = BCryptHashData(hHash, const_cast<PUCHAR>(pBuffer), cbPayloadToVerify, 0);
    if (status == 0) {
        status = BCryptFinishHash(hHash, computedHash, sizeof(computedHash), 0);
    }

    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);

    if (status != 0) {
        return VerifyResult::InvalidHmac;
    }

    // Constant-time HMAC comparison
    int diff = 0;
    for (size_t i = 0; i < 32; ++i) {
        diff |= (computedHash[i] ^ pTicket->bHmacSignature[i]);
    }
    if (diff != 0) {
        return VerifyResult::InvalidHmac;
    }

    // 6. One-time nonce replay check
    if (!CheckAndRecordNonce(pTicket->bNonce, pTicket->nExpiresAt)) {
        return VerifyResult::ReplayedNonce;
    }

    // 7. Output parsed logon data if requested
    if (pOutLogonData) {
        memcpy_s(pOutLogonData, sizeof(FACEUNLOCK_LOGON_V1), pTicket, sizeof(FACEUNLOCK_LOGON_V1));
    }

    return VerifyResult::Success;
}

} // namespace FaceUnlockAuth
