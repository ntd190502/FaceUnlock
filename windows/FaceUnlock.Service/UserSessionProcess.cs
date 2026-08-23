using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaceUnlock.Service;

internal static class UserSessionProcess
{
    const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    const uint TOKEN_DUPLICATE = 0x0002;
    const uint TOKEN_QUERY = 0x0008;
    const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const int SecurityImpersonation = 2;
    const int TokenPrimary = 1;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    const uint LOGON_WITH_PROFILE = 0x00000001;

    public static bool StartInActiveSession(string exePath, out string? error)
    {
        error = null;
        if (!File.Exists(exePath)) { error = "AlertUI executable is missing: " + exePath; return false; }
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)).Any()) return true;

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) { error = "No active console user session"; return false; }
        if (!WTSQueryUserToken(sessionId, out var userToken)) { error = "WTSQueryUserToken failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }

        IntPtr primaryToken = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                error = "DuplicateTokenEx failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            CreateEnvironmentBlock(out environment, primaryToken, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = $"\"{exePath}\"";
            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP;
            var ok = CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment, Path.GetDirectoryName(exePath), ref si, out var pi);
            if (!ok)
            {
                ok = CreateProcessWithTokenW(primaryToken, LOGON_WITH_PROFILE, exePath, commandLine, CREATE_UNICODE_ENVIRONMENT, environment, Path.GetDirectoryName(exePath), ref si, out pi);
            }
            if (!ok)
            {
                error = "Could not start AlertUI in user session: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags, string lpApplicationName, string lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);
}
