using System;
using System.Runtime.InteropServices;

internal sealed class GlobalWinDWatcher : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkD = 0x44;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int KeyDownMask = 0x8000;

    private readonly LowLevelKeyboardProc hookProc;
    private IntPtr hookHandle;
    private bool winDPressed;

    public GlobalWinDWatcher()
    {
        this.hookProc = HookCallback;
    }

    public event EventHandler WinDPressed;

    public bool IsStarted
    {
        get { return this.hookHandle != IntPtr.Zero; }
    }

    public bool Start(out int errorCode)
    {
        errorCode = 0;
        if (this.hookHandle != IntPtr.Zero)
        {
            return true;
        }

        this.hookHandle = SetWindowsHookEx(WhKeyboardLl, this.hookProc, GetModuleHandle(null), 0);
        if (this.hookHandle != IntPtr.Zero)
        {
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    public void Stop()
    {
        if (this.hookHandle == IntPtr.Zero)
        {
            return;
        }

        IntPtr handle = this.hookHandle;
        this.hookHandle = IntPtr.Zero;
        this.winDPressed = false;
        UnhookWindowsHookEx(handle);
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            KeyboardHookStruct key = (KeyboardHookStruct)Marshal.PtrToStructure(lParam, typeof(KeyboardHookStruct));
            bool keyDown = message == WmKeyDown || message == WmSysKeyDown;
            bool keyUp = message == WmKeyUp || message == WmSysKeyUp;
            if (key.vkCode == VkD)
            {
                if (IsWinDGesture(key.vkCode, keyDown, IsWindowsKeyDown()))
                {
                    if (!this.winDPressed)
                    {
                        this.winDPressed = true;
                        EventHandler handler = this.WinDPressed;
                        if (handler != null)
                        {
                            handler(this, EventArgs.Empty);
                        }
                    }
                }
                else if (keyUp)
                {
                    this.winDPressed = false;
                }
            }
            else if (keyUp && (key.vkCode == VkLWin || key.vkCode == VkRWin))
            {
                this.winDPressed = false;
            }
        }

        return CallNextHookEx(this.hookHandle, code, wParam, lParam);
    }

    internal static bool IsWinDGesture(int virtualKey, bool keyDown, bool windowsKeyDown)
    {
        return virtualKey == VkD && keyDown && windowsKeyDown;
    }

    internal static void RunGestureSelfTest()
    {
        if (!IsWinDGesture(VkD, true, true) ||
            IsWinDGesture(VkD, true, false) ||
            IsWinDGesture(VkD, false, true) ||
            IsWinDGesture(0x43, true, true))
        {
            throw new InvalidOperationException("Win+D gesture policy failed.");
        }
    }

    private static bool IsWindowsKeyDown()
    {
        return (GetAsyncKeyState(VkLWin) & KeyDownMask) != 0 ||
            (GetAsyncKeyState(VkRWin) & KeyDownMask) != 0;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr extraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc hookProc, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
