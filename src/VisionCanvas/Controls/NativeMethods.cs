using System.Runtime.InteropServices;

namespace VisionCanvas.Controls;

internal enum NativeWindowMessage
{
    KillFocus = 0x0008,
    SetCursor = 0x0020,
    GetDialogCode = 0x0087,
    KeyDown = 0x0100,
    MouseMove = 0x0200,
    LeftButtonDown = 0x0201,
    LeftButtonUp = 0x0202,
    LeftButtonDoubleClick = 0x0203,
    MiddleButtonDown = 0x0207,
    MiddleButtonUp = 0x0208,
    MiddleButtonDoubleClick = 0x0209,
    MouseWheel = 0x020A,
    CaptureChanged = 0x0215
}

internal enum NativeVirtualKey
{
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20
}

internal enum NativeCursor
{
    Arrow = 32512,
    Cross = 32515,
    Hand = 32649
}

[Flags]
internal enum NativeWindowStyle
{
    TabStop = 0x00010000,
    ClipChildren = 0x02000000,
    ClipSiblings = 0x04000000,
    Visible = 0x10000000,
    Child = 0x40000000
}

internal static partial class NativeMethods
{
    internal const int WantAllDialogKeys = 0x0004;
    private const int ClientHitTest = 1;
    private const double WheelDeltaPerNotch = 120;

    internal static bool IsKeyPressed(NativeVirtualKey key) => GetKeyState((int)key) < 0;

    internal static bool IsClientCursorMessage(nint lParam) => LowWord(lParam) == ClientHitTest;

    // 좌표는 부호 있는 16비트다. 주 모니터 왼쪽의 화면 좌표도 음수 그대로 보존한다.
    internal static NativePoint DecodePoint(nint lParam) => new()
    {
        X = unchecked((short)LowWord(lParam)),
        Y = unchecked((short)HighWord(lParam))
    };

    // 고해상도 휠의 반 칸 같은 부분 입력도 버리지 않는다.
    internal static double DecodeWheelNotches(nint wParam) => unchecked((short)HighWord(wParam)) / WheelDeltaPerNotch;

    private static int LowWord(nint value) => (int)(value.ToInt64() & 0xffff);

    private static int HighWord(nint value) => (int)((value.ToInt64() >> 16) & 0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
