#pragma once
#include <windows.h>
#include <credentialprovider.h>
// This minimal COM factory/provider exists so the project has a concrete integration point.
// It intentionally enumerates zero credentials until standard credential serialization is implemented.
HRESULT CreateFaceUnlockProvider(REFIID riid, void** ppv);
extern const CLSID CLSID_FaceUnlockProvider;
