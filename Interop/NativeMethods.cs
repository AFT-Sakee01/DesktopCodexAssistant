using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class NativeMethods
{
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    public const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    public const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    public const int WM_APP = 0x8000;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMECRITICAL = 0x0006;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int ABN_POSCHANGED = 0x00000001;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_PARENTCHANGE = 0x800F;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    public static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new Guid("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");
    public static readonly Guid GUID_POWERSCHEME_PERSONALITY = new Guid("245d8541-3943-4422-b025-13a784f679b7");

    private const uint WM_SPAWN_WORKER = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const string LiveCaptionsAppsFolderPath = @"shell:AppsFolder\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\LiveCaptions.exe";
    private const string WindowsAiStudioProtocol = "ms-clicktodo";
    private const string WindowsAiStudioAppsFolderPath = @"shell:AppsFolder\MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy!ClickToDoApp";
    private const string WindowsAiStudioPackagePrefix = "MicrosoftWindows.Client.CoreAI_";
    private const string WindowsAiStudioPackageSuffix = "_cw5n1h2txyewy";
    private const uint GW_OWNER = 4;
    private const uint WM_APPCOMMAND = 0x0319;
    private const uint WM_CLOSE = 0x0010;
    private const uint BM_CLICK = 0x00F5;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_XBUTTON1 = 0x05;
    private const int VK_XBUTTON2 = 0x06;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const byte VK_SPACE = 0x20;
    private const byte VK_A = 0x41;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_RWIN = 0x5C;
    private const byte VK_D = 0x44;
    private const byte VK_X = 0x58;
    private const byte VK_U = 0x55;
    private const byte VK_DELETE = 0x2E;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;
    private const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01C4;
    private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const int SHIL_LARGE = 0;
    private const int SHIL_EXTRALARGE = 2;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 0x00000001;
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_ICONONLY = 0x00000004;
    private const int MAX_PATH = 260;
    private const int INFOTIPSIZE = 1024;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int OBJID_WINDOW = 0;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint ABM_WINDOWPOSCHANGED = 0x00000009;
    private const uint ABE_TOP = 1;
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IME_CMODE_NATIVE = 0x0001;
    private const int IME_CMODE_KATAKANA = 0x0002;
    private const int MaxAutomationScanNodes = 500;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WindowEventHandler(uint eventId, IntPtr windowHandle);
    public delegate void EffectivePowerModeCallback(int mode, IntPtr context);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data;
    }

    private delegate void WinEventProc(
        IntPtr hookHandle,
        uint eventId,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    private static readonly object inputIndicatorCacheLock = new object();
    private static DateTime inputIndicatorCacheUtc;
    private static string inputIndicatorCacheLabel;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder imageFileName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr moduleHandle,
        WinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO guiThreadInfo);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerRegisterForEffectivePowerModeNotifications(
        uint version,
        EffectivePowerModeCallback callback,
        IntPtr context,
        out IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registrationHandle);

    public static IntPtr RegisterConsoleDisplayStateNotification(IntPtr windowHandle)
    {
        Guid guid = GUID_CONSOLE_DISPLAY_STATE;
        return RegisterPowerSettingNotification(windowHandle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public static IntPtr RegisterPowerSettingNotificationForWindow(IntPtr windowHandle, Guid powerSettingGuid)
    {
        Guid guid = powerSettingGuid;
        return RegisterPowerSettingNotification(windowHandle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public static bool UnregisterPowerNotification(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        return UnregisterPowerSettingNotification(handle);
    }

    public static bool TryRegisterEffectivePowerModeNotification(
        EffectivePowerModeCallback callback,
        out IntPtr registrationHandle)
    {
        registrationHandle = IntPtr.Zero;
        if (callback == null)
        {
            return false;
        }

        try
        {
            // Version 2 reports newer effective modes. Older Windows builds may only accept v1.
            uint result = PowerRegisterForEffectivePowerModeNotifications(
                2,
                callback,
                IntPtr.Zero,
                out registrationHandle);
            if (result != 0 || registrationHandle == IntPtr.Zero)
            {
                if (registrationHandle != IntPtr.Zero)
                {
                    PowerUnregisterFromEffectivePowerModeNotifications(registrationHandle);
                }

                registrationHandle = IntPtr.Zero;
                result = PowerRegisterForEffectivePowerModeNotifications(
                    1,
                    callback,
                    IntPtr.Zero,
                    out registrationHandle);
            }

            return result == 0 && registrationHandle != IntPtr.Zero;
        }
        catch
        {
            registrationHandle = IntPtr.Zero;
            return false;
        }
    }

    public static bool UnregisterEffectivePowerModeNotification(IntPtr registrationHandle)
    {
        if (registrationHandle == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return PowerUnregisterFromEffectivePowerModeNotifications(registrationHandle) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetVolume(IntPtr waveOutHandle, out uint volume);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr inputContext);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetOpenStatus(IntPtr inputContext);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr inputContext, out int conversionMode, out int sentenceMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destinationWindow, IntPtr sourceWindow, out IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnailId, out SIZE size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnailId, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr dot11Ssid,
        int dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
        IntPtr reserved,
        out IntPtr wlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(
        string section,
        string key,
        string defaultValue,
        StringBuilder returnedString,
        uint size,
        string fileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] iconHandles,
        int[] iconIds,
        uint iconCount,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint icons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref SHFILEINFO fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(
        int imageList,
        ref Guid interfaceId,
        out IImageList imageListInterface);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string parsingName,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IShellItemImageFactory imageFactory);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;

        public SIZE(int cx, int cy)
        {
            this.CX = cx;
            this.CY = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    public sealed class ApplicationWindowInfo
    {
        public IntPtr Handle { get; set; }
        public int ProcessId { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;

        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bOneXEnabled;

        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;

        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_RATE_SET
    {
        public uint uRateSetLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] usRateSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid;
        public uint uPhyId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;

        public int dot11BssType;
        public int dot11BssPhyType;
        public int lRssi;
        public uint uLinkQuality;
        public byte bInRegDomain;
        public ushort usBeaconPeriod;
        public ulong ullTimestamp;
        public ulong ullHostTimestamp;
        public ushort usCapabilityInformation;
        public uint ulChCenterFrequency;
        public WLAN_RATE_SET wlanRateSet;
        public uint ulIeOffset;
        public uint ulIeSize;
    }

    private enum AudioDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum AudioRole
    {
        Console,
        Multimedia,
        Communications
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(AudioDataFlow dataFlow, uint deviceState, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, AudioRole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfaceObject);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channelNumber, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channelNumber, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float volumeMinDb, out float volumeMaxDb, out float volumeIncrementDb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr imageBitmap, IntPtr maskBitmap, ref int index);

        [PreserveSig]
        int ReplaceIcon(int index, IntPtr icon, ref int newIndex);

        [PreserveSig]
        int SetOverlayImage(int imageIndex, int overlayIndex);

        [PreserveSig]
        int Replace(int index, IntPtr imageBitmap, IntPtr maskBitmap);

        [PreserveSig]
        int AddMasked(IntPtr imageBitmap, int maskColor, ref int index);

        [PreserveSig]
        int Draw(IntPtr drawParameters);

        [PreserveSig]
        int Remove(int index);

        [PreserveSig]
        int GetIcon(int index, int flags, out IntPtr icon);
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxFile,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr idList);

        [PreserveSig]
        int SetIDList(IntPtr idList);

        [PreserveSig]
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxDirectory);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxArguments);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out short hotkey);

        [PreserveSig]
        int SetHotkey(short hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr ownerHandle, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public sealed class ShellLinkInfo
    {
        public string TargetPath { get; set; }
        public string Arguments { get; set; }
        public string IconPath { get; set; }
        public int IconIndex { get; set; }
    }

    public sealed class WindowEventHook : IDisposable
    {
        private readonly List<IntPtr> hookHandles;
        private readonly WinEventProc callback;
        private readonly WindowEventHandler handler;
        private bool disposed;

        internal WindowEventHook(WindowEventHandler handler)
        {
            this.handler = handler;
            this.hookHandles = new List<IntPtr>();
            this.callback = OnWinEvent;
            AddHook(EVENT_SYSTEM_FOREGROUND);
            AddHook(EVENT_OBJECT_CREATE);
            AddHook(EVENT_OBJECT_DESTROY);
            AddHook(EVENT_OBJECT_SHOW);
            AddHook(EVENT_OBJECT_HIDE);
            AddHook(EVENT_OBJECT_STATECHANGE);
            AddHook(EVENT_OBJECT_NAMECHANGE);
            AddHook(EVENT_OBJECT_PARENTCHANGE);
            AddHook(EVENT_OBJECT_CLOAKED);
            AddHook(EVENT_OBJECT_UNCLOAKED);
        }

        public bool IsActive
        {
            get { return this.hookHandles.Count > 0; }
        }

        private void AddHook(uint eventId)
        {
            IntPtr handle = SetWinEventHook(
                eventId,
                eventId,
                IntPtr.Zero,
                this.callback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (handle != IntPtr.Zero)
            {
                this.hookHandles.Add(handle);
            }
        }

        private void OnWinEvent(
            IntPtr hookHandle,
            uint eventId,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (this.disposed ||
                objectId != OBJID_WINDOW ||
                childId != 0 ||
                windowHandle == IntPtr.Zero ||
                this.handler == null)
            {
                return;
            }

            try
            {
                this.handler(eventId, windowHandle);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            for (int i = 0; i < this.hookHandles.Count; i++)
            {
                if (this.hookHandles[i] != IntPtr.Zero)
                {
                    try
                    {
                        UnhookWinEvent(this.hookHandles[i]);
                    }
                    catch
                    {
                    }
                }
            }

            this.hookHandles.Clear();
        }
    }

    public static bool TrySetProcessPowerThrottling(bool enabled)
    {
        const int processPowerThrottling = 4;
        const uint processPowerThrottlingCurrentVersion = 1;
        const uint processPowerThrottlingExecutionSpeed = 0x1;

        try
        {
            PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE();
            state.Version = processPowerThrottlingCurrentVersion;
            state.ControlMask = processPowerThrottlingExecutionSpeed;
            state.StateMask = enabled ? processPowerThrottlingExecutionSpeed : 0;
            return SetProcessInformation(
                GetCurrentProcess(),
                processPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterTopAppBar(IntPtr handle, int callbackMessage)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        APPBARDATA data = CreateAppBarData(handle);
        data.uCallbackMessage = (uint)callbackMessage;
        return SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
    }

    public static void RemoveAppBar(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        APPBARDATA data = CreateAppBarData(handle);
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    public static Rectangle SetTopAppBarPosition(IntPtr handle, Rectangle screenBounds, int height)
    {
        if (handle == IntPtr.Zero)
        {
            return Rectangle.Empty;
        }

        height = Math.Max(1, height);
        APPBARDATA data = CreateAppBarData(handle);
        data.uEdge = ABE_TOP;
        data.rc.Left = screenBounds.Left;
        data.rc.Top = screenBounds.Top;
        data.rc.Right = screenBounds.Right;
        data.rc.Bottom = screenBounds.Top + height;

        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + height;
        SHAppBarMessage(ABM_SETPOS, ref data);
        return Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
    }

    public static void NotifyAppBarWindowPositionChanged(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        APPBARDATA data = CreateAppBarData(handle);
        SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
    }

    private static APPBARDATA CreateAppBarData(IntPtr handle)
    {
        APPBARDATA data = new APPBARDATA();
        data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        data.hWnd = handle;
        return data;
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr handle, Point location, Bitmap bitmap)
    {
        return UpdateLayeredWindowFromBitmap(handle, location, bitmap, 255);
    }

    internal sealed class LayeredBitmapSurface : IDisposable
    {
        private IntPtr memoryDc;
        private IntPtr bitmapHandle;
        private IntPtr originalBitmap;
        private int bitmapWidth;
        private int bitmapHeight;
        private bool disposed;

        public bool Update(IntPtr handle, Point location, Bitmap bitmap, byte sourceAlpha, bool refreshBitmap)
        {
            if (this.disposed || handle == IntPtr.Zero || bitmap == null)
            {
                return false;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (this.memoryDc == IntPtr.Zero)
                {
                    this.memoryDc = CreateCompatibleDC(screenDc);
                    if (this.memoryDc == IntPtr.Zero)
                    {
                        return false;
                    }
                }

                if (refreshBitmap ||
                    this.bitmapHandle == IntPtr.Zero ||
                    this.bitmapWidth != bitmap.Width ||
                    this.bitmapHeight != bitmap.Height)
                {
                    if (!ReplaceBitmap(bitmap))
                    {
                        return false;
                    }
                }

                POINT destination = new POINT(location.X, location.Y);
                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                POINT source = new POINT(0, 0);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = sourceAlpha;
                blend.AlphaFormat = AC_SRC_ALPHA;

                return UpdateLayeredWindow(
                    handle,
                    screenDc,
                    ref destination,
                    ref size,
                    this.memoryDc,
                    ref source,
                    0,
                    ref blend,
                    ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void Reset()
        {
            if (this.disposed)
            {
                return;
            }

            ReleaseNativeResources();
        }

        private bool ReplaceBitmap(Bitmap bitmap)
        {
            if (this.bitmapHandle != IntPtr.Zero)
            {
                if (this.originalBitmap != IntPtr.Zero)
                {
                    SelectObject(this.memoryDc, this.originalBitmap);
                }

                DeleteObject(this.bitmapHandle);
                this.bitmapHandle = IntPtr.Zero;
            }

            IntPtr nextBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            if (nextBitmap == IntPtr.Zero)
            {
                return false;
            }

            IntPtr previousBitmap = SelectObject(this.memoryDc, nextBitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
            {
                DeleteObject(nextBitmap);
                return false;
            }

            if (this.originalBitmap == IntPtr.Zero)
            {
                this.originalBitmap = previousBitmap;
            }

            this.bitmapHandle = nextBitmap;
            this.bitmapWidth = bitmap.Width;
            this.bitmapHeight = bitmap.Height;
            return true;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            ReleaseNativeResources();
        }

        private void ReleaseNativeResources()
        {
            if (this.memoryDc != IntPtr.Zero && this.originalBitmap != IntPtr.Zero)
            {
                SelectObject(this.memoryDc, this.originalBitmap);
            }

            this.originalBitmap = IntPtr.Zero;
            if (this.bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(this.bitmapHandle);
                this.bitmapHandle = IntPtr.Zero;
            }

            if (this.memoryDc != IntPtr.Zero)
            {
                DeleteDC(this.memoryDc);
                this.memoryDc = IntPtr.Zero;
            }

            this.bitmapWidth = 0;
            this.bitmapHeight = 0;
        }
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr handle, Point location, Bitmap bitmap, byte sourceAlpha)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, bitmapHandle);

            POINT destination = new POINT(location.X, location.Y);
            SIZE size = new SIZE(bitmap.Width, bitmap.Height);
            POINT source = new POINT(0, 0);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = sourceAlpha;
            blend.AlphaFormat = AC_SRC_ALPHA;

            return UpdateLayeredWindow(
                handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                ULW_ALPHA);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    public static bool TryGetConnectedWifiDetails(Guid interfaceGuid, out WifiConnectionDetails details)
    {
        const uint wlanClientVersion = 2;
        const uint success = 0;
        const int wlanOpcodeCurrentConnection = 7;

        details = new WifiConnectionDetails();
        IntPtr clientHandle = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;
        try
        {
            uint negotiatedVersion;
            uint status = WlanOpenHandle(wlanClientVersion, IntPtr.Zero, out negotiatedVersion, out clientHandle);
            if (status != success || clientHandle == IntPtr.Zero)
            {
                return false;
            }

            int dataSize;
            int opcodeValueType;
            status = WlanQueryInterface(
                clientHandle,
                ref interfaceGuid,
                wlanOpcodeCurrentConnection,
                IntPtr.Zero,
                out dataSize,
                out data,
                out opcodeValueType);
            if (status != success || data == IntPtr.Zero || dataSize <= 0)
            {
                return false;
            }

            WLAN_CONNECTION_ATTRIBUTES attributes =
                (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(data, typeof(WLAN_CONNECTION_ATTRIBUTES));
            WLAN_ASSOCIATION_ATTRIBUTES association = attributes.wlanAssociationAttributes;
            WLAN_SECURITY_ATTRIBUTES security = attributes.wlanSecurityAttributes;
            details.Ssid = DecodeSsid(association.dot11Ssid);
            if (string.IsNullOrEmpty(details.Ssid) && !string.IsNullOrEmpty(attributes.strProfileName))
            {
                details.Ssid = attributes.strProfileName.Trim();
            }

            details.Bssid = FormatBssid(association.dot11Bssid);
            details.PhyType = FormatDot11PhyType(association.dot11PhyType);
            details.AuthAlgorithm = FormatDot11AuthAlgorithm(security.dot11AuthAlgorithm);
            details.CipherAlgorithm = FormatDot11CipherAlgorithm(security.dot11CipherAlgorithm);
            details.SecurityEnabled = security.bSecurityEnabled;
            details.OneXEnabled = security.bOneXEnabled;
            details.SignalQuality = Math.Max(0, Math.Min(100, (int)association.wlanSignalQuality));
            details.TxRateKbps = association.ulTxRate;
            details.RxRateKbps = association.ulRxRate;
            int rssiDbm;
            if (TryGetConnectedWifiRssi(clientHandle, interfaceGuid, details.Bssid, out rssiDbm))
            {
                details.RssiKnown = true;
                details.RssiDbm = rssiDbm;
            }

            return true;
        }
        catch
        {
            details = new WifiConnectionDetails();
            return false;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }

            if (clientHandle != IntPtr.Zero)
            {
                WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }

    public static string TryGetConnectedWifiSsid(Guid interfaceGuid)
    {
        WifiConnectionDetails details;
        if (!TryGetConnectedWifiDetails(interfaceGuid, out details))
        {
            return string.Empty;
        }

        return details == null ? string.Empty : details.Ssid;
    }

    public static bool TryGetConnectedWifiSignalQuality(Guid interfaceGuid, out int quality)
    {
        quality = 0;
        WifiConnectionDetails details;
        if (!TryGetConnectedWifiDetails(interfaceGuid, out details) || details == null)
        {
            return false;
        }

        quality = details.SignalQuality;
        return true;
    }

    private static bool TryGetConnectedWifiRssi(IntPtr clientHandle, Guid interfaceGuid, string connectedBssid, out int rssiDbm)
    {
        const uint success = 0;
        const int dot11BssTypeAny = 3;
        const int maxReasonableBssEntries = 4096;

        rssiDbm = 0;
        if (clientHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(connectedBssid))
        {
            return false;
        }

        IntPtr bssList = IntPtr.Zero;
        try
        {
            Guid queryInterfaceGuid = interfaceGuid;
            uint status = WlanGetNetworkBssList(
                clientHandle,
                ref queryInterfaceGuid,
                IntPtr.Zero,
                dot11BssTypeAny,
                false,
                IntPtr.Zero,
                out bssList);
            if (status != success || bssList == IntPtr.Zero)
            {
                return false;
            }

            int count = Marshal.ReadInt32(bssList, 4);
            if (count <= 0 || count > maxReasonableBssEntries)
            {
                return false;
            }

            int entrySize = Marshal.SizeOf(typeof(WLAN_BSS_ENTRY));
            IntPtr entryPtr = new IntPtr(bssList.ToInt64() + 8);
            for (int i = 0; i < count; i++)
            {
                WLAN_BSS_ENTRY entry = (WLAN_BSS_ENTRY)Marshal.PtrToStructure(entryPtr, typeof(WLAN_BSS_ENTRY));
                if (string.Equals(FormatBssid(entry.dot11Bssid), connectedBssid, StringComparison.OrdinalIgnoreCase))
                {
                    rssiDbm = entry.lRssi;
                    return true;
                }

                entryPtr = new IntPtr(entryPtr.ToInt64() + entrySize);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bssList != IntPtr.Zero)
            {
                WlanFreeMemory(bssList);
            }
        }

        return false;
    }

    private static string DecodeSsid(DOT11_SSID ssid)
    {
        if (ssid.ucSSID == null || ssid.uSSIDLength == 0)
        {
            return string.Empty;
        }

        int length = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
        string text = Encoding.UTF8.GetString(ssid.ucSSID, 0, length);
        return text.Trim(new char[] { '\0' });
    }

    private static string FormatBssid(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 6)
        {
            return string.Empty;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3],
            bytes[4],
            bytes[5]);
    }

    private static string FormatDot11PhyType(int value)
    {
        switch (value)
        {
            case 1:
                return "FHSS";
            case 2:
                return "DSSS";
            case 3:
                return "IR";
            case 4:
                return "OFDM";
            case 5:
                return "HRDSSS";
            case 6:
                return "ERP";
            case 7:
                return "HT";
            case 8:
                return "VHT";
            case 9:
                return "DMG";
            case 10:
                return "HE";
            case 11:
                return "EHT";
            default:
                return "Unknown";
        }
    }

    private static string FormatDot11AuthAlgorithm(int value)
    {
        switch (value)
        {
            case 1:
                return "Open";
            case 2:
                return "Shared";
            case 3:
                return "WPA";
            case 4:
                return "WPA-PSK";
            case 5:
                return "WPA-None";
            case 6:
                return "WPA2";
            case 7:
                return "WPA2-PSK";
            case 8:
                return "WPA3";
            case 9:
                return "WPA3-SAE";
            case 10:
                return "OWE";
            case 11:
                return "WPA3-192";
            case 12:
                return "WPA3-ENT";
            default:
                return "Unknown";
        }
    }

    private static string FormatDot11CipherAlgorithm(int value)
    {
        switch (value)
        {
            case 0:
                return "None";
            case 1:
                return "WEP40";
            case 2:
                return "TKIP";
            case 4:
                return "CCMP";
            case 5:
                return "WEP104";
            case 6:
                return "BIP";
            case 8:
                return "GCMP";
            case 9:
                return "GCMP-256";
            case 10:
                return "CCMP-256";
            case 11:
                return "BIP-GMAC-128";
            case 12:
                return "BIP-GMAC-256";
            case 13:
                return "BIP-CMAC-256";
            case 257:
                return "WEP";
            default:
                return "Unknown";
        }
    }

    public static string TryReadIniValue(string fileName, string section, string key)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            StringBuilder builder = new StringBuilder(4096);
            uint length = GetPrivateProfileString(section, key, string.Empty, builder, (uint)builder.Capacity, fileName);
            if (length == 0)
            {
                return string.Empty;
            }

            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryResolveShortcut(string fileName, out ShellLinkInfo linkInfo)
    {
        linkInfo = null;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        object linkObject = null;
        try
        {
            linkObject = new ShellLink();
            System.Runtime.InteropServices.ComTypes.IPersistFile persistFile =
                (System.Runtime.InteropServices.ComTypes.IPersistFile)linkObject;
            persistFile.Load(fileName, 0);

            IShellLinkW shellLink = (IShellLinkW)linkObject;
            StringBuilder target = new StringBuilder(MAX_PATH);
            StringBuilder arguments = new StringBuilder(INFOTIPSIZE);
            StringBuilder iconPath = new StringBuilder(MAX_PATH);
            int iconIndex;

            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            shellLink.GetArguments(arguments, arguments.Capacity);
            shellLink.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex);

            linkInfo = new ShellLinkInfo
            {
                TargetPath = target.ToString().Trim(),
                Arguments = arguments.ToString().Trim(),
                IconPath = iconPath.ToString().Trim(),
                IconIndex = iconIndex
            };
            return !string.IsNullOrEmpty(linkInfo.TargetPath) || !string.IsNullOrEmpty(linkInfo.IconPath);
        }
        catch
        {
            linkInfo = null;
            return false;
        }
        finally
        {
            if (linkObject != null && Marshal.IsComObject(linkObject))
            {
                try
                {
                    Marshal.FinalReleaseComObject(linkObject);
                }
                catch
                {
                }
            }
        }
    }

    public static Bitmap TryExtractIconBitmap(string fileName, int iconIndex)
    {
        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            return null;
        }

        Bitmap bitmap = TryExtractPrivateIconBitmap(fileName, iconIndex);
        if (bitmap != null)
        {
            return bitmap;
        }

        return TryExtractAssociatedIconBitmap(fileName, iconIndex);
    }

    public static Bitmap TryLoadShellItemBitmap(string parsingName)
    {
        if (string.IsNullOrEmpty(parsingName))
        {
            return null;
        }

        IShellItemImageFactory imageFactory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            Guid factoryGuid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
            int hresult = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref factoryGuid, out imageFactory);
            if (hresult != 0 || imageFactory == null)
            {
                return null;
            }

            SIZE size = new SIZE(256, 256);
            hresult = imageFactory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out bitmapHandle);
            if (hresult != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            using (Bitmap bitmap = Image.FromHbitmap(bitmapHandle))
            {
                return new Bitmap(bitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (imageFactory != null && Marshal.IsComObject(imageFactory))
            {
                try
                {
                    Marshal.ReleaseComObject(imageFactory);
                }
                catch
                {
                }
            }
        }
    }

    public static Bitmap TryLoadShellIconBitmap(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            return null;
        }

        try
        {
            SHFILEINFO fileInfo = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                fileName,
                0,
                ref fileInfo,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero || fileInfo.iIcon < 0)
            {
                return null;
            }

            int[] imageLists = new int[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE };
            for (int i = 0; i < imageLists.Length; i++)
            {
                Bitmap bitmap = TryGetShellImageListBitmap(fileInfo.iIcon, imageLists[i]);
                if (bitmap != null)
                {
                    return bitmap;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static Bitmap TryExtractPrivateIconBitmap(string fileName, int iconIndex)
    {
        IntPtr[] icons = new IntPtr[1];
        int[] iconIds = new int[1];
        try
        {
            uint extracted = PrivateExtractIcons(fileName, iconIndex, 256, 256, icons, iconIds, 1, 0);
            if (extracted == 0 || extracted == uint.MaxValue || icons[0] == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(icons[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
            {
                DestroyIcon(icons[0]);
            }
        }
    }

    private static Bitmap TryExtractAssociatedIconBitmap(string fileName, int iconIndex)
    {
        IntPtr[] icons = new IntPtr[1];
        try
        {
            uint extracted = ExtractIconEx(fileName, iconIndex, icons, null, 1);
            if (extracted == 0 || icons[0] == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(icons[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
            {
                DestroyIcon(icons[0]);
            }
        }
    }

    private static Bitmap TryGetShellImageListBitmap(int iconIndex, int imageListId)
    {
        IImageList imageList = null;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            Guid imageListGuid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            int hresult = SHGetImageList(imageListId, ref imageListGuid, out imageList);
            if (hresult != 0 || imageList == null)
            {
                return null;
            }

            if (imageList.GetIcon(iconIndex, ILD_TRANSPARENT, out iconHandle) != 0 || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(iconHandle);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }

            if (imageList != null && Marshal.IsComObject(imageList))
            {
                try
                {
                    Marshal.ReleaseComObject(imageList);
                }
                catch
                {
                }
            }
        }
    }

    private static Bitmap BitmapFromIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero)
        {
            return null;
        }

        using (Icon icon = (Icon)Icon.FromHandle(iconHandle).Clone())
        {
            return icon.ToBitmap();
        }
    }

    public static void TrySetDpiAware()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch
        {
        }
    }

    public static void AttachToParentConsole()
    {
        try
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch
        {
        }
    }

    public static List<ApplicationWindowInfo> EnumerateApplicationWindows(IntPtr ownHandle)
    {
        List<ApplicationWindowInfo> windows = new List<ApplicationWindowInfo>();
        int ownProcessId = 0;
        try
        {
            ownProcessId = Process.GetCurrentProcess().Id;
        }
        catch
        {
        }

        EnumWindows(delegate(IntPtr handle, IntPtr lParam)
        {
            if (handle == IntPtr.Zero || handle == ownHandle)
            {
                return true;
            }

            if (!IsWindowVisible(handle))
            {
                return true;
            }

            if (GetWindow(handle, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            string className = GetWindowClassName(handle);
            if (IsShellOrUtilityWindowClass(className))
            {
                return true;
            }

            uint processIdValue;
            GetWindowThreadProcessId(handle, out processIdValue);
            int processId = processIdValue > int.MaxValue ? 0 : (int)processIdValue;
            if (string.Equals(className, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
            {
                int hostedProcessId;
                if (TryGetHostedApplicationProcessId(handle, processId, out hostedProcessId))
                {
                    processId = hostedProcessId;
                }
                else
                {
                    return true;
                }
            }

            if (processId <= 0 || processId == ownProcessId)
            {
                return true;
            }

            if (IsUtilityWindowProcess(processId))
            {
                return true;
            }

            RECT rect;
            if (!GetWindowRect(handle, out rect) ||
                rect.Right - rect.Left < 32 ||
                rect.Bottom - rect.Top < 32)
            {
                return true;
            }

            string title = GetWindowTitle(handle);
            if (string.IsNullOrEmpty(title))
            {
                return true;
            }

            windows.Add(new ApplicationWindowInfo
            {
                Handle = handle,
                ProcessId = processId,
                Title = title,
                ClassName = className
            });
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool TryGetHostedApplicationProcessId(IntPtr frameHandle, int frameProcessId, out int hostedProcessId)
    {
        hostedProcessId = 0;
        int foundProcessId = 0;
        try
        {
            EnumChildWindows(frameHandle, delegate(IntPtr childHandle, IntPtr lParam)
            {
                uint childProcessIdValue;
                GetWindowThreadProcessId(childHandle, out childProcessIdValue);
                int childProcessId = childProcessIdValue > int.MaxValue ? 0 : (int)childProcessIdValue;
                if (childProcessId > 0 && childProcessId != frameProcessId)
                {
                    foundProcessId = childProcessId;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            foundProcessId = 0;
        }

        hostedProcessId = foundProcessId;
        return hostedProcessId > 0;
    }

    private static bool IsUtilityWindowProcess(int processId)
    {
        if (processId <= 0)
        {
            return true;
        }

        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                string name = process.ProcessName ?? string.Empty;
                return string.Equals(name, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "Widgets", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "ClickToDo", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsIconic(handle))
            {
                ShowWindow(handle, SW_RESTORE);
            }
            else
            {
                ShowWindow(handle, SW_SHOW);
            }

            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    public static void SendMediaCommand(IntPtr handle, int command)
    {
        try
        {
            SendMessage(handle, WM_APPCOMMAND, handle, new IntPtr(command << 16));
        }
        catch
        {
        }
    }

    public static bool IsApplicationWindowVisible(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!IsWindowVisible(handle))
            {
                return false;
            }

            RECT rect;
            return GetWindowRect(handle, out rect) &&
                rect.Right > rect.Left &&
                rect.Bottom > rect.Top;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterDwmThumbnail(IntPtr destinationWindow, IntPtr sourceWindow, out IntPtr thumbnailId)
    {
        thumbnailId = IntPtr.Zero;
        if (destinationWindow == IntPtr.Zero || sourceWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return DwmRegisterThumbnail(destinationWindow, sourceWindow, out thumbnailId) == 0 &&
                thumbnailId != IntPtr.Zero;
        }
        catch
        {
            thumbnailId = IntPtr.Zero;
            return false;
        }
    }

    public static void UnregisterDwmThumbnail(IntPtr thumbnailId)
    {
        if (thumbnailId == IntPtr.Zero)
        {
            return;
        }

        try
        {
            DwmUnregisterThumbnail(thumbnailId);
        }
        catch
        {
        }
    }

    public static Size QueryThumbnailSourceSize(IntPtr thumbnailId)
    {
        if (thumbnailId == IntPtr.Zero)
        {
            return Size.Empty;
        }

        try
        {
            SIZE size;
            if (DwmQueryThumbnailSourceSize(thumbnailId, out size) == 0)
            {
                return new Size(Math.Max(0, size.CX), Math.Max(0, size.CY));
            }
        }
        catch
        {
        }

        return Size.Empty;
    }

    public static bool UpdateDwmThumbnail(IntPtr thumbnailId, Rectangle destination, byte opacity)
    {
        if (thumbnailId == IntPtr.Zero || destination.Width <= 0 || destination.Height <= 0)
        {
            return false;
        }

        try
        {
            DWM_THUMBNAIL_PROPERTIES properties = new DWM_THUMBNAIL_PROPERTIES();
            properties.dwFlags =
                DWM_TNP_RECTDESTINATION |
                DWM_TNP_OPACITY |
                DWM_TNP_VISIBLE |
                DWM_TNP_SOURCECLIENTAREAONLY;
            properties.rcDestination = ToRect(destination);
            properties.opacity = opacity;
            properties.fVisible = true;
            properties.fSourceClientAreaOnly = false;
            return DwmUpdateThumbnailProperties(thumbnailId, ref properties) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestCloseWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    public static string TryGetProcessImagePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            StringBuilder path = new StringBuilder(32768);
            int size = path.Capacity;
            if (!QueryFullProcessImageName(processHandle, 0, path, ref size) || size <= 0)
            {
                return string.Empty;
            }

            return path.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(processHandle);
                }
                catch
                {
                }
            }
        }
    }

    public static bool ToggleDesktop()
    {
        return TryInvokeShellApplicationMethod("ToggleDesktop");
    }

    public static bool OpenStartMenu()
    {
        return OpenWindowsStartMenu();
    }

    public static bool OpenWindowsStartMenu()
    {
        if (TryInvokeWindowsStartButton(false))
        {
            return true;
        }

        return TryClickHiddenWindowsStartButton();
    }

    public static bool OpenWindowsStartContextMenu()
    {
        if (TryInvokeWindowsStartButton(true))
        {
            return true;
        }

        return OpenWindowsPowerUserMenu();
    }

    public static bool OpenStartContextMenu()
    {
        return OpenWindowsStartContextMenu();
    }

    public static void OpenQuickSettings()
    {
        SendWinKeyChord(VK_A);
    }

    public static bool OpenLiveCaptions()
    {
        if (StartShellProcess("explorer.exe", LiveCaptionsAppsFolderPath))
        {
            return true;
        }

        string systemPath = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");
        if (File.Exists(systemPath) && StartShellProcess(systemPath, null))
        {
            return true;
        }

        return StartShellProcess("LiveCaptions.exe", null);
    }

    public static bool IsLiveCaptionsAvailable()
    {
        string systemPath = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");
        if (File.Exists(systemPath))
        {
            return true;
        }

        string aliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\LiveCaptions.exe");
        return File.Exists(aliasPath);
    }

    public static void OpenHiddenTrayIcons()
    {
        if (TryOpenHiddenTrayIconsByAutomation())
        {
            return;
        }

        Program.LogInfo("Hidden tray icons button was not found through UI Automation.");
    }

    public static bool OpenInputSwitcher()
    {
        return OpenShellUri("ms-inputapp:");
    }

    public static bool OpenActionCenterControl(string controlName)
    {
        if (string.IsNullOrEmpty(controlName))
        {
            return false;
        }

        return OpenShellUri("ms-actioncenter:controlcenter/" + controlName);
    }

    public static bool OpenAvailableNetworks()
    {
        return OpenShellUri("ms-availablenetworks:");
    }

    public static bool OpenWindowsSettings()
    {
        return OpenShellUri("ms-settings:");
    }

    public static bool OpenTaskManager()
    {
        return StartShellProcess("taskmgr.exe", null);
    }

    public static bool OpenWindowsAiStudio()
    {
        if (OpenShellUri(WindowsAiStudioProtocol + ":"))
        {
            return true;
        }

        return StartShellProcess("explorer.exe", WindowsAiStudioAppsFolderPath);
    }

    public static bool IsWindowsAiStudioAvailable()
    {
        return IsShellProtocolRegistered(WindowsAiStudioProtocol) ||
            IsAppPackageRegistered(WindowsAiStudioPackagePrefix, WindowsAiStudioPackageSuffix);
    }

    public static bool OpenWindowsSystemPowerMenu()
    {
        if (TryOpenWindowsPowerUserShutdownMenu())
        {
            return true;
        }

        return OpenSettingsPage("powersleep");
    }

    public static bool OpenWindowsSecurityMenu()
    {
        return TryInvokeShellApplicationMethod("WindowsSecurity");
    }

    public static bool OpenSettingsPage(string pageName)
    {
        if (string.IsNullOrEmpty(pageName))
        {
            return false;
        }

        return OpenShellUri("ms-settings:" + pageName);
    }

    private static bool OpenShellUri(string uri)
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = uri;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartShellProcess(string fileName, string arguments)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.UseShellExecute = true;
            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool OpenWindowsPowerUserMenu()
    {
        try
        {
            SendWinKeyChord(VK_X);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsShellProtocolRegistered(string protocolName)
    {
        if (string.IsNullOrEmpty(protocolName))
        {
            return false;
        }

        string normalized = protocolName.TrimEnd(':');
        return RegistryKeyExists(Registry.CurrentUser, @"Software\Classes\" + normalized) ||
            RegistryKeyExists(Registry.LocalMachine, @"SOFTWARE\Classes\" + normalized) ||
            RegistryKeyExists(Registry.ClassesRoot, normalized);
    }

    private static bool IsAppPackageRegistered(string packagePrefix, string packageSuffix)
    {
        if (string.IsNullOrEmpty(packagePrefix) || string.IsNullOrEmpty(packageSuffix))
        {
            return false;
        }

        return RegistryContainsSubKeyName(
                Registry.CurrentUser,
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                packagePrefix,
                packageSuffix) ||
            RegistryContainsSubKeyName(
                Registry.CurrentUser,
                @"Software\Classes\ActivatableClasses\Package",
                packagePrefix,
                packageSuffix) ||
            RegistryContainsSubKeyName(
                Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications",
                packagePrefix,
                packageSuffix) ||
            RegistryContainsSubKeyName(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications",
                packagePrefix,
                packageSuffix);
    }

    private static bool RegistryKeyExists(RegistryKey root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                return key != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool RegistryContainsSubKeyName(RegistryKey root, string path, string prefix, string suffix)
    {
        if (root == null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null)
                {
                    return false;
                }

                string[] names = key.GetSubKeyNames();
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i];
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryInvokeShellApplicationMethod(string methodName)
    {
        object shell = null;
        try
        {
            if (string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return false;
            }

            shellType.InvokeMember(
                methodName,
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                null);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogInfo("Shell.Application " + methodName + " failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
            {
                try
                {
                    Marshal.FinalReleaseComObject(shell);
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryOpenWindowsPowerUserShutdownMenu()
    {
        try
        {
            SendWinKeyChord(VK_X);
            Thread.Sleep(90);
            SendSingleKey(VK_U);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SendWinKeyChord(byte virtualKey)
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    private static void SendSingleKey(byte virtualKey)
    {
        try
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    public static IntPtr GetForegroundWindowHandle()
    {
        try
        {
            return GetForegroundWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static bool TryGetWindowProcessId(IntPtr handle, out int processId)
    {
        processId = 0;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint processIdValue;
            GetWindowThreadProcessId(handle, out processIdValue);
            if (processIdValue == 0 || processIdValue > int.MaxValue)
            {
                return false;
            }

            processId = (int)processIdValue;
            return true;
        }
        catch
        {
            processId = 0;
            return false;
        }
    }

    public static string GetWindowTitleForDisplay(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            string className = GetWindowClassName(handle);
            if (IsShellOrUtilityWindowClass(className))
            {
                return string.Empty;
            }

            return GetWindowTitle(handle);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string GetForegroundInputLanguageLabel()
    {
        string taskbarLabel;
        if (TryGetTaskbarInputIndicatorLabel(out taskbarLabel))
        {
            string normalizedTaskbarLabel;
            if (TryNormalizeTaskbarInputLabel(taskbarLabel, out normalizedTaskbarLabel))
            {
                return normalizedTaskbarLabel;
            }

            return taskbarLabel;
        }

        try
        {
            CultureInfo culture;
            IntPtr focusedWindow;
            if (TryGetForegroundInputCulture(out culture, out focusedWindow))
            {
                if (string.Equals(culture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase))
                {
                    int conversionMode;
                    if (TryGetDefaultImeWindowConversionMode(focusedWindow, out conversionMode) ||
                        TryGetImeConversionMode(focusedWindow, out conversionMode))
                    {
                        return FormatJapaneseInputCulture(GetJapaneseModeFromConversionMode(conversionMode));
                    }
                }

                bool? nativeMode = TryGetDefaultImeWindowNativeMode(focusedWindow);
                if (!nativeMode.HasValue)
                {
                    nativeMode = TryGetImeNativeMode(focusedWindow);
                }

                return FormatInputCulture(culture, nativeMode);
            }
        }
        catch
        {
        }

        try
        {
            string tag = Windows.Globalization.Language.CurrentInputMethodLanguageTag;
            if (!string.IsNullOrEmpty(tag))
            {
                return FormatInputCulture(CultureInfo.GetCultureInfoByIetfLanguageTag(tag), null);
            }
        }
        catch
        {
        }

        try
        {
            InputLanguage language = InputLanguage.CurrentInputLanguage;
            if (language != null && language.Culture != null)
            {
                return FormatInputCulture(language.Culture, null);
            }
        }
        catch
        {
        }

        return "IME";
    }

    private static bool TryGetForegroundInputCulture(out CultureInfo culture, out IntPtr focusedWindow)
    {
        culture = null;
        focusedWindow = IntPtr.Zero;
        try
        {
            IntPtr foreground = GetForegroundWindow();
            uint processId;
            uint threadId = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out processId);
            focusedWindow = GetFocusedWindowForThread(threadId, foreground);
            uint focusedProcessId;
            uint inputThreadId = focusedWindow == IntPtr.Zero ? threadId : GetWindowThreadProcessId(focusedWindow, out focusedProcessId);
            IntPtr layout = GetKeyboardLayout(inputThreadId);
            int lcid = unchecked((int)((long)layout & 0xFFFF));
            if (lcid <= 0)
            {
                return false;
            }

            culture = CultureInfo.GetCultureInfo(lcid);
            return culture != null;
        }
        catch
        {
            culture = null;
            focusedWindow = IntPtr.Zero;
            return false;
        }
    }

    private static bool TryNormalizeTaskbarInputLabel(string label, out string normalizedLabel)
    {
        normalizedLabel = label;
        string text = (label ?? string.Empty).Trim();
        if (!IsPlainImeModeLabel(text))
        {
            return false;
        }

        CultureInfo culture;
        IntPtr focusedWindow;
        if (!TryGetForegroundInputCulture(out culture, out focusedWindow) || culture == null)
        {
            return false;
        }

        if (string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
        {
            normalizedLabel = FormatChineseInputCulture(culture, !IsPlainEnglishInputLabel(text));
            return true;
        }

        if (string.Equals(culture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase))
        {
            normalizedLabel = FormatJapaneseInputCulture(GetJapaneseModeFromCompactLabel(text));
            return true;
        }

        return false;
    }

    private static bool IsPlainImeModeLabel(string text)
    {
        return string.Equals(text, "中", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "日", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "あ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "ア", StringComparison.OrdinalIgnoreCase) ||
               IsPlainEnglishInputLabel(text);
    }

    private static bool IsPlainEnglishInputLabel(string text)
    {
        return string.Equals(text, "英", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "ENG", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "A", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetTaskbarInputIndicatorLabel(out string label)
    {
        label = string.Empty;
        lock (inputIndicatorCacheLock)
        {
            if (!string.IsNullOrEmpty(inputIndicatorCacheLabel) &&
                (DateTime.UtcNow - inputIndicatorCacheUtc).TotalMilliseconds < 250.0)
            {
                label = inputIndicatorCacheLabel;
                return true;
            }
        }

        try
        {
            AutomationElement root = AutomationElement.RootElement;
            if (root == null)
            {
                return false;
            }

            AutomationElementCollection windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < windows.Count; i++)
            {
                AutomationElement window = windows[i];
                string className = SafeAutomationProperty(window, AutomationElement.ClassNameProperty);
                if (!string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int visited = 0;
                if (TryFindInputIndicatorLabel(window, 0, ref visited, out label))
                {
                    lock (inputIndicatorCacheLock)
                    {
                        inputIndicatorCacheLabel = label;
                        inputIndicatorCacheUtc = DateTime.UtcNow;
                    }

                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryFindInputIndicatorLabel(AutomationElement element, int depth, ref int visited, out string label)
    {
        label = string.Empty;
        if (element == null || depth > 9 || visited > MaxAutomationScanNodes)
        {
            return false;
        }

        visited++;
        try
        {
            string name = SafeAutomationProperty(element, AutomationElement.NameProperty);
            string className = SafeAutomationProperty(element, AutomationElement.ClassNameProperty);
            string automationId = SafeAutomationProperty(element, AutomationElement.AutomationIdProperty);
            if (IsInputIndicatorCandidate(name, className, automationId) &&
                TryMapInputIndicatorNameToLabel(name, out label))
            {
                return true;
            }

            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement child = walker.GetFirstChild(element);
            while (child != null && visited <= MaxAutomationScanNodes)
            {
                if (TryFindInputIndicatorLabel(child, depth + 1, ref visited, out label))
                {
                    return true;
                }

                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryOpenHiddenTrayIconsByAutomation()
    {
        try
        {
            AutomationElement root = AutomationElement.RootElement;
            if (root == null)
            {
                return false;
            }

            AutomationElementCollection windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < windows.Count; i++)
            {
                AutomationElement window = windows[i];
                string className = SafeAutomationProperty(window, AutomationElement.ClassNameProperty);
                if (!string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int visited = 0;
                if (TryInvokeHiddenTrayButton(window, 0, ref visited))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryInvokeWindowsStartButton(bool rightClick)
    {
        try
        {
            AutomationElement root = AutomationElement.RootElement;
            if (root == null)
            {
                return false;
            }

            AutomationElementCollection windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < windows.Count; i++)
            {
                AutomationElement window = windows[i];
                string className = SafeAutomationProperty(window, AutomationElement.ClassNameProperty);
                if (!string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int visited = 0;
                AutomationElement startButton;
                if (TryFindWindowsStartButton(window, 0, ref visited, out startButton))
                {
                    return rightClick
                        ? TryClickAutomationElement(startButton, true)
                        : TryInvokeAutomationElement(startButton);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryClickHiddenWindowsStartButton()
    {
        IntPtr startButton = FindWindowsStartButtonChild("Shell_TrayWnd");
        if (startButton == IntPtr.Zero)
        {
            startButton = FindWindowsStartButtonChild("Shell_SecondaryTrayWnd");
        }

        if (startButton == IntPtr.Zero)
        {
            return false;
        }

        // SeelenUI hides Shell_TrayWnd from UI Automation, but the native child
        // Start button can still accept BM_CLICK. This avoids launching
        // StartMenuExperienceHost as a standalone ApplicationFrameWindow.
        IntPtr parentShell = FindWindow("Shell_TrayWnd", null);
        if (parentShell != IntPtr.Zero)
        {
            SetForegroundWindow(parentShell);
        }

        SendMessage(startButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static IntPtr FindWindowsStartButtonChild(string shellClassName)
    {
        if (string.IsNullOrEmpty(shellClassName))
        {
            return IntPtr.Zero;
        }

        IntPtr shellWindow = FindWindow(shellClassName, null);
        if (shellWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr found = IntPtr.Zero;
        EnumChildWindows(shellWindow, delegate(IntPtr childHandle, IntPtr lParam)
        {
            string className = GetWindowClassName(childHandle);
            if (string.Equals(className, "Start", StringComparison.OrdinalIgnoreCase))
            {
                found = childHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static bool TryFindWindowsStartButton(AutomationElement element, int depth, ref int visited, out AutomationElement startButton)
    {
        startButton = null;
        if (element == null || depth > 12 || visited > MaxAutomationScanNodes)
        {
            return false;
        }

        visited++;
        try
        {
            string name = SafeAutomationProperty(element, AutomationElement.NameProperty);
            string className = SafeAutomationProperty(element, AutomationElement.ClassNameProperty);
            string automationId = SafeAutomationProperty(element, AutomationElement.AutomationIdProperty);
            ControlType controlType = SafeAutomationControlType(element);
            if (IsWindowsStartButtonCandidate(name, className, automationId, controlType))
            {
                startButton = element;
                return true;
            }

            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement child = walker.GetFirstChild(element);
            while (child != null && visited <= MaxAutomationScanNodes)
            {
                if (TryFindWindowsStartButton(child, depth + 1, ref visited, out startButton))
                {
                    return true;
                }

                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsWindowsStartButtonCandidate(string name, string className, string automationId, ControlType controlType)
    {
        bool likelyButton = controlType == null ||
            controlType == ControlType.Button ||
            controlType == ControlType.Custom;
        if (!likelyButton)
        {
            return false;
        }

        string combined = ((name ?? string.Empty) + " " + (className ?? string.Empty) + " " + (automationId ?? string.Empty)).ToLowerInvariant();
        string trimmed = combined.Trim();
        return combined.IndexOf("startbutton", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("start button", StringComparison.Ordinal) >= 0 ||
               string.Equals(trimmed, "start", StringComparison.Ordinal) ||
               combined.StartsWith("start ", StringComparison.Ordinal) ||
               combined.IndexOf(" start ", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("开始", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("開始", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("スタート", StringComparison.Ordinal) >= 0;
    }

    private static bool TryInvokeHiddenTrayButton(AutomationElement element, int depth, ref int visited)
    {
        if (element == null || depth > 10 || visited > MaxAutomationScanNodes)
        {
            return false;
        }

        visited++;
        try
        {
            string name = SafeAutomationProperty(element, AutomationElement.NameProperty);
            string className = SafeAutomationProperty(element, AutomationElement.ClassNameProperty);
            string automationId = SafeAutomationProperty(element, AutomationElement.AutomationIdProperty);
            ControlType controlType = SafeAutomationControlType(element);
            if (IsHiddenTrayButtonCandidate(name, className, automationId, controlType) &&
                TryInvokeOrClickAutomationElement(element))
            {
                return true;
            }

            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement child = walker.GetFirstChild(element);
            while (child != null && visited <= MaxAutomationScanNodes)
            {
                if (TryInvokeHiddenTrayButton(child, depth + 1, ref visited))
                {
                    return true;
                }

                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsHiddenTrayButtonCandidate(string name, string className, string automationId, ControlType controlType)
    {
        string combined = ((name ?? string.Empty) + " " + (className ?? string.Empty) + " " + (automationId ?? string.Empty)).ToLowerInvariant();
        bool likelyButton = controlType == null ||
            controlType == ControlType.Button ||
            controlType == ControlType.Custom;
        if (!likelyButton)
        {
            return false;
        }

        return combined.IndexOf("show hidden icons", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("hidden icons", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("notification overflow", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("system tray overflow", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("tray overflow", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("overflow chevron", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("chevron", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("隐藏的图标", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("顯示隱藏", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("隱藏的圖示", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("隠れている", StringComparison.Ordinal) >= 0 ||
               combined.IndexOf("非表示", StringComparison.Ordinal) >= 0;
    }

    private static ControlType SafeAutomationControlType(AutomationElement element)
    {
        try
        {
            object value = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
            return value == null || value == AutomationElement.NotSupported ? null : value as ControlType;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryInvokeOrClickAutomationElement(AutomationElement element)
    {
        if (TryInvokeAutomationElement(element))
        {
            return true;
        }

        return TryClickAutomationElement(element, false);
    }

    private static bool TryInvokeAutomationElement(AutomationElement element)
    {
        try
        {
            object pattern;
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) && pattern is InvokePattern)
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryClickAutomationElement(AutomationElement element, bool rightClick)
    {
        try
        {
            System.Windows.Point point;
            if (element.TryGetClickablePoint(out point))
            {
                return ClickScreenPoint(point, rightClick);
            }

            System.Windows.Rect bounds = element.Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Width > 1.0 && bounds.Height > 1.0)
            {
                return ClickScreenPoint(new System.Windows.Point(bounds.Left + bounds.Width / 2.0, bounds.Top + bounds.Height / 2.0), rightClick);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool ClickScreenPoint(System.Windows.Point point, bool rightClick)
    {
        try
        {
            int x = (int)Math.Round(point.X);
            int y = (int)Math.Round(point.Y);
            if (!SetCursorPos(x, y))
            {
                return false;
            }

            if (rightClick)
            {
                mouse_event(MOUSEEVENTF_RIGHTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
            }
            else
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeAutomationProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            object value = element.GetCurrentPropertyValue(property, true);
            return value == null || value == AutomationElement.NotSupported ? string.Empty : value.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsInputIndicatorCandidate(string name, string className, string automationId)
    {
        string combined = ((name ?? string.Empty) + " " + (className ?? string.Empty) + " " + (automationId ?? string.Empty)).ToLowerInvariant();
        if (combined.IndexOf("input indicator", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("input method", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("language", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("keyboard layout", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("ime", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("输入", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("輸入", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("语言", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("語言", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("键盘", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("鍵盤", StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        string trimmed = (name ?? string.Empty).Trim();
        return IsCompactInputIndicatorText(trimmed);
    }

    private static bool IsCompactInputIndicatorText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 10)
        {
            return false;
        }

        return string.Equals(text, "中", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "英", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "ENG", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "A", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "日", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "あ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "ア", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "한", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapInputIndicatorNameToLabel(string rawName, out string label)
    {
        label = string.Empty;
        string name = (rawName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return false;
        }

        string lower = name.ToLowerInvariant();
        string chineseVariant = GetChineseVariantLabel(name);
        bool japaneseIndicator = IsJapaneseIndicatorName(name);
        if (lower.IndexOf("english mode", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("alphanumeric", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("direct input", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("英文模式", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("英數", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("英数", StringComparison.Ordinal) >= 0)
        {
            label = japaneseIndicator ? FormatJapaneseInputCulture(GetJapaneseModeFromIndicatorName(name)) :
                (string.IsNullOrEmpty(chineseVariant) ? "ENG" : chineseVariant + "（ENG）");
            return true;
        }

        if (string.Equals(name, "英", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ENG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "A", StringComparison.OrdinalIgnoreCase))
        {
            label = "ENG";
            return true;
        }

        if (lower.IndexOf("chinese mode", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("chinese", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("中文", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("中国", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("中國", StringComparison.Ordinal) >= 0)
        {
            label = (string.IsNullOrEmpty(chineseVariant) ? "简体" : chineseVariant) + "（中）";
            return true;
        }

        if (string.Equals(name, "中", StringComparison.OrdinalIgnoreCase))
        {
            label = "中";
            return true;
        }

        if (japaneseIndicator)
        {
            label = FormatJapaneseInputCulture(GetJapaneseModeFromIndicatorName(name));
            return true;
        }

        if (lower.IndexOf("korean", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hangul", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("한국", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("한글", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("韓", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("韩", StringComparison.Ordinal) >= 0 ||
            string.Equals(name, "한", StringComparison.OrdinalIgnoreCase))
        {
            label = "한";
            return true;
        }

        if (lower.IndexOf("english", StringComparison.Ordinal) >= 0)
        {
            label = "ENG";
            return true;
        }

        return false;
    }

    private static bool IsJapaneseIndicatorName(string rawName)
    {
        string name = rawName ?? string.Empty;
        string lower = name.ToLowerInvariant();
        return lower.IndexOf("japanese", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("hiragana", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("katakana", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("romaji", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("日本", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("ひらがな", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("カタカナ", StringComparison.Ordinal) >= 0 ||
               lower.IndexOf("ローマ字", StringComparison.Ordinal) >= 0 ||
               string.Equals(name.Trim(), "日", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name.Trim(), "あ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name.Trim(), "ア", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetJapaneseModeFromCompactLabel(string rawName)
    {
        string name = (rawName ?? string.Empty).Trim();
        if (IsPlainEnglishInputLabel(name))
        {
            return "A";
        }

        if (string.Equals(name, "あ", StringComparison.OrdinalIgnoreCase))
        {
            return "あ";
        }

        if (string.Equals(name, "ア", StringComparison.OrdinalIgnoreCase))
        {
            return "ア";
        }

        return "日";
    }

    private static string GetJapaneseModeFromIndicatorName(string rawName)
    {
        string name = (rawName ?? string.Empty).Trim();
        string lower = name.ToLowerInvariant();
        if (IsPlainEnglishInputLabel(name) ||
            lower.IndexOf("direct input", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("alphanumeric", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("romaji", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("英数", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("英數", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("ローマ字", StringComparison.Ordinal) >= 0)
        {
            return "A";
        }

        if (string.Equals(name, "あ", StringComparison.OrdinalIgnoreCase) ||
            lower.IndexOf("hiragana", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("ひらがな", StringComparison.Ordinal) >= 0)
        {
            return "あ";
        }

        if (string.Equals(name, "ア", StringComparison.OrdinalIgnoreCase) ||
            lower.IndexOf("katakana", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("カタカナ", StringComparison.Ordinal) >= 0)
        {
            return "ア";
        }

        return "日";
    }

    private static string GetChineseVariantLabel(string rawName)
    {
        string name = rawName ?? string.Empty;
        string lower = name.ToLowerInvariant();
        if (lower.IndexOf("traditional", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-hant", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-tw", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-hk", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-mo", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("taiwan", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hong kong", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("macau", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("繁体", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("繁體", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("注音", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("倉頡", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("仓颉", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("速成", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("bopomofo", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("cangjie", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("quick", StringComparison.Ordinal) >= 0)
        {
            return "繁體";
        }

        if (lower.IndexOf("simplified", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-hans", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-cn", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("zh-sg", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("china", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("singapore", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("简体", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("簡體", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("拼音", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("pinyin", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("五笔", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("五筆", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("wubi", StringComparison.Ordinal) >= 0)
        {
            return "简体";
        }

        return string.Empty;
    }

    public static bool TryGetOutputVolumePercent(out int percent)
    {
        percent = 0;
        if (TryGetCoreAudioOutputVolumePercent(out percent))
        {
            return true;
        }

        try
        {
            uint volume;
            if (waveOutGetVolume(IntPtr.Zero, out volume) != 0)
            {
                return false;
            }

            int left = (int)(volume & 0xFFFF);
            int right = (int)((volume >> 16) & 0xFFFF);
            double average = (left + right) / 2.0;
            percent = Math.Max(0, Math.Min(100, (int)Math.Round(average * 100.0 / 65535.0)));
            return true;
        }
        catch
        {
            percent = 0;
            return false;
        }
    }

    private static bool TryGetCoreAudioOutputVolumePercent(out int percent)
    {
        const int classContextAll = 23;

        percent = 0;
        object enumeratorObject = null;
        IMMDevice endpoint = null;
        object volumeObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumerator();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, AudioRole.Multimedia, out endpoint) != 0 ||
                endpoint == null)
            {
                return false;
            }

            Guid volumeGuid = typeof(IAudioEndpointVolume).GUID;
            if (endpoint.Activate(ref volumeGuid, classContextAll, IntPtr.Zero, out volumeObject) != 0 ||
                volumeObject == null)
            {
                return false;
            }

            IAudioEndpointVolume endpointVolume = (IAudioEndpointVolume)volumeObject;
            float scalar;
            if (endpointVolume.GetMasterVolumeLevelScalar(out scalar) != 0)
            {
                return false;
            }

            percent = Math.Max(0, Math.Min(100, (int)Math.Round(scalar * 100.0f)));
            return true;
        }
        catch
        {
            percent = 0;
            return false;
        }
        finally
        {
            if (volumeObject != null && Marshal.IsComObject(volumeObject))
            {
                try
                {
                    Marshal.ReleaseComObject(volumeObject);
                }
                catch
                {
                }
            }

            if (endpoint != null && Marshal.IsComObject(endpoint))
            {
                try
                {
                    Marshal.ReleaseComObject(endpoint);
                }
                catch
                {
                }
            }

            if (enumeratorObject != null && Marshal.IsComObject(enumeratorObject))
            {
                try
                {
                    Marshal.ReleaseComObject(enumeratorObject);
                }
                catch
                {
                }
            }
        }
    }

    public static bool TryGetOutputMute(out bool muted)
    {
        const int classContextAll = 23;

        muted = false;
        object enumeratorObject = null;
        IMMDevice endpoint = null;
        object volumeObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumerator();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, AudioRole.Multimedia, out endpoint) != 0 ||
                endpoint == null)
            {
                return false;
            }

            Guid volumeGuid = typeof(IAudioEndpointVolume).GUID;
            if (endpoint.Activate(ref volumeGuid, classContextAll, IntPtr.Zero, out volumeObject) != 0 ||
                volumeObject == null)
            {
                return false;
            }

            IAudioEndpointVolume endpointVolume = (IAudioEndpointVolume)volumeObject;
            return endpointVolume.GetMute(out muted) == 0;
        }
        catch
        {
            muted = false;
            return false;
        }
        finally
        {
            if (volumeObject != null && Marshal.IsComObject(volumeObject))
            {
                try
                {
                    Marshal.ReleaseComObject(volumeObject);
                }
                catch
                {
                }
            }

            if (endpoint != null && Marshal.IsComObject(endpoint))
            {
                try
                {
                    Marshal.ReleaseComObject(endpoint);
                }
                catch
                {
                }
            }

            if (enumeratorObject != null && Marshal.IsComObject(enumeratorObject))
            {
                try
                {
                    Marshal.ReleaseComObject(enumeratorObject);
                }
                catch
                {
                }
            }
        }
    }

    private static IntPtr GetFocusedWindowForThread(uint threadId, IntPtr fallbackWindow)
    {
        if (threadId == 0)
        {
            return fallbackWindow;
        }

        try
        {
            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero)
            {
                return info.hwndFocus;
            }
        }
        catch
        {
        }

        return fallbackWindow;
    }

    private static bool? TryGetDefaultImeWindowNativeMode(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr imeWindow = ImmGetDefaultIMEWnd(windowHandle);
            if (imeWindow == IntPtr.Zero)
            {
                return null;
            }

            IntPtr result;
            if (!TrySendImeControl(imeWindow, IMC_GETCONVERSIONMODE, out result))
            {
                return null;
            }

            long conversionMode = result.ToInt64();
            return (conversionMode & IME_CMODE_NATIVE) != 0;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetDefaultImeWindowConversionMode(IntPtr windowHandle, out int conversionMode)
    {
        conversionMode = 0;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr imeWindow = ImmGetDefaultIMEWnd(windowHandle);
            if (imeWindow == IntPtr.Zero)
            {
                return false;
            }

            IntPtr result;
            if (!TrySendImeControl(imeWindow, IMC_GETCONVERSIONMODE, out result))
            {
                return false;
            }

            conversionMode = unchecked((int)result.ToInt64());
            return true;
        }
        catch
        {
            conversionMode = 0;
            return false;
        }
    }

    private static bool TrySendImeControl(IntPtr imeWindow, int command, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (imeWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return SendMessageTimeout(
                imeWindow,
                WM_IME_CONTROL,
                new IntPtr(command),
                IntPtr.Zero,
                SMTO_NORMAL,
                80,
                out result) != IntPtr.Zero;
        }
        catch
        {
            result = IntPtr.Zero;
            return false;
        }
    }

    private static bool? TryGetImeNativeMode(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr inputContext = IntPtr.Zero;
        try
        {
            inputContext = ImmGetContext(windowHandle);
            if (inputContext == IntPtr.Zero)
            {
                return null;
            }

            if (!ImmGetOpenStatus(inputContext))
            {
                return false;
            }

            int conversionMode;
            int sentenceMode;
            if (ImmGetConversionStatus(inputContext, out conversionMode, out sentenceMode))
            {
                return (conversionMode & IME_CMODE_NATIVE) != 0;
            }

            return true;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inputContext != IntPtr.Zero)
            {
                try
                {
                    ImmReleaseContext(windowHandle, inputContext);
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryGetImeConversionMode(IntPtr windowHandle, out int conversionMode)
    {
        conversionMode = 0;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr inputContext = IntPtr.Zero;
        try
        {
            inputContext = ImmGetContext(windowHandle);
            if (inputContext == IntPtr.Zero)
            {
                return false;
            }

            if (!ImmGetOpenStatus(inputContext))
            {
                conversionMode = 0;
                return true;
            }

            int sentenceMode;
            return ImmGetConversionStatus(inputContext, out conversionMode, out sentenceMode);
        }
        catch
        {
            conversionMode = 0;
            return false;
        }
        finally
        {
            if (inputContext != IntPtr.Zero)
            {
                try
                {
                    ImmReleaseContext(windowHandle, inputContext);
                }
                catch
                {
                }
            }
        }
    }

    private static string GetJapaneseModeFromConversionMode(int conversionMode)
    {
        if ((conversionMode & IME_CMODE_NATIVE) == 0)
        {
            return "A";
        }

        if ((conversionMode & IME_CMODE_KATAKANA) != 0)
        {
            return "ア";
        }

        return "あ";
    }

    private static string FormatInputCulture(CultureInfo culture, bool? nativeMode)
    {
        if (culture == null)
        {
            return "IME";
        }

        string name = culture.TwoLetterISOLanguageName;
        if (string.Equals(name, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return FormatChineseInputCulture(culture, nativeMode);
        }

        if (string.Equals(name, "ja", StringComparison.OrdinalIgnoreCase))
        {
            return FormatJapaneseInputCulture(nativeMode.HasValue && !nativeMode.Value ? "A" : "日");
        }

        if (nativeMode.HasValue &&
            !nativeMode.Value &&
            string.Equals(name, "ko", StringComparison.OrdinalIgnoreCase))
        {
            return "ENG";
        }

        if (string.Equals(name, "ko", StringComparison.OrdinalIgnoreCase))
        {
            return "한";
        }

        if (string.Equals(name, "en", StringComparison.OrdinalIgnoreCase))
        {
            return "ENG";
        }

        return string.IsNullOrEmpty(name) ? "IME" : name.ToUpperInvariant();
    }

    private static string FormatChineseInputCulture(CultureInfo culture, bool? nativeMode)
    {
        string variant = IsTraditionalChineseCulture(culture) ? "繁體" : "简体";
        return variant + (nativeMode.HasValue && !nativeMode.Value ? "（ENG）" : "（中）");
    }

    private static string FormatJapaneseInputCulture(string mode)
    {
        mode = (mode ?? string.Empty).Trim();
        if (mode.Length == 0)
        {
            mode = "日";
        }

        return "日语（" + mode + "）";
    }

    private static bool IsTraditionalChineseCulture(CultureInfo culture)
    {
        if (culture == null)
        {
            return false;
        }

        string name = culture.Name ?? string.Empty;
        string englishName = culture.EnglishName ?? string.Empty;
        string nativeName = culture.NativeName ?? string.Empty;
        return name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "zh-CHT", StringComparison.OrdinalIgnoreCase) ||
               englishName.IndexOf("Traditional", StringComparison.OrdinalIgnoreCase) >= 0 ||
               englishName.IndexOf("Taiwan", StringComparison.OrdinalIgnoreCase) >= 0 ||
               englishName.IndexOf("Hong Kong", StringComparison.OrdinalIgnoreCase) >= 0 ||
               englishName.IndexOf("Macao", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nativeName.IndexOf("繁", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nativeName.IndexOf("臺", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nativeName.IndexOf("台", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nativeName.IndexOf("香港", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static RECT ToRect(Rectangle rectangle)
    {
        RECT rect = new RECT();
        rect.Left = rectangle.Left;
        rect.Top = rectangle.Top;
        rect.Right = rectangle.Right;
        rect.Bottom = rectangle.Bottom;
        return rect;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(length + 1);
        int copied = GetWindowText(handle, builder, builder.Capacity);
        if (copied <= 0)
        {
            return string.Empty;
        }

        return builder.ToString().Trim();
    }

    private static bool IsShellOrUtilityWindowClass(string className)
    {
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "DV2ControlHost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryPulseSeelenDockWindowToFront(out string detail)
    {
        List<IntPtr> handles = FindSeelenDockAndBarWindows();
        if (handles.Count == 0)
        {
            detail = "Seelen dock/top bar windows were not found.";
            return false;
        }

        int successCount = 0;
        int failureCount = 0;
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < handles.Count; i++)
        {
            IntPtr handle = handles[i];
            bool success = SetWindowPos(
                handle,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOACTIVATE |
                SWP_NOMOVE |
                SWP_NOSIZE |
                SWP_SHOWWINDOW);
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(success ? "pulsed " : "failed ");
            builder.Append("0x");
            builder.Append(handle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
            if (success)
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }
        }

        detail = "Seelen dock/top bar foreground pulse handled " +
            handles.Count.ToString(CultureInfo.InvariantCulture) +
            " window(s), success=" +
            successCount.ToString(CultureInfo.InvariantCulture) +
            ", failed=" +
            failureCount.ToString(CultureInfo.InvariantCulture) +
            ". " +
            builder;
        return successCount > 0;
    }

    private static List<IntPtr> FindSeelenDockAndBarWindows()
    {
        List<IntPtr> handles = new List<IntPtr>();
        IntPtr fallbackHandle = IntPtr.Zero;
        int fallbackScore = int.MinValue;
        EnumWindows(delegate(IntPtr handle, IntPtr lParam)
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            string className = GetWindowClassName(handle);
            if (!string.Equals(className, "Tauri Window", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0 || !ContainsSeelen(TryGetProcessImagePath((int)processId)))
            {
                return true;
            }

            RECT rect;
            if (!GetWindowRect(handle, out rect))
            {
                return true;
            }

            int width = Math.Max(0, rect.Right - rect.Left);
            int height = Math.Max(0, rect.Bottom - rect.Top);
            if (width <= 0 || height <= 0)
            {
                return true;
            }

            string title = GetWindowTitle(handle);
            Rectangle bounds = Screen.FromHandle(handle).Bounds;
            bool explicitBarTitle = IsSeelenDockTitle(title) || IsSeelenTopBarTitle(title);
            bool likelyEdgeBar = IsLikelySeelenTopOrBottomBarWindow(rect, bounds, width, height);
            if (explicitBarTitle || likelyEdgeBar)
            {
                AddUniqueWindowHandle(handles, handle);
                return true;
            }

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            bool topMost = (exStyle & WS_EX_TOPMOST) != 0;
            bool noActivate = (exStyle & WS_EX_NOACTIVATE) != 0;
            bool edgeAligned =
                rect.Left <= bounds.Left + 2 ||
                rect.Top <= bounds.Top + 2 ||
                rect.Right >= bounds.Right - 2 ||
                rect.Bottom >= bounds.Bottom - 2;
            int score = 0;
            if (topMost)
            {
                score += 1000;
            }

            if (noActivate)
            {
                score += 500;
            }

            if (edgeAligned)
            {
                score += 250;
            }

            score += Math.Min(10000, width * height / 1000);
            if (score > fallbackScore)
            {
                fallbackScore = score;
                fallbackHandle = handle;
            }

            return true;
        }, IntPtr.Zero);

        if (handles.Count == 0 && fallbackHandle != IntPtr.Zero)
        {
            AddUniqueWindowHandle(handles, fallbackHandle);
        }

        return handles;
    }

    private static void AddUniqueWindowHandle(List<IntPtr> handles, IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        for (int i = 0; i < handles.Count; i++)
        {
            if (handles[i] == handle)
            {
                return;
            }
        }

        handles.Add(handle);
    }

    private static bool IsLikelySeelenTopOrBottomBarWindow(RECT rect, Rectangle screenBounds, int width, int height)
    {
        if (width <= 0 || height <= 0 || screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            return false;
        }

        const int Tolerance = 4;
        int maximumBarHeight = Math.Max(72, Math.Min(220, screenBounds.Height / 4));
        bool compactHeight = height <= maximumBarHeight;
        bool horizontalBar = width >= Math.Max(160, height * 2);
        bool touchesTop = rect.Top <= screenBounds.Top + Tolerance;
        bool touchesBottom = rect.Bottom >= screenBounds.Bottom - Tolerance;
        return compactHeight && horizontalBar && (touchesTop || touchesBottom);
    }

    private static bool IsSeelenDockTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        return title.IndexOf("Dock", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Taskbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Weg", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("停靠", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("任务", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("任務", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsSeelenTopBarTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        return title.IndexOf("Fancy Toolbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("TopBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Top Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Toolbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Tool Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("精美工具栏", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("花式工具栏", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("顶部", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("頂部", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("工具栏", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("工具列", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static IntPtr FindDesktopHostWindow()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr result;
            SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out result);
        }

        IntPtr worker = IntPtr.Zero;
        EnumWindows(delegate(IntPtr topHandle, IntPtr lParam)
        {
            IntPtr shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                IntPtr nextWorker = FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
                if (nextWorker != IntPtr.Zero)
                {
                    worker = nextWorker;
                }
            }

            return true;
        }, IntPtr.Zero);

        if (worker != IntPtr.Zero)
        {
            return worker;
        }

        return progman;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr ownHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownHandle)
        {
            return false;
        }

        if (!IsWindowVisible(foreground))
        {
            return false;
        }

        string className = GetWindowClassName(foreground);
        if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RECT rect;
        if (!GetWindowRect(foreground, out rect))
        {
            return false;
        }

        Rectangle bounds = Screen.FromHandle(foreground).Bounds;
        const int Tolerance = 2;
        return rect.Left <= bounds.Left + Tolerance &&
               rect.Top <= bounds.Top + Tolerance &&
               rect.Right >= bounds.Right - Tolerance &&
               rect.Bottom >= bounds.Bottom - Tolerance;
    }

    public static bool IsForegroundWindowMaximizedOrFullscreen(IntPtr ownHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownHandle)
        {
            return false;
        }

        if (!IsWindowVisible(foreground))
        {
            return false;
        }

        if (IsCurrentProcessWindow(foreground) || IsSeelenUiWindow(foreground))
        {
            return false;
        }

        string className = GetWindowClassName(foreground);
        if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsZoomed(foreground))
        {
            return true;
        }

        RECT rect;
        if (!GetWindowRect(foreground, out rect))
        {
            return false;
        }

        Rectangle bounds = Screen.FromHandle(foreground).Bounds;
        const int Tolerance = 2;
        return rect.Left <= bounds.Left + Tolerance &&
               rect.Top <= bounds.Top + Tolerance &&
               rect.Right >= bounds.Right - Tolerance &&
               rect.Bottom >= bounds.Bottom - Tolerance;
    }

    public static bool IsLeftMouseButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & unchecked((short)0x8000)) != 0;
    }

    public static bool IsAnyMouseButtonDown()
    {
        return
            (GetAsyncKeyState(VK_LBUTTON) & unchecked((short)0x8000)) != 0 ||
            (GetAsyncKeyState(VK_RBUTTON) & unchecked((short)0x8000)) != 0 ||
            (GetAsyncKeyState(VK_MBUTTON) & unchecked((short)0x8000)) != 0 ||
            (GetAsyncKeyState(VK_XBUTTON1) & unchecked((short)0x8000)) != 0 ||
            (GetAsyncKeyState(VK_XBUTTON2) & unchecked((short)0x8000)) != 0;
    }

    public static bool IsClickThroughModifierDown()
    {
        return
            (GetAsyncKeyState(VK_CONTROL) & unchecked((short)0x8000)) != 0 ||
            (GetAsyncKeyState(VK_MENU) & unchecked((short)0x8000)) != 0;
    }

    public static bool IsForegroundDesktopOrShell(IntPtr ownHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownHandle)
        {
            return true;
        }

        string className = GetWindowClassName(foreground);
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        StringBuilder builder = new StringBuilder(256);
        int length = GetClassName(handle, builder, builder.Capacity);
        if (length <= 0)
        {
            return string.Empty;
        }

        return builder.ToString();
    }

    private static bool IsCurrentProcessWindow(IntPtr handle)
    {
        try
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            return processId == (uint)Process.GetCurrentProcess().Id;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSeelenUiWindow(IntPtr handle)
    {
        string className = GetWindowClassName(handle);
        if (ContainsSeelen(className))
        {
            return true;
        }

        try
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0)
            {
                return false;
            }

            return ContainsSeelen(TryGetProcessImagePath((int)processId));
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsSeelen(string value)
    {
        return !string.IsNullOrEmpty(value) &&
            value.IndexOf("seelen", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string DescribeProcessMachine()
    {
        ushort processMachine;
        ushort nativeMachine;
        try
        {
            if (IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine))
            {
                return string.Format(
                    "process={0}, native={1}, 64bit={2}",
                    MachineName(processMachine),
                    MachineName(nativeMachine),
                    Environment.Is64BitProcess);
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        bool wow64;
        if (IsWow64Process(GetCurrentProcess(), out wow64))
        {
            return string.Format("wow64={0}, 64bit={1}", wow64, Environment.Is64BitProcess);
        }

        return string.Format("64bit={0}", Environment.Is64BitProcess);
    }

    private static string MachineName(ushort machine)
    {
        if (machine == IMAGE_FILE_MACHINE_UNKNOWN)
        {
            return "native";
        }

        if (machine == IMAGE_FILE_MACHINE_ARM64)
        {
            return "ARM64";
        }

        if (machine == IMAGE_FILE_MACHINE_ARMNT)
        {
            return "ARM";
        }

        if (machine == IMAGE_FILE_MACHINE_AMD64)
        {
            return "x64";
        }

        if (machine == IMAGE_FILE_MACHINE_I386)
        {
            return "x86";
        }

        return "0x" + machine.ToString("X4");
    }
}
