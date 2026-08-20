using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using FaceUnlock.Core;
using Microsoft.Win32;

namespace FaceUnlock.Service;

public sealed class NativeApiFailureException : Exception
{
    public string Api { get; }
    public string Dll { get; }

    public NativeApiFailureException(string api, string dll, Exception innerException)
        : base($"Native API binding failed: {dll}!{api}", innerException)
    {
        Api = api;
        Dll = dll;
    }
}

public sealed class WindowsShellGateSystem : IShellGateSystem
{
    private const int WtsActive = 0;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;

    private readonly Func<bool> _pairedCheck;
    private readonly string _shellPath;

    private static T NativeCall<T>(string api, string dll, Func<T> call)
    {
        try { return call(); }
        catch (EntryPointNotFoundException ex) { throw new NativeApiFailureException(api, dll, ex); }
        catch (DllNotFoundException ex) { throw new NativeApiFailureException(api, dll, ex); }
    }

    private static void NativeCall(string api, string dll, Action call) =>
        NativeCall(api, dll, () => { call(); return true; });

    public WindowsShellGateSystem(Func<bool> pairedCheck, string? shellPath = null)
    {
        _pairedCheck = pairedCheck;
        _shellPath = shellPath ?? Path.Combine(AppContext.BaseDirectory, "FaceUnlockShell.exe");
    }

    public bool IsMachinePaired
    {
        get
        {
            try { return _pairedCheck(); }
            catch { return false; }
        }
    }

    public IReadOnlyList<InteractiveGateSession> GetInteractiveSessions()
    {
        var result = new List<InteractiveGateSession>();
        var enumeration = NativeCall("WTSEnumerateSessionsW", "wtsapi32.dll", () =>
        {
            var ok = WtsEnumerateSessions(IntPtr.Zero, 0, 1, out var nativeBuffer, out var nativeCount);
            return (ok, nativeBuffer, nativeCount);
        });
        var buffer = enumeration.nativeBuffer;
        var count = enumeration.nativeCount;
        if (!enumeration.ok)
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(buffer, i * size));
                if (info.State != WtsActive || info.SessionId == 0)
                {
                    continue;
                }

                var sid = TryGetSessionSid(info.SessionId);
                if (string.IsNullOrWhiteSpace(sid))
                {
                    continue;
                }
                result.Add(new InteractiveGateSession(info.SessionId, sid, IsShellGateEnabled(sid)));
            }
        }
        finally
        {
            NativeCall("WTSFreeMemory", "wtsapi32.dll", () => WtsFreeMemory(buffer));
        }
        return result;
    }

    public IReadOnlyList<SessionProcess> GetShellProcesses(int sessionId) =>
        QueryProcesses("FaceUnlockShell.exe", sessionId, requireShellMode: true);

    public IReadOnlyList<SessionProcess> GetExplorerProcesses(int sessionId) =>
        QueryProcessesByName("explorer", sessionId);

    public bool IsProcessAlive(int processId, int sessionId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited
                && process.SessionId == sessionId
                && string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool TryLaunchShell(InteractiveGateSession session, out int processId, out string? errorMessage)
    {
        processId = 0;
        errorMessage = null;
        if (!File.Exists(_shellPath))
        {
            errorMessage = $"Shell binary missing: {_shellPath}";
            return false;
        }
        var tokenResult = NativeCall("WTSQueryUserToken", "wtsapi32.dll", () =>
        {
            var ok = WtsQueryUserToken((uint)session.SessionId, out var token);
            return (ok, token);
        });
        var userToken = tokenResult.token;
        if (!tokenResult.ok)
        {
            errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            var duplicateResult = NativeCall("DuplicateTokenEx", "advapi32.dll", () =>
            {
                var ok = DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token);
                return (ok, token);
            });
            primaryToken = duplicateResult.token;
            if (!duplicateResult.ok)
            {
                errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            var environmentResult = NativeCall("CreateEnvironmentBlock", "userenv.dll", () =>
            {
                var ok = CreateEnvironmentBlock(out var block, primaryToken, false);
                return (ok, block);
            });
            environment = environmentResult.block;
            if (!environmentResult.ok)
            {
                errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var commandLine = new StringBuilder($"\"{_shellPath}\" --shell");
            var processResult = NativeCall("CreateProcessAsUserW", "advapi32.dll", () =>
            {
                var localStartup = startup;
                var ok = CreateProcessAsUser(
                        primaryToken,
                        _shellPath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment | CreateNewProcessGroup,
                        environment,
                        Path.GetDirectoryName(_shellPath),
                        ref localStartup,
                        out var info);
                return (ok, info);
            });
            var processInfo = processResult.info;
            if (!processResult.ok)
            {
                errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            processId = unchecked((int)processInfo.ProcessId);
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(processInfo.Thread));
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(processInfo.Process));
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) NativeCall("DestroyEnvironmentBlock", "userenv.dll", () => DestroyEnvironmentBlock(environment));
            if (primaryToken != IntPtr.Zero) NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(primaryToken));
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(userToken));
        }
    }

    public bool TryTerminateProcess(SessionProcess target, string expectedProcessName, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited
                || process.SessionId != target.SessionId
                || !string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Process identity changed before termination.";
                return false;
            }
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool IsTrustedShellProcess(int processId, int sessionId)
    {
        return GetShellProcesses(sessionId).Any(process => process.ProcessId == processId);
    }

    private static IReadOnlyList<SessionProcess> QueryProcessesByName(string processName, int sessionId)
    {
        var result = new List<SessionProcess>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == sessionId)
                    {
                        result.Add(new SessionProcess(process.Id, sessionId));
                    }
                }
                catch { }
            }
        }
        return result;
    }

    private IReadOnlyList<SessionProcess> QueryProcesses(string executableName, int sessionId, bool requireShellMode)
    {
        var result = new List<SessionProcess>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT ProcessId, SessionId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='{executableName.Replace("'", "''")}'");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var itemSession = Convert.ToInt32(item["SessionId"] ?? -1);
                    if (itemSession != sessionId)
                    {
                        continue;
                    }
                    var commandLine = item["CommandLine"]?.ToString() ?? string.Empty;
                    if (requireShellMode && commandLine.Contains("--test", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (requireShellMode)
                    {
                        var executablePath = item["ExecutablePath"]?.ToString();
                        if (string.IsNullOrWhiteSpace(executablePath)
                            || !string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(_shellPath), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    result.Add(new SessionProcess(Convert.ToInt32(item["ProcessId"]), itemSession));
                }
            }
        }
        catch
        {
            // Unknown process inventory is handled as missing/locked by the watchdog.
        }
        return result;
    }

    private static string? TryGetSessionSid(int sessionId)
    {
        var tokenResult = NativeCall("WTSQueryUserToken", "wtsapi32.dll", () =>
        {
            var ok = WtsQueryUserToken((uint)sessionId, out var nativeToken);
            return (ok, nativeToken);
        });
        var token = tokenResult.nativeToken;
        if (!tokenResult.ok)
        {
            return null;
        }
        try
        {
            using var identity = new System.Security.Principal.WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(token));
        }
    }

    private static bool IsShellGateEnabled(string userSid)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey($@"{userSid}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
            var shell = key?.GetValue("Shell")?.ToString();
            return shell?.Contains("FaceUnlockShell.exe", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    public static void RunNativeApiSmokeTest()
    {
        var requiredExports = new Dictionary<string, string[]>
        {
            ["wtsapi32.dll"] = ["WTSEnumerateSessionsW", "WTSFreeMemory", "WTSQueryUserToken"],
            ["advapi32.dll"] = ["DuplicateTokenEx", "CreateProcessAsUserW"],
            ["userenv.dll"] = ["CreateEnvironmentBlock", "DestroyEnvironmentBlock"],
            ["kernel32.dll"] = ["CloseHandle"]
        };
        foreach (var pair in requiredExports)
        {
            if (!NativeLibrary.TryLoad(pair.Key, out var library))
            {
                throw new DllNotFoundException(pair.Key);
            }
            try
            {
                foreach (var api in pair.Value)
                {
                    if (!NativeLibrary.TryGetExport(library, api, out _))
                    {
                        throw new EntryPointNotFoundException($"{pair.Key}!{api}");
                    }
                }
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        var enumeration = NativeCall("WTSEnumerateSessionsW", "wtsapi32.dll", () =>
        {
            var ok = WtsEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count);
            return (ok, buffer, count);
        });
        NativeCall("WTSFreeMemory", "wtsapi32.dll", () => WtsFreeMemory(enumeration.buffer));

        var query = NativeCall("WTSQueryUserToken", "wtsapi32.dll", () =>
        {
            var ok = WtsQueryUserToken((uint)Process.GetCurrentProcess().SessionId, out var token);
            return (ok, token);
        });
        if (query.token != IntPtr.Zero)
        {
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(query.token));
        }

        var currentToken = System.Security.Principal.WindowsIdentity.GetCurrent().Token;
        var duplicate = NativeCall("DuplicateTokenEx", "advapi32.dll", () =>
        {
            var ok = DuplicateTokenEx(currentToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token);
            return (ok, token);
        });
        IntPtr environment = IntPtr.Zero;
        try
        {
            var environmentResult = NativeCall("CreateEnvironmentBlock", "userenv.dll", () =>
            {
                var ok = CreateEnvironmentBlock(out var block, duplicate.token, false);
                return (ok, block);
            });
            environment = environmentResult.block;

            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var commandLine = new StringBuilder("\"Z:\\FaceUnlock-Native-Smoke-Missing.exe\"");
            NativeCall("CreateProcessAsUserW", "advapi32.dll", () =>
            {
                var localStartup = startup;
                return CreateProcessAsUser(
                    duplicate.token, "Z:\\FaceUnlock-Native-Smoke-Missing.exe", commandLine,
                    IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment,
                    environment, null, ref localStartup, out _);
            });
        }
        finally
        {
            NativeCall("DestroyEnvironmentBlock", "userenv.dll", () => DestroyEnvironmentBlock(environment));
            if (duplicate.token != IntPtr.Zero)
            {
                NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(duplicate.token));
            }
            NativeCall("CloseHandle", "kernel32.dll", () => CloseHandle(IntPtr.Zero));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Ptr;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory", ExactSpelling = true)]
    private static extern void WtsFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQueryUserToken", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", EntryPoint = "CreateEnvironmentBlock", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", EntryPoint = "DestroyEnvironmentBlock", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public interface IShellClientAuthorizer
{
    bool IsTrustedShellClient(int clientProcessId, LocalAuthRequest request, out string? errorMessage);
}

public sealed class WindowsShellClientAuthorizer : IShellClientAuthorizer
{
    private readonly IShellGateSystem _system;

    public WindowsShellClientAuthorizer(IShellGateSystem system)
    {
        _system = system;
    }

    public bool IsTrustedShellClient(int clientProcessId, LocalAuthRequest request, out string? errorMessage)
    {
        errorMessage = null;
        if (!request.process_id.HasValue || request.process_id.Value != clientProcessId)
        {
            errorMessage = "Named-pipe client PID mismatch.";
            return false;
        }
        if (!request.session_id.HasValue || !_system.IsTrustedShellProcess(clientProcessId, request.session_id.Value))
        {
            errorMessage = "Client is not FaceUnlockShell.exe --shell in the bound session.";
            return false;
        }
        return true;
    }
}
