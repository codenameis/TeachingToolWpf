using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace PyramidComparison;

internal sealed class ObservedDecoder(IRasterDecoder decoder, Action<string> record) : IRasterDecoder
{
    public string CacheIdentity => decoder.CacheIdentity;

    public IRasterReader Open(string path, CancellationToken token)
    {
        record("DecodeStart");
        var reader = decoder.Open(path, token);
        record("DecodeComplete");
        return new Reader(reader, record);
    }

    private sealed class Reader(IRasterReader reader, Action<string> record) : IRasterReader
    {
        public int Width => reader.Width;
        public int Height => reader.Height;
        public PixelKind Kind => reader.Kind;
        public void ReadRows(int top, int height, Span<byte> destination, CancellationToken token) => reader.ReadRows(top, height, destination, token);
        public void Dispose()
        {
            record("OriginalWritten");
            reader.Dispose();
            record("DecoderDisposed");
        }
    }
}
