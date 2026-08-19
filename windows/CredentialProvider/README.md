# Credential Provider scaffold

This folder is intentionally a **safe integration scaffold**, not an authentication bypass.

A Windows Credential Provider can show an `Unlock with iPhone` tile and communicate with `FaceUnlock.Service`, but for ordinary local/Microsoft-account unlock it must ultimately submit credentials that Windows authentication packages accept. A phone signature alone is not a standard Windows password credential.

The included DLL skeleton demonstrates registration and the boundary where phone approval is consulted. Before production use, implement one of these supported deployment models:

1. Phone approval as an additional gate, then submit the user's normal Windows credential through standard serialization; or
2. Microsoft-approved Windows Hello Companion Device capability (see `../CompanionCDF`), if you can obtain provisioning; or
3. An enterprise-supported identity solution designed for your account type.

Do not filter out built-in PIN/password providers.
