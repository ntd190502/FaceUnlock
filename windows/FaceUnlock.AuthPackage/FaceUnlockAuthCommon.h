#pragma once
#include <windows.h>

#define FACEUNLOCK_AUTHPACKAGE_NAME_A "FaceUnlock"
#define FACEUNLOCK_AUTHPACKAGE_NAME_W L"FaceUnlock"
#define FACEUNLOCK_AUTHPACKAGE_COMMENT_W L"FaceUnlock True Passwordless Authentication Package"

#define FACEUNLOCK_LOGON_MAGIC 0x46554C4B // 'FULK'
#define FACEUNLOCK_LOGON_VERSION 1
#define FACEUNLOCK_MAX_TICKET_TTL_SECONDS 30

#pragma pack(push, 1)
typedef struct _FACEUNLOCK_LOGON_V1 {
    DWORD dwMagic;             // 'FULK' (0x46554C4B)
    DWORD dwVersion;           // 1
    DWORD cbTotalSize;         // Total byte size of this struct
    CHAR  szRequestId[64];     // UUIDv4 format
    WCHAR wszUserSid[128];     // Target account SID (e.g. S-1-5-21-...)
    WCHAR wszAccountName[256]; // Target local username
    WCHAR wszMachineName[256]; // Target computer name
    CHAR  szDeviceId[64];      // Paired iPhone Device UUID
    INT64 nIssuedAt;           // Unix epoch timestamp (seconds)
    INT64 nExpiresAt;          // Unix epoch timestamp (seconds)
    BYTE  bNonce[16];          // 16 random bytes unique per ticket
    BYTE  bHmacSignature[32];  // HMAC-SHA256(dwMagic..bNonce, machine_secret)
} FACEUNLOCK_LOGON_V1, *PFACEUNLOCK_LOGON_V1;
#pragma pack(pop)
