using System.Runtime.InteropServices;

namespace TeachingToolWpf.Controls;

internal static partial class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern short GetKeyState(int virtualKey);
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
