using System.Security.Cryptography;
namespace FaceUnlock.Core;
public static class Protocol
{
    public static string Canonical(string sessionId,string challenge,string pcId,long expiresAt)=>$"faceunlock-v1|{sessionId}|{challenge}|{pcId}|{expiresAt}";
    public static string OfflineRequestCanonical(string sessionId,string challenge,string pcId,long expiresAt)=>$"faceunlock-offline-request-v1|{sessionId}|{challenge}|{pcId}|{expiresAt}";
    public static string OfflineRequestCanonical(string sessionId,string challenge,string pcId,long expiresAt,string logicalRequestId,string? onlineSessionId)=>
        $"faceunlock-offline-request-v2|{sessionId}|{challenge}|{pcId}|{expiresAt}|{logicalRequestId}|{onlineSessionId ?? string.Empty}";
    public static string RandomToken(int bytes=32)=>Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+','-').Replace('/','_');
}
