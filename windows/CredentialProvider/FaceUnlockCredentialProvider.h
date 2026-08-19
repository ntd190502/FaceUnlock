#pragma once
#include <windows.h>
#include <credentialprovider.h>

HRESULT CreateFaceUnlockProvider(REFIID riid, void** ppv);
extern const CLSID CLSID_FaceUnlockProvider;

// Diagnostic lifetime counters for verification & test harness
extern "C" {
    __declspec(dllexport) LONG WINAPI GetCredentialCtorCount();
    __declspec(dllexport) LONG WINAPI GetCredentialDtorCount();
    __declspec(dllexport) LONG WINAPI GetAuthWorkerCount();
}
