using System.Runtime.InteropServices;
using NetVips;

namespace TeachingToolWpf.Imaging;

/// <summary>Region.Fetch의 managed 임시 배열 없이 libvips가 반환한 원본을 대여 버퍼에 복사한다.</summary>
internal static class VipsRegionPixels
{
    public static void Copy(Region region, int left, int top, int width, int height, byte[] destination, int length)
    {
        // SafeHandle 인자는 native 호출이 끝날 때까지 region 수명을 유지한다.
        var data = OperatingSystem.IsWindows()
            ? FetchWindows(region, left, top, width, height, out var size)
            : FetchLinux(region, left, top, width, height, out size);
        if (data == 0)
        {
            throw new VipsException("타일 원본 영역을 읽지 못했습니다.");
        }
        try
        {
            if (size != (nuint)length)
            {
                throw new InvalidDataException("타일 픽셀 형식 또는 길이가 맞지 않습니다.");
            }
            Marshal.Copy(data, destination, 0, length);
        }
        finally
        {
            NetVips.NetVips.Free(data);
        }
    }

    // libvips의 안정된 public C API다. Windows/Linux 배포가 쓰는 ABI 42 이름만 이 경계에 둔다.
    [DllImport("libvips-42.dll", EntryPoint = "vips_region_fetch", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FetchWindows(Region region, int left, int top, int width, int height, out nuint length);

    [DllImport("libvips.so.42", EntryPoint = "vips_region_fetch", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FetchLinux(Region region, int left, int top, int width, int height, out nuint length);
}
