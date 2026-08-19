// REFERENCE ONLY - requires Microsoft's restricted secondaryAuthenticationFactor capability.
// Namespace: Windows.Security.Authentication.Identity.Provider
using Windows.Security.Authentication.Identity.Provider;
using Windows.Storage.Streams;

public static class CompanionAuthReference
{
    public static async Task StartAsync(string deviceId, IBuffer serviceAuthenticationHmac)
    {
        // Microsoft returns session/device nonces here. A companion device computes the required HMACs,
        // after which FinishAuthenticationAsync can complete Windows companion-device authentication.
        var result = await SecondaryAuthenticationFactorAuthentication.StartAuthenticationAsync(deviceId, serviceAuthenticationHmac);
        _ = result.Authentication;
        // Intentionally no fake implementation: FinishAuthenticationAsync requires protocol-correct HMACs
        // and an app provisioned for the restricted capability.
    }
}
