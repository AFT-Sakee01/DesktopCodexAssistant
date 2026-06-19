using System;
using System.Runtime.InteropServices;

internal sealed class GlobalCtrlDWatcher : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkD = 0x44;
    private const int VkControl = 0x11;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int KeyDownMask = 0x8000;

    private readonly LowLevelKeyboardProc hookProc;
    private IntPtr hookHandle;
    private bool ctrlDPressed;

    public GlobalCtrlDWatcher()
    {
        this.hookProc = HookCallback;
    }

    public event EventHandler CtrlDPressed;

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
        this.ctrlDPressed = false;
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
                if (keyDown && IsControlKeyDown())
                {
                    if (!this.ctrlDPressed)
                    {
                        this.ctrlDPressed = true;
                        EventHandler handler = this.CtrlDPressed;
                        if (handler != null)
                        {
                            handler(this, EventArgs.Empty);
                        }
                    }
                }
                else if (keyUp)
                {
                    this.ctrlDPressed = false;
                }
            }
            else if (keyUp && (key.vkCode == VkControl || key.vkCode == VkLControl || key.vkCode == VkRControl))
            {
                this.ctrlDPressed = false;
            }
        }

        return CallNextHookEx(this.hookHandle, code, wParam, lParam);
    }

    private static bool IsControlKeyDown()
    {
        return (GetAsyncKeyState(VkControl) & KeyDownMask) != 0 ||
            (GetAsyncKeyState(VkLControl) & KeyDownMask) != 0 ||
            (GetAsyncKeyState(VkRControl) & KeyDownMask) != 0;
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
