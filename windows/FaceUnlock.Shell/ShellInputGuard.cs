using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaceUnlock.Shell;

public interface IShellInputGuard : IDisposable
{
    bool IsActive { get; }
    bool TryInstall(out string? errorMessage);
    bool TryUninstall(out string? errorMessage);
}

public static class ShellInputPolicy
{
    public const int VkTab = 0x09;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkF4 = 0x73;
    public const int VkF10 = 0x79;
    public const int VkLeftWindows = 0x5B;
    public const int VkRightWindows = 0x5C;

    public static bool ShouldBlock(int virtualKey, bool altDown, bool controlDown, bool shiftDown, bool windowsDown)
    {
        // Blocking both Windows modifier events prevents Start and all Win+ shortcuts,
        // including R/E/D/M/Shift+M/Tab/L/X/A/S/I/U/P/K.
        if (virtualKey is VkLeftWindows or VkRightWindows || windowsDown)
        {
            return true;
        }

        if (altDown && virtualKey is VkTab or VkEscape or VkF4 or VkSpace)
        {
            return true;
        }

        // This covers Ctrl+Esc and Ctrl+Shift+Esc. The latter reaches Task Manager
        // without SAS and can be blocked by a low-level user-mode hook.
        // Ctrl+Alt+Del is SAS and never reaches this hook.
        if (controlDown && shiftDown && virtualKey == VkEscape)
        {
            return true;
        }

        if (controlDown && virtualKey == VkEscape)
        {
            return true;
        }

        // Prevent keyboard activation of a system menu even if window styles regress.
        return virtualKey == VkF10;
    }
}

public sealed class ShellInputGuard : IShellInputGuard
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;

    private readonly object _sync = new();
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;
    private bool _disposed;
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _genericControlDown;
    private bool _leftControlDown;
    private bool _rightControlDown;
    private bool _genericShiftDown;
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _genericAltDown;
    private bool _leftAltDown;
    private bool _rightAltDown;

    private bool ControlDown => _genericControlDown || _leftControlDown || _rightControlDown;
    private bool ShiftDown => _genericShiftDown || _leftShiftDown || _rightShiftDown;
    private bool AltDown => _genericAltDown || _leftAltDown || _rightAltDown;

    public ShellInputGuard()
    {
        _callback = HookCallback;
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _hookHandle != IntPtr.Zero;
            }
        }
    }

    public bool TryInstall(out string? errorMessage)
    {
        lock (_sync)
        {
            errorMessage = null;
            if (_disposed)
            {
                errorMessage = "Input guard has already been disposed.";
                return false;
            }
            if (_hookHandle != IntPtr.Zero)
            {
                return true;
            }

            try
            {
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                var moduleHandle = module == null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
                _hookHandle = SetWindowsHookEx(WhKeyboardLl, _callback, moduleHandle, 0);
                if (_hookHandle == IntPtr.Zero)
                {
                    errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }

                SeedModifierState();
            }
            catch (Exception ex)
            {
                _hookHandle = IntPtr.Zero;
                ResetModifierState();
                errorMessage = ex.Message;
                return false;
            }

            return true;
        }
    }

    public bool TryUninstall(out string? errorMessage)
    {
        lock (_sync)
        {
            errorMessage = null;
            if (_hookHandle == IntPtr.Zero)
            {
                return true;
            }

            if (!UnhookWindowsHookEx(_hookHandle))
            {
                errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            _hookHandle = IntPtr.Zero;
            ResetModifierState();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_hookHandle != IntPtr.Zero && UnhookWindowsHookEx(_hookHandle))
            {
                _hookHandle = IntPtr.Zero;
            }
            ResetModifierState();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsKeyboardMessage(wParam))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var key = unchecked((int)data.VirtualKeyCode);
            var isKeyDown = IsKeyDownMessage(wParam);

            // LowLevelKeyboardProc runs before the asynchronous key state is updated,
            // so track swallowed modifier events ourselves instead of trusting only
            // GetAsyncKeyState from inside this callback.
            if (isKeyDown)
            {
                UpdateModifierState(key, true);
            }

            var altDown = AltDown || (data.Flags & 0x20) != 0;
            var windowsDown = _leftWindowsDown || _rightWindowsDown;
            var block = ShellInputPolicy.ShouldBlock(key, altDown, ControlDown, ShiftDown, windowsDown);

            if (!isKeyDown)
            {
                UpdateModifierState(key, false);
            }

            if (block)
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private static bool IsKeyboardMessage(IntPtr message)
    {
        var value = unchecked((int)message.ToInt64());
        return value is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp;
    }

    private static bool IsKeyDownMessage(IntPtr message)
    {
        var value = unchecked((int)message.ToInt64());
        return value is WmKeyDown or WmSysKeyDown;
    }

    private void UpdateModifierState(int virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case ShellInputPolicy.VkLeftWindows:
                _leftWindowsDown = isDown;
                break;
            case ShellInputPolicy.VkRightWindows:
                _rightWindowsDown = isDown;
                break;
            case VkControl:
                _genericControlDown = isDown;
                break;
            case VkLeftControl:
                _leftControlDown = isDown;
                break;
            case VkRightControl:
                _rightControlDown = isDown;
                break;
            case VkShift:
                _genericShiftDown = isDown;
                break;
            case VkLeftShift:
                _leftShiftDown = isDown;
                break;
            case VkRightShift:
                _rightShiftDown = isDown;
                break;
            case VkMenu:
                _genericAltDown = isDown;
                break;
            case VkLeftMenu:
                _leftAltDown = isDown;
                break;
            case VkRightMenu:
                _rightAltDown = isDown;
                break;
        }
    }

    private void SeedModifierState()
    {
        _leftWindowsDown = IsDown(ShellInputPolicy.VkLeftWindows);
        _rightWindowsDown = IsDown(ShellInputPolicy.VkRightWindows);
        _genericControlDown = IsDown(VkControl);
        _leftControlDown = IsDown(VkLeftControl);
        _rightControlDown = IsDown(VkRightControl);
        _genericShiftDown = IsDown(VkShift);
        _leftShiftDown = IsDown(VkLeftShift);
        _rightShiftDown = IsDown(VkRightShift);
        _genericAltDown = IsDown(VkMenu);
        _leftAltDown = IsDown(VkLeftMenu);
        _rightAltDown = IsDown(VkRightMenu);
    }

    private void ResetModifierState()
    {
        _leftWindowsDown = false;
        _rightWindowsDown = false;
        _genericControlDown = false;
        _leftControlDown = false;
        _rightControlDown = false;
        _genericShiftDown = false;
        _leftShiftDown = false;
        _rightShiftDown = false;
        _genericAltDown = false;
        _leftAltDown = false;
        _rightAltDown = false;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
