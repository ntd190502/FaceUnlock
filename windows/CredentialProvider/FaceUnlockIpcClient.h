#pragma once
#include <windows.h>
#include <string>

struct FaceUnlockIpcResult {
    bool ok;
    std::wstring status;
    std::wstring message;
    long long expires_at;
};

class FaceUnlockIpcClient {
public:
    static FaceUnlockIpcResult RequestUnlock(
        const std::wstring& requestId,
        const std::wstring& usage,
        const std::wstring& username,
        DWORD timeoutMs = 90000
    );
};
