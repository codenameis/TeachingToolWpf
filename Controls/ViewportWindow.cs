using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TeachingToolWpf.Controls;

/// <summary>An input-capable Win32 child window. A STATIC control is mouse-transparent by default.</summary>
internal static class ViewportWindow
{
    private const string ClassName = "TeachingToolWpf.Dx12Viewport";
    private const NativeWindowStyle Style = NativeWindowStyle.Child | NativeWindowStyle.Visible |
        NativeWindowStyle.ClipSiblings | NativeWindowStyle.ClipChildren | NativeWindowStyle.TabStop;
    // Keep the managed delegate alive for every window registered with this class.
    private static readonly WindowProcedure Procedure = DefWindowProc;
    private static readonly Lazy<ushort> RegisteredClass = new(Register);

    public static nint Create(nint parent)
    {
        _ = RegisteredClass.Value;
        var handle = NativeMethods.CreateWindowEx(0, ClassName, "Vision editing canvas",
            (int)Style, 0, 0, 1, 1, parent, 0, GetModuleHandle(null), 0);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static ushort Register()
    {
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(0, (nint)NativeCursor.Arrow),
            ClassName = ClassName
        };
        var atom = RegisterClassEx(ref definition);
        if (atom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return atom;
    }

    internal static void UseCursor(NativeCursor cursor) => SetCursor(LoadCursor(0, (nint)cursor));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);
}
