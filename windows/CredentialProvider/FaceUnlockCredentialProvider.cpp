#include "FaceUnlockCredentialProvider.h"
#include <new>

// {64D6E84B-4969-4B59-A11A-58C3D9FA0110}
const CLSID CLSID_FaceUnlockProvider = {0x64d6e84b,0x4969,0x4b59,{0xa1,0x1a,0x58,0xc3,0xd9,0xfa,0x01,0x10}};

class Provider final : public ICredentialProvider {
    LONG refs_=1; CREDENTIAL_PROVIDER_USAGE_SCENARIO usage_{};
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override { if(!ppv)return E_POINTER;*ppv=nullptr;if(riid==IID_IUnknown||riid==IID_ICredentialProvider){*ppv=static_cast<ICredentialProvider*>(this);AddRef();return S_OK;}return E_NOINTERFACE; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override { auto r=InterlockedDecrement(&refs_);if(!r)delete this;return r; }
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,DWORD) override { usage_=cpus; return (cpus==CPUS_LOGON||cpus==CPUS_UNLOCK_WORKSTATION)?S_OK:E_NOTIMPL; }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }
    IFACEMETHODIMP Advise(ICredentialProviderEvents*,UINT_PTR) override { return S_OK; }
    IFACEMETHODIMP UnAdvise() override { return S_OK; }
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override { if(!count)return E_POINTER;*count=0;return S_OK; }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD,CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR**) override { return E_INVALIDARG; }
    IFACEMETHODIMP GetCredentialCount(DWORD* count,DWORD* def,BOOL* autoLogon) override { if(!count||!def||!autoLogon)return E_POINTER; *count=0;*def=CREDENTIAL_PROVIDER_NO_DEFAULT;*autoLogon=FALSE;return S_OK; }
    IFACEMETHODIMP GetCredentialAt(DWORD,ICredentialProviderCredential**) override { return E_INVALIDARG; }
};
HRESULT CreateFaceUnlockProvider(REFIID riid,void** ppv){auto p=new(std::nothrow) Provider();if(!p)return E_OUTOFMEMORY;auto hr=p->QueryInterface(riid,ppv);p->Release();return hr;}
