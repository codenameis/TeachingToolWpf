using System.Diagnostics;
using PoseMatch.Tdcm;
using VisionAlgorithm.PoseMatch;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record TdcmTemplate(PixelImage Pixels, int Reduction, RoiGeometry TeachingRegion);
public sealed record TdcmSearchOptions
{
    public MatchOptions Matching { get; init; } = new();
    public int WindowSize { get; init; } = 512;
    public int Threads { get; init; } = 4;
    public TdcmDevice Device { get; init; } = TdcmDevice.Cpu;
    public TdcmBackend Backend { get; init; } = TdcmBackend.OnnxRuntime;
    public string? WeightsPath { get; init; }
    public int MaximumWindows { get; init; } = 4096;
    public void Validate()
    {
        Matching.Validate();
        if (!Enum.IsDefined(Device) || !Enum.IsDefined(Backend))
        {
            throw new ArgumentOutOfRangeException(nameof(Device));
        }
        if (Backend == TdcmBackend.OnnxRuntime && !string.IsNullOrWhiteSpace(WeightsPath))
            throw new ArgumentException("사용자 가중치 파일은 TorchSharp에서 선택하세요.");
        if (WindowSize is < 224 or > 1024 || WindowSize % 4 != 0 || Threads is < 1 or > 32 || MaximumWindows is < 1 or > 65536 || Matching.MaximumMatches > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "창 224~1024 (4 배수), 스레드 1~32, 최대 창 1~65536, 최대 결과 1~1000이 필요합니다.");
        }
    }
}
public sealed record TdcmSearchOutput(MatchOutput Result, int Windows, double TotalMilliseconds, double ReadMilliseconds, double InitializationMilliseconds, double InferenceMilliseconds);

public interface ITdcmMatchingService
{
    Task<TdcmTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token, RoiExclusion? exclusion = null);
    Task<TdcmSearchOutput> SearchAsync(ImageReference image, bool live, TdcmTemplate template, RoiGeometry? region, TdcmSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null);
}

public sealed class TdcmMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<TdcmSearchOptions, ITdcmMatcher>? createMatcher = null) : ITdcmMatchingService, IDisposable
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private ITdcmMatcher? _matcher;
    private (int Threads, TdcmDevice Device, TdcmBackend Backend, string? Path, long Version) _sessionKey;
    private int _disposed;

    public Task<TdcmTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.Run(async () =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ShapeMatchingService.ValidateRegion(image, roi);
            if (roi.Shape.Kind != RoiShapeKind.Rectangle || exclusion is not null)
            {
                throw new NotSupportedException("PoseMatch-TDCM 학습은 마스크 없는 사각형 ROI를 지원합니다.");
            }

            if (Math.Max(roi.Width, roi.Height) > 2048 || Math.Min(roi.Width, roi.Height) < 18 || Math.Max(roi.Width, roi.Height) / Math.Min(roi.Width, roi.Height) > 2)
            {
                throw new ArgumentException("학습 ROI는 각 변 18~2048 px, 가로세로 비율 1:2~2:1이어야 합니다. 내부 모델은 36×36으로 정규화합니다.");
            }

            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            var left = Math.Max(0, (int)Math.Floor(roi.Bounds.Left));
            var top = Math.Max(0, (int)Math.Floor(roi.Bounds.Top));
            var right = Math.Min(image.Width, (int)Math.Ceiling(roi.Bounds.Right));
            var bottom = Math.Min(image.Height, (int)Math.Ceiling(roi.Bounds.Bottom));
            var crop = reader.Read(left, top, right - left, bottom - top, token, 16_777_216);
            // 회전 ROI를 원래 축으로 펼친다. 검색 결과 각도는 이 축에 대한 절대 영상 각도다.
            var pixels = new byte[36 * 36];
            // 검색 피라미드는 면적을 평균한다. 학습도 출력 픽셀의 면적을 표본화하여
            // 축소 시 얇은 선/문자가 한 지점의 위상에 따라 사라지지 않게 한다.
            var samplesX = Math.Max(1, (int)Math.Ceiling(roi.Width / 36));
            var samplesY = Math.Max(1, (int)Math.Ceiling(roi.Height / 36));
            for (var y = 0; y < 36; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < 36; x++)
                {
                    double sum = 0;
                    for (var sy = 0; sy < samplesY; sy++)
                    {
                        for (var sx = 0; sx < samplesX; sx++)
                        {
                            var world = roi.ToWorld(new(
                                ((x + (sx + .5) / samplesX) / 36 - .5) * roi.Width,
                                ((y + (sy + .5) / samplesY) / 36 - .5) * roi.Height));
                            sum += Sample(world);
                        }
                    }
                    pixels[y * 36 + x] = (byte)Math.Round(sum / (samplesX * samplesY));
                }
            }

            double Sample(PointD world)
            {
                var px = Math.Clamp(world.X - left - .5, 0, crop.Width - 1);
                var py = Math.Clamp(world.Y - top - .5, 0, crop.Height - 1);
                var x0 = (int)px;
                var y0 = (int)py;
                var x1 = Math.Min(x0 + 1, crop.Width - 1);
                var y1 = Math.Min(y0 + 1, crop.Height - 1);
                var fx = px - x0;
                var fy = py - y0;
                return (crop.Pixels.Span[y0 * crop.Stride + x0] * (1 - fx) + crop.Pixels.Span[y0 * crop.Stride + x1] * fx) * (1 - fy) +
                    (crop.Pixels.Span[y1 * crop.Stride + x0] * (1 - fx) + crop.Pixels.Span[y1 * crop.Stride + x1] * fx) * fy;
            }
            var factor = 1;
            while (Math.Max(roi.Width, roi.Height) / factor > 54 && factor < 32)
            {
                factor *= 2;
            }

            return new TdcmTemplate(new(pixels, 36, 36, 36), factor, roi);
        }, token);

    public async Task<TdcmSearchOutput> SearchAsync(ImageReference image, bool live, TdcmTemplate template, RoiGeometry? region,
        TdcmSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
    {
        var timer = Stopwatch.StartNew();
        var completed = await Task.Run(async () =>
        {
            await _sessionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                options.Validate();
                if (exclusion is not null)
                {
                    throw new NotSupportedException("PoseMatch-TDCM 검색 마스크는 지원하지 않습니다.");
                }

                if (region is not null)
                {
                    ShapeMatchingService.ValidateRegion(image, region);
                }

                var readStart = Stopwatch.StartNew();
                using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
                reader.PreferDirectGray = true;
                var readMs = readStart.Elapsed.TotalMilliseconds;
                var initialization = Stopwatch.StartNew();
                var key = (options.Threads, options.Device, options.Backend, options.WeightsPath,
                    string.IsNullOrWhiteSpace(options.WeightsPath) ? 0 : File.GetLastWriteTimeUtc(options.WeightsPath).Ticks);
                if (_matcher is null || _sessionKey != key)
                {
                    _matcher?.Dispose();
                    _matcher = null;
                    _matcher = createMatcher is not null ? createMatcher(options)
                        : options.Backend == TdcmBackend.OnnxRuntime ? new TdcmMatcher(options.Threads, options.Device)
                        : throw new NotSupportedException("호스트에 TorchSharp 검사 공급자가 연결되지 않았습니다.");
                    _sessionKey = key;
                }
                var matcher = _matcher;
                var initializationMs = initialization.Elapsed.TotalMilliseconds;
                var inferenceMs = 0d;
                var factor = template.Reduction;
                var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
                var firstX = Math.Max(0, (int)Math.Floor(bounds.Left / factor));
                var firstY = Math.Max(0, (int)Math.Floor(bounds.Top / factor));
                var lastX = Math.Min((image.Width + factor - 1) / factor, (int)Math.Ceiling(bounds.Right / factor));
                var lastY = Math.Min((image.Height + factor - 1) / factor, (int)Math.Ceiling(bounds.Bottom / factor));
                var windows = 0;
                var truncated = false;
                var candidates = new PriorityQueue<PoseMatchResult, float>();
                var step = firstX == 0 && firstY == 0 && lastX <= options.WindowSize && lastY <= options.WindowSize
                    ? options.WindowSize : options.WindowSize - 128;
                for (var y = firstY; y < lastY; y += step)
                {
                    for (var x = firstX; x < lastX; x += step)
                    {
                        token.ThrowIfCancellationRequested();
                        if (windows == options.MaximumWindows)
                        {
                            truncated = true;
                            goto Finished;
                        }
                        var left = Math.Max(0, x - 64);
                        var top = Math.Max(0, y - 64);
                        var width = Math.Min(options.WindowSize, (image.Width + factor - 1) / factor - left);
                        var height = Math.Min(options.WindowSize, (image.Height + factor - 1) / factor - top);
                        var read = Stopwatch.StartNew();
                        var pixels = ReadWindow(reader, image, left, top, width, height, factor, options.WindowSize, token);
                        readMs += read.Elapsed.TotalMilliseconds;
                        var inference = Stopwatch.StartNew();
                        var output = matcher.Find(pixels, template.Pixels, options.Matching with
                        {
                            MaximumMatches = options.Matching.MaximumCandidates
                        }, token);
                        inferenceMs += inference.Elapsed.TotalMilliseconds;
                        windows++;
                        truncated |= output.CandidatesTruncated || output.MatchLimitExceeded;
                        foreach (var match in output.Matches)
                        {
                            var gx = match.X + left;
                            var gy = match.Y + top;
                            if (gx < x || gy < y || gx >= Math.Min(x + step, lastX) || gy >= Math.Min(y + step, lastY))
                            {
                                continue;
                            }

                            var global = match with
                            {
                                X = gx * factor,
                                Y = gy * factor,
                                Width = match.Width * factor,
                                Height = match.Height * factor
                            };
                            if (region is not null && !region.Contains(new PointD(global.X, global.Y)))
                            {
                                continue;
                            }

                            candidates.Enqueue(global, global.Score);
                            if (candidates.Count > options.Matching.MaximumCandidates)
                            {
                                candidates.Dequeue();
                                truncated = true;
                            }
                        }
                    }
                }

            Finished:
                var matches = new List<PoseMatchResult>();
                var limit = false;
                var radius = options.Matching.SuppressionRadius * factor;
                foreach (var match in candidates.UnorderedItems.Select(item => item.Element).OrderByDescending(match => match.Score).ThenBy(match => match.Y).ThenBy(match => match.X))
                {
                    if (matches.Any(other => Math.Pow(other.X - match.X, 2) + Math.Pow(other.Y - match.Y, 2) < radius * radius))
                    {
                        continue;
                    }

                    if (matches.Count == options.Matching.MaximumMatches)
                    {
                        limit = true;
                        break;
                    }
                    matches.Add(match);
                }
                return new TdcmSearchOutput(new(matches, truncated, limit), windows, timer.Elapsed.TotalMilliseconds, readMs, initializationMs, inferenceMs);
            }
            finally
            {
                _sessionGate.Release();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    Dispose();
                }
            }
        }, token).ConfigureAwait(false);
        return completed with { TotalMilliseconds = timer.Elapsed.TotalMilliseconds };
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        // 실행 중인 네이티브 세션은 해당 실행의 finally에서 해제한다. UI 종료를 막지 않는다.
        if (_sessionGate.Wait(0))
        {
            try
            {
                _matcher?.Dispose();
                _matcher = null;
            }
            finally
            {
                _sessionGate.Release();
            }
        }
    }

    private static PixelImage ReadWindow(MatchingImageReader reader, ImageReference image, int left, int top, int width, int height, int factor, int windowSize, CancellationToken token)
    {
        var rawWidth = Math.Min(width * factor, image.Width - left * factor);
        var rawHeight = Math.Min(height * factor, image.Height - top * factor);
        var fullWidth = rawWidth / factor;
        var fullHeight = rawHeight / factor;
        // GRN은 창 전체 통계를 사용한다. 작은 창으로 줄이면 같은 대상도 점수/검출이 바뀐다.
        var outputWidth = windowSize;
        var outputHeight = windowSize;
        var pixels = new byte[outputWidth * outputHeight];
        if (fullWidth > 0 && fullHeight > 0)
        {
            var reduced = reader.ReadReduced(left * factor, top * factor, fullWidth * factor, fullHeight * factor, factor, token);
            for (var y = 0; y < fullHeight; y++)
            {
                reduced.Pixels.Span.Slice(y * reduced.Stride, fullWidth).CopyTo(pixels.AsSpan(y * outputWidth));
            }
        }
        // 불완전한 마지막 면적도 원본에서 평균하여 가장자리 픽셀을 누락하지 않는다.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x < fullWidth && y < fullHeight)
                {
                    continue;
                }

                var w = Math.Min(factor, rawWidth - x * factor);
                var h = Math.Min(factor, rawHeight - y * factor);
                var edge = reader.Read((left + x) * factor, (top + y) * factor, w, h, token);
                var sum = 0;
                foreach (var value in edge.Pixels.Span)
                {
                    sum += value;
                }

                pixels[y * outputWidth + x] = (byte)(sum / (w * h));
            }
        }

        return TdcmImageAdapter.FromGray(new(pixels, outputWidth, outputHeight, outputWidth));
    }
}
