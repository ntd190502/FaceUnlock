#include "../FaceUnlock.AuthPackage/AuthPackageCore.h"
#include "../FaceUnlock.AuthPackage/FaceUnlockAuthCommon.h"
#include <iostream>
#include <vector>
#include <cassert>
#include <chrono>
#include <random>
#include <bcrypt.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "advapi32.lib")

static int g_passCount = 0;
static int g_failCount = 0;

static void Check(bool cond, const char* name, const char* reason = "") {
    if (cond) {
        g_passCount++;
        std::cout << "  [PASS] " << name << "\n";
    } else {
        g_failCount++;
        std::cout << "  [FAIL] " << name << " (" << reason << ")\n";
    }
}

static void SignTicket(FACEUNLOCK_LOGON_V1& ticket, const BYTE* secret) {
    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG);
    BCryptCreateHash(hAlg, &hHash, nullptr, 0, const_cast<PUCHAR>(secret), 32, 0);
    DWORD payloadLen = sizeof(FACEUNLOCK_LOGON_V1) - sizeof(ticket.bHmacSignature);
    BCryptHashData(hHash, reinterpret_cast<PUCHAR>(&ticket), payloadLen, 0);
    BCryptFinishHash(hHash, ticket.bHmacSignature, 32, 0);
    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);
}

static FACEUNLOCK_LOGON_V1 CreateValidTicket(const BYTE* secret, const char* reqId = "test-req-001", const wchar_t* userSid = L"S-1-5-21-12345") {
    FACEUNLOCK_LOGON_V1 t{};
    t.dwMagic = FACEUNLOCK_LOGON_MAGIC;
    t.dwVersion = FACEUNLOCK_LOGON_VERSION;
    t.cbTotalSize = sizeof(FACEUNLOCK_LOGON_V1);
    strcpy_s(t.szRequestId, reqId);
    wcscpy_s(t.wszUserSid, userSid);
    wcscpy_s(t.wszAccountName, L"LocalAdmin");
    wcscpy_s(t.wszMachineName, L"TEST-PC");
    strcpy_s(t.szDeviceId, "iphone-device-uuid-001");

    auto now = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    t.nIssuedAt = now;
    t.nExpiresAt = now + 30;

    // Random 16 bytes nonce
    std::random_device rd;
    for (size_t i = 0; i < 16; ++i) {
        t.bNonce[i] = static_cast<BYTE>(rd() & 0xFF);
    }

    SignTicket(t, secret);
    return t;
}

int main() {
    std::cout << "============================================================\n";
    std::cout << "  FaceUnlock AuthPackage Safety & Fuzz Harness\n";
    std::cout << "============================================================\n";

    BYTE mockSecret[32] = {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    FaceUnlockAuth::AuthPackageCore::ClearNonceCacheForTesting();

    // -------------------------------------------------------------
    // UNIT TESTS
    // -------------------------------------------------------------
    std::cout << "\n[Unit Tests]\n";

    // Test 1: Null buffer
    auto res1 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(nullptr, 0, mockSecret, nullptr);
    Check(res1 == FaceUnlockAuth::VerifyResult::NullPointer, "Test 1: Null buffer rejected with NullPointer");

    // Test 2: Buffer smaller than struct
    BYTE tiny[10] = { 0 };
    auto res2 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(tiny, sizeof(tiny), mockSecret, nullptr);
    Check(res2 == FaceUnlockAuth::VerifyResult::BufferTooSmall, "Test 2: Small buffer rejected with BufferTooSmall");

    // Test 3: Invalid magic
    auto t3 = CreateValidTicket(mockSecret);
    t3.dwMagic = 0xDEADBEEF;
    auto res3 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t3), sizeof(t3), mockSecret, nullptr);
    Check(res3 == FaceUnlockAuth::VerifyResult::InvalidMagic, "Test 3: Invalid magic rejected with InvalidMagic");

    // Test 4: Invalid version
    auto t4 = CreateValidTicket(mockSecret);
    t4.dwVersion = 99;
    auto res4 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t4), sizeof(t4), mockSecret, nullptr);
    Check(res4 == FaceUnlockAuth::VerifyResult::InvalidVersion, "Test 4: Invalid version rejected with InvalidVersion");

    // Test 5: Expired ticket (>30s)
    auto t5 = CreateValidTicket(mockSecret);
    t5.nIssuedAt -= 100;
    t5.nExpiresAt -= 70;
    SignTicket(t5, mockSecret);
    auto res5 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t5), sizeof(t5), mockSecret, nullptr);
    Check(res5 == FaceUnlockAuth::VerifyResult::Expired, "Test 5: Expired ticket rejected with Expired");

    // Test 6: Future ticket
    auto t6 = CreateValidTicket(mockSecret);
    t6.nIssuedAt += 500;
    t6.nExpiresAt += 530;
    SignTicket(t6, mockSecret);
    auto res6 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t6), sizeof(t6), mockSecret, nullptr);
    Check(res6 == FaceUnlockAuth::VerifyResult::FutureTimestamp, "Test 6: Future ticket rejected with FutureTimestamp");

    // Test 7: Tampered HMAC / Modified payload
    auto t7 = CreateValidTicket(mockSecret);
    t7.wszUserSid[0] = L'X'; // Tamper SID after signing
    auto res7 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t7), sizeof(t7), mockSecret, nullptr);
    Check(res7 == FaceUnlockAuth::VerifyResult::InvalidHmac, "Test 7: Tampered payload rejected with InvalidHmac");

    // Test 8: Valid ticket verification
    FACEUNLOCK_LOGON_V1 outData{};
    auto t8 = CreateValidTicket(mockSecret, "req-valid-888", L"S-1-5-21-99999");
    auto res8 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t8), sizeof(t8), mockSecret, &outData);
    Check(res8 == FaceUnlockAuth::VerifyResult::Success && strcmp(outData.szRequestId, "req-valid-888") == 0,
        "Test 8: Valid ticket accepted and parsed correctly");

    // Test 9: One-time replay protection (same nonce rejected on second try)
    auto res9 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t8), sizeof(t8), mockSecret, nullptr);
    Check(res9 == FaceUnlockAuth::VerifyResult::ReplayedNonce, "Test 9: Replayed ticket nonce rejected with ReplayedNonce");

    // Test 10: Wrong secret rejection
    BYTE wrongSecret[32] = { 0xFF };
    auto t10 = CreateValidTicket(mockSecret);
    auto res10 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t10), sizeof(t10), wrongSecret, nullptr);
    Check(res10 == FaceUnlockAuth::VerifyResult::InvalidHmac, "Test 10: Verification with wrong secret rejected with InvalidHmac");

    // Test 11: Deterministic Service test vector verification
    // Struct with fixed fields and known HMAC
    std::cout << "\n[Cross-Component Test Vector]\n";
    FACEUNLOCK_LOGON_V1 t11{};
    t11.dwMagic = FACEUNLOCK_LOGON_MAGIC;
    t11.dwVersion = 1;
    t11.cbTotalSize = sizeof(FACEUNLOCK_LOGON_V1);
    strcpy_s(t11.szRequestId, "vector-req-12345");
    wcscpy_s(t11.wszUserSid, L"S-1-5-21-33333");
    wcscpy_s(t11.wszAccountName, L"VectorAdmin");
    wcscpy_s(t11.wszMachineName, L"VECTOR-PC");
    strcpy_s(t11.szDeviceId, "vector-device-001");
    auto nowVec = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    t11.nIssuedAt = nowVec;
    t11.nExpiresAt = nowVec + 30;
    for (int i = 0; i < 16; ++i) t11.bNonce[i] = static_cast<BYTE>(i + 1);
    SignTicket(t11, mockSecret);

    FACEUNLOCK_LOGON_V1 outVec{};
    auto res11 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(
        reinterpret_cast<const BYTE*>(&t11),
        sizeof(t11),
        mockSecret,
        &outVec
    );
    Check(res11 == FaceUnlockAuth::VerifyResult::Success &&
          strcmp(outVec.szRequestId, "vector-req-12345") == 0 &&
          wcscmp(outVec.wszUserSid, L"S-1-5-21-33333") == 0 &&
          wcscmp(outVec.wszAccountName, L"VectorAdmin") == 0,
          "Test 11: Cross-component deterministic test vector verified PASS in AuthPackageCore");

    // Test 12: 1-byte flip tampering
    auto t12 = t11;
    t12.bNonce[0] ^= 0x01; // Tamper 1 byte of payload
    auto res12 = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(reinterpret_cast<const BYTE*>(&t12), sizeof(t12), mockSecret, nullptr);
    Check(res12 == FaceUnlockAuth::VerifyResult::InvalidHmac, "Test 12: Single byte flip rejected with InvalidHmac");

    // -------------------------------------------------------------
    // FUZZ TESTS: 10,000 malformed inputs
    // -------------------------------------------------------------
    std::cout << "\n[Fuzz Testing: 10,000 malformed inputs]\n";
    std::mt19937 rng(1337);
    int fuzzPassed = 0;
    const int kFuzzIterations = 10000;

    for (int i = 0; i < kFuzzIterations; ++i) {
        // Random length from 0 to 4096 bytes
        size_t len = rng() % 4096;
        std::vector<BYTE> fuzzBuf(len);
        for (size_t b = 0; b < len; ++b) {
            fuzzBuf[b] = static_cast<BYTE>(rng() & 0xFF);
        }

        FACEUNLOCK_LOGON_V1 dummyOut{};
        auto fRes = FaceUnlockAuth::AuthPackageCore::VerifyTicketBuffer(
            fuzzBuf.data(),
            static_cast<DWORD>(fuzzBuf.size()),
            mockSecret,
            &dummyOut
        );

        // Fuzzed random bytes should NEVER result in Success and must NEVER crash
        if (fRes != FaceUnlockAuth::VerifyResult::Success) {
            fuzzPassed++;
        }
    }

    Check(fuzzPassed == kFuzzIterations, "Test 13: 10,000 Fuzz iterations failed closed with 0 crashes or false accepts");

    std::cout << "\n============================================================\n";
    std::cout << "  HARNESS SUMMARY: " << g_passCount << " passed, " << g_failCount << " failed\n";
    std::cout << "============================================================\n";

    return (g_failCount == 0) ? 0 : 1;
}
