#pragma once
#include <windows.h>
#include <string>
#include <atomic>

struct FaceUnlockIpcResult {
    bool ok;
    std::wstring status;
    std::wstring message;
    long long expires_at;
};

class FaceUnlockIpcClient {
public:
    static FaceUnlockIpcResult Ping(
        DWORD timeoutMs = 3000
    );

    static FaceUnlockIpcResult RequestUnlock(
        const std::wstring& requestId,
        const std::wstring& usage,
        const std::wstring& userSid,
        const std::wstring& qualifiedUsername,
        DWORD timeoutMs = 90000,
        const std::atomic<bool>* cancelToken = nullptr
    );

    static FaceUnlockIpcResult GrantStatus(
        const std::wstring& requestId,
        DWORD timeoutMs = 3000
    );

    static FaceUnlockIpcResult ReserveGrant(
        const std::wstring& requestId,
        DWORD timeoutMs = 5000
    );

    static FaceUnlockIpcResult ReleaseGrant(
        const std::wstring& requestId,
        DWORD timeoutMs = 5000
    );

    static FaceUnlockIpcResult ConsumeGrant(
        const std::wstring& requestId,
        DWORD timeoutMs = 5000
    );

    static FaceUnlockIpcResult CancelRequest(
        const std::wstring& requestId,
        DWORD timeoutMs = 3000
    );
};
