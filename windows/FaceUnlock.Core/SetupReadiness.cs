namespace FaceUnlock.Core;

public enum FaceUnlockServiceHealth
{
    Missing,
    Stopped,
    Unhealthy,
    Healthy
}

public sealed record SetupReadinessResult(bool IsReady, string ServiceLabel, string Message, bool CanEnableShellGate);

public static class SetupReadiness
{
    public static SetupReadinessResult Evaluate(bool paired, string pairingReason, FaceUnlockServiceHealth service, bool shellEnabled)
    {
        var serviceLabel = service switch
        {
            FaceUnlockServiceHealth.Healthy => "Running",
            FaceUnlockServiceHealth.Missing => "Missing",
            FaceUnlockServiceHealth.Stopped => "Stopped",
            _ => "Unhealthy"
        };
        var ready = paired && service == FaceUnlockServiceHealth.Healthy && shellEnabled;
        var message = ready
            ? "FaceUnlock is ready."
            : !paired
                ? $"Pair your iPhone to finish FaceUnlock setup. ({pairingReason})"
                : service switch
                {
                    FaceUnlockServiceHealth.Missing => "FaceUnlock Service is not installed.",
                    FaceUnlockServiceHealth.Stopped => "FaceUnlock Service is stopped.",
                    FaceUnlockServiceHealth.Unhealthy => "FaceUnlock Service is unhealthy.",
                    _ => "Enable FaceUnlock on Windows startup?"
                };
        return new SetupReadinessResult(ready, serviceLabel, message, paired && service == FaceUnlockServiceHealth.Healthy && !shellEnabled);
    }
}
