# Windows Hello Companion Device Framework reference

Microsoft documented this framework for scenarios such as a phone paired over Bluetooth receiving an approval gesture and then unlocking the PC. It uses `Windows.Security.Authentication.Identity.Provider.SecondaryAuthenticationFactor*` APIs.

However, the API requires the restricted `secondaryAuthenticationFactor` capability and Microsoft documentation says calls fail unless the developer account is specially provisioned. The companion device framework is also deprecated.

`CompanionAuthReference.cs` is therefore reference code only. It is not included in the default build.
