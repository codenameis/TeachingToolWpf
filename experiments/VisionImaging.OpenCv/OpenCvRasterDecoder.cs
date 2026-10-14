using OpenCvSharp;
using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionImaging.OpenCv;

/// <summary>전체 원본 Mat을 디코딩하는 비교용 어댑터. 대용량 메모리 비용을 숨기지 않는다.</summary>
public sealed class OpenCvRasterDecoder : IRasterDecoder
{
    public string CacheIdentity => "opencv-4.13-unchanged-rgba-v1";

    public IRasterReader Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var image = Cv2.ImRead(path, ImreadModes.Unchanged);
        try
        {
            token.ThrowIfCancellationRequested();
            if (image.Empty())
            {
                throw new InvalidDataException("OpenCV가 파일을 디코딩하지 못했습니다.");
            }
            if (image.Type() != MatType.CV_8UC1 && image.Type() != MatType.CV_16UC1 &&
                image.Type() != MatType.CV_8UC3 && image.Type() != MatType.CV_8UC4)
            {
                throw new NotSupportedException($"실험 디코더가 지원하지 않는 픽셀 형식: {image.Type()}");
            }
            return new Reader(image);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private sealed class Reader(Mat image) : IRasterReader
    {
        public int Width => image.Width;
        public int Height => image.Height;
        public PixelKind Kind => image.Type() == MatType.CV_16UC1 ? PixelKind.Gray16 : image.Channels() == 1 ? PixelKind.Gray8 : PixelKind.Rgba32;

        public unsafe void ReadRows(int top, int height, Span<byte> destination, CancellationToken token)
        {
            // Mat.Width는 native 호출이다. 읽기 중 크기는 고정이므로 픽셀마다 다시 조회하지 않는다.
            var width = Width;
            var channels = image.Channels();
            var stride = checked(width * (Kind == PixelKind.Gray16 ? 2 : Kind == PixelKind.Gray8 ? 1 : 4));
            if (top < 0 || height <= 0 || top > Height - height || destination.Length != checked(stride * height))
            {
                throw new ArgumentOutOfRangeException(nameof(top));
            }
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                var target = destination.Slice(y * stride, stride);
                var source = new ReadOnlySpan<byte>((void*)image.Ptr(top + y), checked(width * (int)image.ElemSize()));
                if (channels == 1)
                {
                    source.CopyTo(target);
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        target[x * 4] = source[(x * channels) + 2];
                        target[(x * 4) + 1] = source[(x * channels) + 1];
                        target[(x * 4) + 2] = source[x * channels];
                        target[(x * 4) + 3] = channels == 4 ? source[(x * channels) + 3] : (byte)255;
                    }
                }
            }
        }

        public void Dispose() => image.Dispose();
    }
}
