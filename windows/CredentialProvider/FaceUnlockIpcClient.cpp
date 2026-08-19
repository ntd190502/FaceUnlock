#include "FaceUnlockIpcClient.h"
#include "FaceUnlockCredentialProvider.h"
#include <strsafe.h>

static const WCHAR* const kPipeName = L"\\\\.\\pipe\\FaceUnlock.Auth.v1";

// Helper to escape basic JSON string characters
static std::string EscapeJson(const std::string& input) {
    std::string out;
    out.reserve(input.size() + 8);
    for (char c : input) {
        if (c == '"') out += "\\\"";
        else if (c == '\\') out += "\\\\";
        else if (c == '\b') out += "\\b";
        else if (c == '\f') out += "\\f";
        else if (c == '\n') out += "\\n";
        else if (c == '\r') out += "\\r";
        else if (c == '\t') out += "\\t";
        else out += c;
    }
    return out;
}

static std::string Utf16ToUtf8(const std::wstring& wstr) {
    if (wstr.empty()) return std::string();
    int sizeNeeded = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), nullptr, 0, nullptr, nullptr);
    if (sizeNeeded <= 0) return std::string();
    std::string str(sizeNeeded, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), &str[0], sizeNeeded, nullptr, nullptr);
    return str;
}

static std::wstring Utf8ToUtf16(const std::string& str) {
    if (str.empty()) return std::wstring();
    int sizeNeeded = MultiByteToWideChar(CP_UTF8, 0, str.data(), static_cast<int>(str.size()), nullptr, 0);
    if (sizeNeeded <= 0) return std::wstring();
    std::wstring wstr(sizeNeeded, 0);
    MultiByteToWideChar(CP_UTF8, 0, str.data(), static_cast<int>(str.size()), &wstr[0], sizeNeeded);
    return wstr;
}

// Lightweight JSON field extractor for "status", "message", and "expires_at"
static std::string ExtractJsonField(const std::string& json, const std::string& key) {
    std::string pattern = "\"" + key + "\":\"";
    size_t pos = json.find(pattern);
    if (pos == std::string::npos) return "";
    pos += pattern.length();
    size_t endPos = json.find("\"", pos);
    if (endPos == std::string::npos) return "";
    return json.substr(pos, endPos - pos);
}

static long long ExtractJsonLong(const std::string& json, const std::string& key) {
    std::string pattern = "\"" + key + "\":";
    size_t pos = json.find(pattern);
    if (pos == std::string::npos) return 0;
    pos += pattern.length();
    while (pos < json.length() && (json[pos] == ' ' || json[pos] == '\t')) pos++;
    size_t endPos = json.find_first_of(",}\r\n ", pos);
    if (endPos == std::string::npos) endPos = json.length();
    std::string numStr = json.substr(pos, endPos - pos);
    try {
        return std::stoll(numStr);
    } catch (...) {
        return 0;
    }
}

static HANDLE ConnectPipeWithTimeout(DWORD timeoutMs, FaceUnlockIpcResult& outErr, const std::atomic<bool>* cancelToken = nullptr) {
    HANDLE hPipe = INVALID_HANDLE_VALUE;
    DWORD startTick = GetTickCount();

    while (true) {
        if (cancelToken && cancelToken->load(std::memory_order_seq_cst)) {
            outErr.ok = false;
            outErr.status = L"cancelled";
            outErr.message = L"Cancelled by caller";
            return INVALID_HANDLE_VALUE;
        }

        hPipe = CreateFileW(
            kPipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr
        );

        if (hPipe != INVALID_HANDLE_VALUE) {
            return hPipe;
        }

        DWORD err = GetLastError();
        if (err != ERROR_PIPE_BUSY) {
            outErr.ok = false;
            outErr.status = L"service_not_running";
            outErr.message = L"FaceUnlock Service is not running";
            return INVALID_HANDLE_VALUE;
        }

        // Poll in small slices to remain responsive to cancelToken
        DWORD waitSlice = (timeoutMs > 200) ? 200 : timeoutMs;
        if (!WaitNamedPipeW(kPipeName, waitSlice)) {
            if (GetTickCount() - startTick >= timeoutMs) {
                outErr.ok = false;
                outErr.status = L"timeout";
                outErr.message = L"Named pipe busy timeout";
                return INVALID_HANDLE_VALUE;
            }
        }
    }
}

static FaceUnlockIpcResult ExecuteSingleCommand(
    const std::string& jsonRequest,
    const std::string& successStatus,
    DWORD timeoutMs)
{
    FaceUnlockIpcResult result = { false, L"error", L"Failed to connect to FaceUnlock Service", 0 };

    HANDLE hPipe = ConnectPipeWithTimeout(timeoutMs, result);
    if (hPipe == INVALID_HANDLE_VALUE) {
        return result;
    }

    DWORD bytesWritten = 0;
    BOOL writeOk = WriteFile(
        hPipe,
        jsonRequest.c_str(),
        static_cast<DWORD>(jsonRequest.length()),
        &bytesWritten,
        nullptr
    );

    if (!writeOk) {
        CloseHandle(hPipe);
        result.status = L"error";
        result.message = L"Failed to send request to FaceUnlock Service";
        return result;
    }

    std::string responseBuffer;
    char buffer[1024];
    DWORD startTick = GetTickCount();

    while (true) {
        DWORD bytesAvailable = 0;
        if (PeekNamedPipe(hPipe, nullptr, 0, nullptr, &bytesAvailable, nullptr)) {
            if (bytesAvailable > 0) {
                DWORD bytesRead = 0;
                BOOL readOk = ReadFile(hPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr);
                if (!readOk || bytesRead == 0) {
                    break;
                }

                buffer[bytesRead] = '\0';
                responseBuffer += buffer;

                size_t newlinePos = responseBuffer.find('\n');
                if (newlinePos != std::string::npos) {
                    std::string line = responseBuffer.substr(0, newlinePos);
                    if (!line.empty() && line.back() == '\r') {
                        line.pop_back();
                    }

                    std::string status = ExtractJsonField(line, "status");
                    std::string msg = ExtractJsonField(line, "message");
                    std::string ticket = ExtractJsonField(line, "ticket");
                    long long exp = ExtractJsonLong(line, "expires_at");

                    result.status = Utf8ToUtf16(status);
                    result.message = Utf8ToUtf16(msg);
                    result.expires_at = exp;
                    result.ticket = ticket;
                    result.ok = (status == successStatus);

                    CloseHandle(hPipe);
                    return result;
                }
            } else {
                Sleep(20);
            }
        } else {
            break;
        }

        if (GetTickCount() - startTick >= timeoutMs) {
            result.status = L"timeout";
            result.message = L"Timed out waiting for response";
            break;
        }
    }

    CloseHandle(hPipe);
    return result;
}

FaceUnlockIpcResult FaceUnlockIpcClient::Ping(DWORD timeoutMs) {
    std::string json = "{\"version\":1,\"command\":\"ping\"}\n";
    return ExecuteSingleCommand(json, "ok", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::ReserveGrant(const std::wstring& requestId, DWORD timeoutMs) {
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string json = "{\"version\":1,\"command\":\"reserve_grant\",\"request_id\":\"" + reqIdUtf8 + "\"}\n";
    return ExecuteSingleCommand(json, "reserved", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::ReleaseGrant(const std::wstring& requestId, DWORD timeoutMs) {
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string json = "{\"version\":1,\"command\":\"release_grant\",\"request_id\":\"" + reqIdUtf8 + "\"}\n";
    return ExecuteSingleCommand(json, "approved", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::ConsumeGrant(const std::wstring& requestId, DWORD timeoutMs) {
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string json = "{\"version\":1,\"command\":\"consume_grant\",\"request_id\":\"" + reqIdUtf8 + "\"}\n";
    return ExecuteSingleCommand(json, "consumed", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::CancelRequest(const std::wstring& requestId, DWORD timeoutMs) {
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string json = "{\"version\":1,\"command\":\"cancel_request\",\"request_id\":\"" + reqIdUtf8 + "\"}\n";
    return ExecuteSingleCommand(json, "cancelled", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::GrantStatus(const std::wstring& requestId, DWORD timeoutMs) {
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string json = "{\"version\":1,\"command\":\"grant_status\",\"request_id\":\"" + reqIdUtf8 + "\"}\n";
    return ExecuteSingleCommand(json, "approved", timeoutMs);
}

FaceUnlockIpcResult FaceUnlockIpcClient::RequestUnlock(
    const std::wstring& requestId,
    const std::wstring& usage,
    const std::wstring& userSid,
    const std::wstring& qualifiedUsername,
    DWORD timeoutMs,
    const std::atomic<bool>* cancelToken)
{
    FaceUnlockIpcResult result = { false, L"error", L"Failed to connect to FaceUnlock Service", 0 };

    // Step 1: Send request_unlock and get immediate ACK
    std::string reqIdUtf8   = EscapeJson(Utf16ToUtf8(requestId));
    std::string usageUtf8   = EscapeJson(Utf16ToUtf8(usage));
    std::string sidUtf8     = EscapeJson(Utf16ToUtf8(userSid));
    std::string qualUserUtf8 = EscapeJson(Utf16ToUtf8(qualifiedUsername));

    std::string jsonRequest = "{\"version\":1,\"command\":\"request_unlock\",\"request_id\":\"" +
        reqIdUtf8 + "\",\"usage\":\"" + usageUtf8 + "\",\"user_sid\":\"" + sidUtf8 +
        "\",\"qualified_username\":\"" + qualUserUtf8 + "\",\"username\":\"" + qualUserUtf8 + "\"}\n";

    char logBuf[256];
    StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC connect -> send request_unlock reqId=%.16ls", requestId.c_str());
    AppendCpLog(logBuf);

    FaceUnlockIpcResult ackResult = ExecuteSingleCommand(jsonRequest, "pending", 5000);
    if (!ackResult.ok && ackResult.status != L"approved") {
        StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC request_unlock failed: status=%ls message=%ls",
            ackResult.status.c_str(), ackResult.message.c_str());
        AppendCpLog(logBuf);
        return ackResult;
    }

    if (ackResult.status == L"approved") {
        AppendCpLog("IPC request_unlock: approved immediately");
        return ackResult;
    }

    StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC request_unlock ACK received: status=%ls. Beginning poll...",
        ackResult.status.c_str());
    AppendCpLog(logBuf);

    // Step 2: Poll grant_status every 300ms until approved / terminal status / timeout / cancel
    DWORD startTick = GetTickCount();
    while (true) {
        if (cancelToken && cancelToken->load(std::memory_order_seq_cst)) {
            AppendCpLog("IPC poll: cancelled by caller");
            CancelRequest(requestId, 2000);
            result.ok = false;
            result.status = L"cancelled";
            result.message = L"Request cancelled by caller";
            return result;
        }

        if (GetTickCount() - startTick >= timeoutMs) {
            AppendCpLog("IPC poll: timed out waiting for iPhone Face ID");
            CancelRequest(requestId, 2000);
            result.ok = false;
            result.status = L"timeout";
            result.message = L"Request timed out waiting for Face ID";
            return result;
        }

        Sleep(300);

        if (cancelToken && cancelToken->load(std::memory_order_seq_cst)) {
            AppendCpLog("IPC poll: cancelled by caller after sleep");
            CancelRequest(requestId, 2000);
            result.ok = false;
            result.status = L"cancelled";
            result.message = L"Request cancelled by caller";
            return result;
        }

        FaceUnlockIpcResult pollRes = GrantStatus(requestId, 2000);
        if (pollRes.status == L"approved") {
            StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC poll: APPROVED reqId=%.16ls exp=%lld",
                requestId.c_str(), pollRes.expires_at);
            AppendCpLog(logBuf);
            return pollRes;
        } else if (pollRes.status == L"rejected" || pollRes.status == L"timeout" ||
                   pollRes.status == L"cancelled" || pollRes.status == L"not_paired" ||
                   pollRes.status == L"expired")
        {
            StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC poll: terminal status=%ls message=%ls",
                pollRes.status.c_str(), pollRes.message.c_str());
            AppendCpLog(logBuf);
            return pollRes;
        } else if (pollRes.status == L"pending" || pollRes.status == L"not_found") {
            // Still waiting for iPhone approval
            continue;
        } else {
            // Service communication error or service died
            StringCchPrintfA(logBuf, ARRAYSIZE(logBuf), "IPC poll error: status=%ls msg=%ls",
                pollRes.status.c_str(), pollRes.message.c_str());
            AppendCpLog(logBuf);
        }
    }
}

FaceUnlockIpcResult FaceUnlockIpcClient::IssueLsaTicket(
    const std::wstring& requestId,
    const std::wstring& userSid,
    const std::wstring& qualifiedUsername,
    DWORD timeoutMs
) {
    std::string reqIdUtf8 = Utf16ToUtf8(requestId);
    std::string sidUtf8   = Utf16ToUtf8(userSid);
    std::string userUtf8  = Utf16ToUtf8(qualifiedUsername);

    std::string json = "{\"version\":1,\"command\":\"issue_lsa_ticket\",\"request_id\":\"" +
                       EscapeJson(reqIdUtf8) +
                       "\",\"user_sid\":\"" +
                       EscapeJson(sidUtf8) +
                       "\",\"qualified_username\":\"" +
                       EscapeJson(userUtf8) +
                       "\"}\n";

    return ExecuteSingleCommand(json, "approved", timeoutMs);
}
