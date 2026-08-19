#include "FaceUnlockIpcClient.h"
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
                    long long exp = ExtractJsonLong(line, "expires_at");

                    result.status = Utf8ToUtf16(status);
                    result.message = Utf8ToUtf16(msg);
                    result.expires_at = exp;
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

FaceUnlockIpcResult FaceUnlockIpcClient::RequestUnlock(
    const std::wstring& requestId,
    const std::wstring& usage,
    const std::wstring& username,
    DWORD timeoutMs,
    const std::atomic<bool>* cancelToken)
{
    FaceUnlockIpcResult result = { false, L"error", L"Failed to connect to FaceUnlock Service", 0 };

    HANDLE hPipe = ConnectPipeWithTimeout(timeoutMs, result, cancelToken);
    if (hPipe == INVALID_HANDLE_VALUE) {
        return result;
    }

    DWORD startTick = GetTickCount();

    // Prepare JSON Request
    std::string reqIdUtf8 = EscapeJson(Utf16ToUtf8(requestId));
    std::string usageUtf8 = EscapeJson(Utf16ToUtf8(usage));
    std::string userUtf8 = EscapeJson(Utf16ToUtf8(username));

    std::string jsonRequest = "{\"version\":1,\"command\":\"request_unlock\",\"request_id\":\"" +
        reqIdUtf8 + "\",\"usage\":\"" + usageUtf8 + "\",\"username\":\"" + userUtf8 + "\"}\n";

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

    // Read responses line by line with PeekNamedPipe to support cancellation and timeout
    std::string responseBuffer;
    char buffer[1024];

    while (true) {
        if (cancelToken && cancelToken->load(std::memory_order_seq_cst)) {
            CloseHandle(hPipe);
            result.ok = false;
            result.status = L"cancelled";
            result.message = L"Request cancelled by caller";
            return result;
        }

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

                // Check if we have a full newline-delimited JSON line
                size_t newlinePos = responseBuffer.find('\n');
                while (newlinePos != std::string::npos) {
                    std::string line = responseBuffer.substr(0, newlinePos);
                    responseBuffer = responseBuffer.substr(newlinePos + 1);

                    // Trim CR if present
                    if (!line.empty() && line.back() == '\r') {
                        line.pop_back();
                    }

                    if (!line.empty()) {
                        std::string status = ExtractJsonField(line, "status");
                        std::string msg = ExtractJsonField(line, "message");
                        long long exp = ExtractJsonLong(line, "expires_at");

                        result.status = Utf8ToUtf16(status);
                        result.message = Utf8ToUtf16(msg);
                        result.expires_at = exp;

                        if (status == "approved") {
                            result.ok = true;
                            CloseHandle(hPipe);
                            return result;
                        } else if (status != "pending") {
                            // Final non-approved status (rejected, timeout, error, not_paired, busy, cancelled, etc.)
                            result.ok = false;
                            CloseHandle(hPipe);
                            return result;
                        }
                    }

                    newlinePos = responseBuffer.find('\n');
                }
            } else {
                Sleep(50); // Yield to prevent CPU spin while waiting for iPhone auth
            }
        } else {
            break;
        }

        if (GetTickCount() - startTick >= timeoutMs) {
            result.status = L"timeout";
            result.message = L"Timed out waiting for Face ID response";
            break;
        }
    }

    CloseHandle(hPipe);
    return result;
}
