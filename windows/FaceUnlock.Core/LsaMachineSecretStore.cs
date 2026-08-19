using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FaceUnlock.Core;

public enum LsaSecretStatus
{
    Loaded,
    Created,
    Invalid,
    AccessDenied,
    Error
}

public sealed class LsaMachineSecretStore
{
    public const string DefaultEntropyString = "FaceUnlock-LSA-Secret-v1";
    public static readonly byte[] Entropy = Encoding.UTF8.GetBytes(DefaultEntropyString);

    public string SecretFilePath { get; }

    public LsaMachineSecretStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            SecretFilePath = filePath;
        }
        else
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                programData = @"C:\ProgramData";
            }
            SecretFilePath = Path.Combine(programData, "FaceUnlock", "lsa_secret.dpapi");
        }
    }

    /// <summary>
    /// Loads the existing 32-byte machine secret or generates and persists a new one.
    /// Does not log secret value.
    /// </summary>
    public (byte[]? Secret, LsaSecretStatus Status, string? ErrorMessage) LoadOrCreate()
    {
        try
        {
            var dir = Path.GetDirectoryName(SecretFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(SecretFilePath))
            {
                try
                {
                    var encryptedBytes = File.ReadAllBytes(SecretFilePath);
                    if (encryptedBytes.Length == 0 || encryptedBytes.Length > 65536)
                    {
                        return (null, LsaSecretStatus.Invalid, $"Invalid encrypted secret file size: {encryptedBytes.Length} bytes");
                    }

                    var raw = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.LocalMachine);
                    if (raw.Length != 32)
                    {
                        CryptographicOperations.ZeroMemory(raw);
                        return (null, LsaSecretStatus.Invalid, $"Decrypted secret length must be exactly 32 bytes, got {raw.Length}");
                    }

                    return (raw, LsaSecretStatus.Loaded, null);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return (null, LsaSecretStatus.AccessDenied, ex.Message);
                }
                catch (CryptographicException ex)
                {
                    // Fail closed if corrupted - do not silently overwrite an existing key that may be in use
                    return (null, LsaSecretStatus.Invalid, $"DPAPI unprotect failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    return (null, LsaSecretStatus.Error, ex.Message);
                }
            }

            // File does not exist: generate a cryptographically strong 32-byte random key
            var newSecret = new byte[32];
            RandomNumberGenerator.Fill(newSecret);

            try
            {
                var encrypted = ProtectedData.Protect(newSecret, Entropy, DataProtectionScope.LocalMachine);

                // Atomic write via temp file
                var tempPath = SecretFilePath + $".tmp.{Guid.NewGuid():N}";
                File.WriteAllBytes(tempPath, encrypted);

                // Apply strict ACL: SYSTEM FullControl, Builtin Administrators FullControl
                ApplySystemAdminOnlyAcl(tempPath);

                if (File.Exists(SecretFilePath))
                {
                    File.Delete(SecretFilePath);
                }
                File.Move(tempPath, SecretFilePath);

                // Ensure final file also has the ACL applied
                ApplySystemAdminOnlyAcl(SecretFilePath);

                return (newSecret, LsaSecretStatus.Created, null);
            }
            catch (Exception ex)
            {
                CryptographicOperations.ZeroMemory(newSecret);
                return (null, LsaSecretStatus.Error, $"Failed to persist LSA machine secret: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return (null, LsaSecretStatus.Error, ex.Message);
        }
    }

    public static void ApplySystemAdminOnlyAcl(string filePath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
                return;

            var fileInfo = new FileInfo(filePath);
            var fileSecurity = new FileSecurity();

            // Disable inheritance and remove inherited rules
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));

            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(fileSecurity);
        }
        catch
        {
            // Ignore ACL failure if running without admin token in test environments
        }
    }
}
