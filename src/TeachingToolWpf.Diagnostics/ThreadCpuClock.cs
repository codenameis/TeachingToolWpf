using System.Runtime.InteropServices;

namespace TeachingToolWpf.Diagnostics;

internal static class ThreadCpuClock
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);

    public static double Milliseconds()
    {
        if (!GetThreadTimes(new nint(-2), out _, out _, out var kernel, out var user))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return (kernel + user) / 10_000d;
    }
}
