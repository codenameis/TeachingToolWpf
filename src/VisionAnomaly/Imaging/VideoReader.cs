using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Imaging;

/// <summary>생성·읽기·해제는 같은 수집 worker가 순서대로 실행한다.</summary>
public sealed class VideoReader : IDisposable
{
    private readonly VideoCapture _capture;
    private readonly Mat _decoded = new();
    private readonly Mat _bgra = new();
    private readonly VideoSourceSpec _source;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sequence;

    public VideoReader(VideoSourceSpec source)
    {
        source.Validate();
        _source = source;
        try
        {
            _capture = source.Kind == VideoSourceKind.Camera
                ? new(int.Parse(source.Address), VideoCaptureAPIs.DSHOW)
                : source.Kind == VideoSourceKind.Stream
                    ? new(source.Address, VideoCaptureAPIs.FFMPEG,
                        // OpenCV CAP_PROP_OPEN_TIMEOUT_MSEC / READ_TIMEOUT_MSEC (wrapper enum에는 아직 없음).
                        [53, 5000, 54, 2000])
                    : new(source.Address, VideoCaptureAPIs.FFMPEG);
            if (!_capture.IsOpened())
            {
                throw new IOException("영상을 열 수 없습니다. 주소·장치·코덱을 확인하세요.");
            }

            var fps = _capture.Fps;
            FramesPerSecond = double.IsFinite(fps) && fps is > 0 and <= 1000 ? fps : 25;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public double FramesPerSecond
    {
        get;
    }
    public double MediaSeconds
    {
        get; private set;
    }

    public VideoFrame? Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_capture.Read(_decoded) || _decoded.Empty())
        {
            if (_source.Kind == VideoSourceKind.File)
            {
                return null;
            }

            throw new IOException("영상 수신이 중단되었습니다. 연결을 확인하고 다시 연결하세요.");
        }
        token.ThrowIfCancellationRequested();
        if ((long)_decoded.Width * _decoded.Height > 8192L * 8192)
        {
            throw new InvalidDataException("입력 영상은 최대8192×8192 픽셀 예산을 지원합니다.");
        }

        if (_decoded.Channels() == 1)
        {
            Cv2.CvtColor(_decoded, _bgra, ColorConversionCodes.GRAY2BGRA);
        }
        else if (_decoded.Channels() == 3)
        {
            Cv2.CvtColor(_decoded, _bgra, ColorConversionCodes.BGR2BGRA);
        }
        else if (_decoded.Channels() == 4)
        {
            _decoded.CopyTo(_bgra);
        }
        else
        {
            throw new InvalidDataException("지원하지 않는 영상 채널 형식입니다.");
        }

        var pixels = new PixelBuffer(_bgra.Width, _bgra.Height, PixelKind.Bgra32);
        try
        {
            for (var row = 0; row < pixels.Height; row++)
            {
                Marshal.Copy(_bgra.Ptr(row), pixels.Bytes, row * pixels.Stride, pixels.Stride);
            }

            var fileTime = _capture.PosMsec / 1000;
            MediaSeconds = _source.Kind == VideoSourceKind.File
                ? Math.Max(_sequence / FramesPerSecond, double.IsFinite(fileTime) ? fileTime : 0)
                : _clock.Elapsed.TotalSeconds;
            return new(++_sequence, (long)(MediaSeconds * Stopwatch.Frequency), pixels);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _decoded.Dispose();
        _bgra.Dispose();
        _capture?.Dispose();
    }
}
