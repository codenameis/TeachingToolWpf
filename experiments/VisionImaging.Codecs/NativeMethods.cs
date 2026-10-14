using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VisionImaging.Codecs;

internal static unsafe partial class NativeMethods
{
    private const string Library = "VisionImageCodecs";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public int Width, Height, Kind, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Error
    {
        public int Code;
        private fixed byte _message[508];

        public string Message
        {
            get
            {
                fixed (byte* text = _message)
                {
                    var bytes = new ReadOnlySpan<byte>(text, 508);
                    var end = bytes.IndexOf((byte)0);
                    return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
                }
            }
        }
    }

    [LibraryImport(Library, EntryPoint = "vi_abi_version")]
    internal static partial int AbiVersion();

    [LibraryImport(Library, EntryPoint = "vi_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, ulong budget, int* cancelled, out nint reader, out Info info, out Error error);

    [LibraryImport(Library, EntryPoint = "vi_read_rows")]
    internal static partial int Read(ReaderHandle reader, int rows, byte* pixels, ulong length, int* cancelled, out Error error);

    [LibraryImport(Library, EntryPoint = "vi_close")]
    internal static partial void Close(nint reader);
}

internal sealed class ReaderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ReaderHandle() : this(nint.Zero) { }

    public ReaderHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.Close(handle);
        return true;
    }
}
