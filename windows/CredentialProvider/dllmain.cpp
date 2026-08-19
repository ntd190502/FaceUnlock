#include "FaceUnlockCredentialProvider.h"
#include <new>
static HMODULE g_module{}; static LONG g_locks{};
class Factory final:public IClassFactory{LONG refs_=1;public:
 IFACEMETHODIMP QueryInterface(REFIID r,void**p)override{if(!p)return E_POINTER;*p=nullptr;if(r==IID_IUnknown||r==IID_IClassFactory){*p=static_cast<IClassFactory*>(this);AddRef();return S_OK;}return E_NOINTERFACE;}
 IFACEMETHODIMP_(ULONG) AddRef()override{return InterlockedIncrement(&refs_);} IFACEMETHODIMP_(ULONG) Release()override{auto x=InterlockedDecrement(&refs_);if(!x)delete this;return x;}
 IFACEMETHODIMP CreateInstance(IUnknown*o,REFIID r,void**p)override{if(o)return CLASS_E_NOAGGREGATION;return CreateFaceUnlockProvider(r,p);} IFACEMETHODIMP LockServer(BOOL l)override{if(l)InterlockedIncrement(&g_locks);else InterlockedDecrement(&g_locks);return S_OK;}};
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){g_module=h;DisableThreadLibraryCalls(h);}return TRUE;}
extern "C" HRESULT __declspec(dllexport) DllCanUnloadNow(){return g_locks==0?S_OK:S_FALSE;}
extern "C" HRESULT __declspec(dllexport) DllGetClassObject(REFCLSID c,REFIID r,void**p){if(c!=CLSID_FaceUnlockProvider)return CLASS_E_CLASSNOTAVAILABLE;auto f=new(std::nothrow)Factory();if(!f)return E_OUTOFMEMORY;auto hr=f->QueryInterface(r,p);f->Release();return hr;}
