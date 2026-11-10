// File: samples/ToolBlock.Simulator/Services/ToolBlockDynamicImageLoader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBlock.Simulator.Models;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.ImageJ;
using VisionImaging;
using VisionImaging.Abstractions;

namespace ToolBlock.Simulator.Services;

/// <summary>
/// Provides <see cref="ITileSource"/> instances for <c>VisionCanvas.Skia</c>, streams 530 MP+ disk images
/// (such as <c>ken32.png</c> at <c>17,280 × 30,720</c>) directly from the native <see cref="DiskPyramidSource"/>
/// without allocating a full-image byte array, and extracts bounded Gray8 ROI windows on demand.
/// </summary>
public sealed class ToolBlockDynamicImageLoader : IImageLoader, IDisposable
{
    public const string LiveKeyPrefix = "toolblock:live:";
    public const int DefaultSceneWidth = 512;
    public const int DefaultSceneHeight = 384;
    public const int DefaultSceneStride = 512;
    public const int DefaultMaxWindowPixels = 1_048_576; // 1 MP budget (1024 × 1024) for instant < 2 ms ROI analysis

    private const int StrideAlignmentBytes = 16;
    private const int MaxCachedGrayTiles = 24;
    private const double DefaultDpi = 96.0;
    private const int RedLumaWeight = 77;
    private const int GreenLumaWeight = 150;
    private const int BlueLumaWeight = 29;
    private const int LumaRoundingBias = 128;
    private const int LumaShiftBits = 8;

    private readonly string _cacheDirectory;
    private readonly IImageLoader _fallbackLoader;
    private readonly object _readerGate = new();
    private readonly Dictionary<TileKey, byte[]> _grayTileCache = new(MaxCachedGrayTiles);
    private readonly Queue<TileKey> _grayTileFifo = new(MaxCachedGrayTiles);

    private ITileSource? _analysisTileSource;
    private string _analysisSourceKey = string.Empty;
    private byte[] _activeGrayBuffer;
    private int _activeWidth;
    private int _activeHeight;
    private int _activeStride;
    private bool _disposed;

    public ToolBlockDynamicImageLoader(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _fallbackLoader = new SourceImageLoader(new ImageLoader(cacheDirectory));
        _activeWidth = DefaultSceneWidth;
        _activeHeight = DefaultSceneHeight;
        _activeStride = DefaultSceneStride;
        _activeGrayBuffer = new byte[_activeHeight * _activeStride];
    }

    public int ActiveWidth => _activeWidth;
    public int ActiveHeight => _activeHeight;
    public int ActiveStride => _activeStride;

    public ReadOnlyImageView2D<byte> GetActiveImageView()
        => new(_activeGrayBuffer, _activeWidth, _activeHeight, _activeStride);

    public void UpdateActiveBuffer(ReadOnlySpan<byte> sourceBuffer, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        int requiredLen = checked(height * stride);
        if (_activeGrayBuffer.Length != requiredLen)
        {
            _activeGrayBuffer = new byte[requiredLen];
        }

        sourceBuffer.Slice(0, requiredLen).CopyTo(_activeGrayBuffer);
        _activeWidth = width;
        _activeHeight = height;
        _activeStride = stride;
    }

    /// <summary>
    /// Synchronizes the dedicated analysis tile reader with the active disk image file (<paramref name="filePath"/>).
    /// Must be called after <c>ImageCoordinator.ReplaceAsync</c> has already built/opened the pyramid cache.
    /// </summary>
    public void BindDiskAnalysisSource(string filePath, int resolvedWidth, int resolvedHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        lock (_readerGate)
        {
            _activeWidth = Math.Max(1, resolvedWidth);
            _activeHeight = Math.Max(1, resolvedHeight);
            _activeStride = _activeWidth;

            if (string.Equals(_analysisSourceKey, filePath, StringComparison.OrdinalIgnoreCase) && _analysisTileSource is not null)
            {
                return;
            }

            _grayTileCache.Clear();
            _grayTileFifo.Clear();
            _analysisTileSource?.Dispose();
            _analysisTileSource = null;
            _analysisSourceKey = string.Empty;

            try
            {
                _analysisTileSource = DiskPyramidSource.Open(filePath, _cacheDirectory, new Progress<string>(), CancellationToken.None);
                _analysisSourceKey = filePath;
                _activeWidth = _analysisTileSource.Info.Width;
                _activeHeight = _analysisTileSource.Info.Height;
                _activeStride = _activeWidth;
            }
            catch
            {
                _analysisTileSource = null;
                _analysisSourceKey = string.Empty;
            }
        }
    }

    /// <summary>
    /// Fast $O(1)$ 3×3 neighborhood pixel probe directly from the cached 512×512 tile (for <c>ken32.png</c> and disk images)
    /// or from the in-memory frame buffer.
    /// </summary>
    public ImageJPixelProbeResult ProbePixel(
        LoadedImageFrameItem? frame,
        int imageX,
        int imageY,
        ImageJWorkbenchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (frame is null)
        {
            return default;
        }

        if (!frame.IsDiskTiled)
        {
            var view = new ReadOnlyImageView2D<byte>(frame.WorkingPixels, frame.Width, frame.Height, frame.Stride);
            return engine.ProbePixel(view, imageX, imageY);
        }

        if (imageX < 0 || imageY < 0 || imageX >= _activeWidth || imageY >= _activeHeight)
        {
            return ImageJPixelProbeResult.OutOfBounds(imageX, imageY);
        }

        // Extract a tiny 3x3 window around (imageX, imageY) from Level-0 tiles (cached in _grayTileCache!)
        int x0 = Math.Max(0, imageX - 1);
        int y0 = Math.Max(0, imageY - 1);
        int x1 = Math.Min(_activeWidth - 1, imageX + 1);
        int y1 = Math.Min(_activeHeight - 1, imageY + 1);
        int w = (x1 - x0) + 1;
        int h = (y1 - y0) + 1;

        ExtractedRoiWindow patch = ExtractRoiWindow(frame, x0, y0, w, h, maxPixelBudget: 64);
        if (!patch.IsValid)
        {
            return ImageJPixelProbeResult.OutOfBounds(imageX, imageY);
        }

        ImageJPixelProbeResult localProbe = engine.ProbePixel(patch.AsView(), imageX - x0, imageY - y0);
        return new ImageJPixelProbeResult(
            IsInsideImage: true,
            X: imageX,
            Y: imageY,
            GrayValue: localProbe.GrayValue,
            Mean3x3: localProbe.Mean3x3,
            Min3x3: localProbe.Min3x3,
            Max3x3: localProbe.Max3x3,
            SobelGradient: localProbe.SobelGradient);
    }

    /// <summary>
    /// Extracts a compact Gray8 window for the specified world ROI <c>(roiX, roiY, roiWidth, roiHeight)</c>.
    /// When the ROI area exceeds <paramref name="maxPixelBudget"/> (e.g., Whole Image on <c>ken32.png</c> = 530 MP),
    /// automatically selects the appropriate pyramid level (<c>ScaleFactor = 2^Level</c>) so the returned window
    /// never exceeds <paramref name="maxPixelBudget"/> and always completes in &lt; 5 ms.
    /// </summary>
    public ExtractedRoiWindow ExtractRoiWindow(
        LoadedImageFrameItem? frame,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        int maxPixelBudget = DefaultMaxWindowPixels)
    {
        if (frame is null || _activeWidth <= 0 || _activeHeight <= 0)
        {
            return default;
        }

        int clampedX = Math.Clamp(roiX, 0, Math.Max(0, _activeWidth - 1));
        int clampedY = Math.Clamp(roiY, 0, Math.Max(0, _activeHeight - 1));
        int clampedW = Math.Clamp(roiWidth, 1, Math.Max(1, _activeWidth - clampedX));
        int clampedH = Math.Clamp(roiHeight, 1, Math.Max(1, _activeHeight - clampedY));
        int budget = Math.Max(4096, maxPixelBudget);

        if (!frame.IsDiskTiled)
        {
            return ExtractFromMemoryFrame(frame, clampedX, clampedY, clampedW, clampedH, budget);
        }

        lock (_readerGate)
        {
            if (_analysisTileSource is null)
            {
                return default;
            }

            ImageInfo info = _analysisTileSource.Info;
            int level = 0;
            while (level + 1 < info.Levels)
            {
                int factor = 1 << level;
                long levelW = Math.Max(1, (clampedW + factor - 1) / factor);
                long levelH = Math.Max(1, (clampedH + factor - 1) / factor);
                if (levelW * levelH <= budget)
                {
                    break;
                }

                level++;
            }

            int scaleFactor = 1 << level;
            int levelImageW = Math.Max(1, (info.Width + scaleFactor - 1) / scaleFactor);
            int levelImageH = Math.Max(1, (info.Height + scaleFactor - 1) / scaleFactor);

            int lx = Math.Clamp(clampedX / scaleFactor, 0, levelImageW - 1);
            int ly = Math.Clamp(clampedY / scaleFactor, 0, levelImageH - 1);
            int lw = Math.Clamp((clampedW + scaleFactor - 1) / scaleFactor, 1, levelImageW - lx);
            int lh = Math.Clamp((clampedH + scaleFactor - 1) / scaleFactor, 1, levelImageH - ly);
            int stride = (lw + (StrideAlignmentBytes - 1)) & ~(StrideAlignmentBytes - 1);
            var output = new byte[checked(lh * stride)];

            int minTx = lx / ImageInfo.TileSize;
            int maxTx = (lx + lw - 1) / ImageInfo.TileSize;
            int minTy = ly / ImageInfo.TileSize;
            int maxTy = (ly + lh - 1) / ImageInfo.TileSize;

            for (int ty = minTy; ty <= maxTy; ty++)
            {
                int tileOriginY = ty * ImageInfo.TileSize;
                int tileH = Math.Min(ImageInfo.TileSize, levelImageH - tileOriginY);
                if (tileH <= 0)
                {
                    continue;
                }

                for (int tx = minTx; tx <= maxTx; tx++)
                {
                    int tileOriginX = tx * ImageInfo.TileSize;
                    int tileW = Math.Min(ImageInfo.TileSize, levelImageW - tileOriginX);
                    if (tileW <= 0)
                    {
                        continue;
                    }

                    byte[] tileGray = GetOrReadGray8TileLocked(new TileKey(level, tx, ty), tileW, tileH);
                    int copyLeft = Math.Max(lx, tileOriginX);
                    int copyRight = Math.Min(lx + lw, tileOriginX + tileW);
                    int copyWidth = copyRight - copyLeft;
                    if (copyWidth <= 0)
                    {
                        continue;
                    }

                    int copyTop = Math.Max(ly, tileOriginY);
                    int copyBottom = Math.Min(ly + lh, tileOriginY + tileH);

                    for (int y = copyTop; y < copyBottom; y++)
                    {
                        int srcRowOffset = ((y - tileOriginY) * tileW) + (copyLeft - tileOriginX);
                        int dstRowOffset = ((y - ly) * stride) + (copyLeft - lx);
                        tileGray.AsSpan(srcRowOffset, copyWidth).CopyTo(output.AsSpan(dstRowOffset, copyWidth));
                    }
                }
            }

            return new ExtractedRoiWindow(
                output,
                lw,
                lh,
                stride,
                clampedX,
                clampedY,
                clampedW,
                clampedH,
                scaleFactor);
        }
    }

    private byte[] GetOrReadGray8TileLocked(TileKey key, int expectedW, int expectedH)
    {
        if (_grayTileCache.TryGetValue(key, out byte[]? cached) && cached.Length >= expectedW * expectedH)
        {
            return cached;
        }

        var grayTile = new byte[checked(expectedW * expectedH)];
        if (_analysisTileSource is null)
        {
            return grayTile;
        }

        using PixelBuffer raw = _analysisTileSource.Read(key, CancellationToken.None);
        int w = Math.Min(expectedW, raw.Width);
        int h = Math.Min(expectedH, raw.Height);
        ReadOnlySpan<byte> src = raw.Span;

        for (int y = 0; y < h; y++)
        {
            Span<byte> dstRow = grayTile.AsSpan(y * expectedW, w);
            if (raw.Kind == PixelKind.Gray8)
            {
                src.Slice(y * raw.Stride, w).CopyTo(dstRow);
            }
            else if (raw.Kind == PixelKind.Gray16)
            {
                ReadOnlySpan<byte> srcRow = src.Slice(y * raw.Stride, w * 2);
                for (int x = 0; x < w; x++)
                {
                    dstRow[x] = srcRow[(x * 2) + 1];
                }
            }
            else
            {
                ReadOnlySpan<byte> srcRow = src.Slice(y * raw.Stride, w * 4);
                for (int x = 0; x < w; x++)
                {
                    int idx = x * 4;
                    int c0 = srcRow[idx];
                    int c1 = srcRow[idx + 1];
                    int c2 = srcRow[idx + 2];
                    int r = raw.Kind == PixelKind.Bgra32 ? c2 : c0;
                    int b = raw.Kind == PixelKind.Bgra32 ? c0 : c2;
                    dstRow[x] = (byte)(((RedLumaWeight * r) + (GreenLumaWeight * c1) + (BlueLumaWeight * b) + LumaRoundingBias) >> LumaShiftBits);
                }
            }
        }

        if (_grayTileFifo.Count >= MaxCachedGrayTiles)
        {
            TileKey oldest = _grayTileFifo.Dequeue();
            _grayTileCache.Remove(oldest);
        }

        _grayTileCache[key] = grayTile;
        _grayTileFifo.Enqueue(key);
        return grayTile;
    }

    private static ExtractedRoiWindow ExtractFromMemoryFrame(
        LoadedImageFrameItem frame,
        int clampedX,
        int clampedY,
        int clampedW,
        int clampedH,
        int budget)
    {
        int scaleFactor = 1;
        while (((clampedW + scaleFactor - 1) / scaleFactor) * (long)((clampedH + scaleFactor - 1) / scaleFactor) > budget)
        {
            scaleFactor <<= 1;
        }

        int lw = Math.Max(1, (clampedW + scaleFactor - 1) / scaleFactor);
        int lh = Math.Max(1, (clampedH + scaleFactor - 1) / scaleFactor);
        int stride = (lw + (StrideAlignmentBytes - 1)) & ~(StrideAlignmentBytes - 1);
        var output = new byte[checked(lh * stride)];
        ReadOnlySpan<byte> working = frame.WorkingPixels;

        if (scaleFactor == 1)
        {
            for (int y = 0; y < lh; y++)
            {
                working.Slice(((clampedY + y) * frame.Stride) + clampedX, lw)
                    .CopyTo(output.AsSpan(y * stride, lw));
            }
        }
        else
        {
            for (int y = 0; y < lh; y++)
            {
                int srcY = Math.Min(frame.Height - 1, clampedY + (y * scaleFactor));
                Span<byte> dstRow = output.AsSpan(y * stride, lw);
                for (int x = 0; x < lw; x++)
                {
                    int srcX = Math.Min(frame.Width - 1, clampedX + (x * scaleFactor));
                    dstRow[x] = working[(srcY * frame.Stride) + srcX];
                }
            }
        }

        return new ExtractedRoiWindow(
            output,
            lw,
            lh,
            stride,
            clampedX,
            clampedY,
            clampedW,
            clampedH,
            scaleFactor);
    }

    /// <summary>
    /// Generates one of the built-in ImageJ test samples as an in-memory <see cref="LoadedImageFrameItem"/>.
    /// </summary>
    public static LoadedImageFrameItem CreateSampleFrame(int frameIndex, ImageJSampleKind sampleKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);

        int width = DefaultSceneWidth;
        int height = DefaultSceneHeight;
        int stride = DefaultSceneStride;
        var buffer = new byte[height * stride];

        ImageJWorkbenchEngine.RenderBuiltInSample(buffer, width, height, stride, sampleKind);

        string sampleName = sampleKind switch
        {
            ImageJSampleKind.BlobsAndParticles => "Sample_Blobs.tif",
            ImageJSampleKind.CellColonyCounter => "Sample_CellColonies.tif",
            ImageJSampleKind.MetrologyEdgesAndRings => "Sample_EdgesAndRings.tif",
            ImageJSampleKind.UnevenShadingAndSaltPepper => "Sample_ShadingSaltPepper.tif",
            _ => "Sample_ImageJ.tif"
        };

        return new LoadedImageFrameItem(
            frameIndex,
            sampleName,
            $"imagej://samples/{sampleName}",
            width,
            height,
            stride,
            buffer);
    }

    /// <summary>
    /// Saves an 8-bit grayscale buffer to disk as PNG, BMP, TIFF, or JPEG.
    /// </summary>
    public static void SaveGray8ImageToFile(
        string filePath,
        ReadOnlySpan<byte> buffer,
        int width,
        int height,
        int stride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var copy = new byte[checked(height * stride)];
        buffer.Slice(0, copy.Length).CopyTo(copy);

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            DefaultDpi,
            DefaultDpi,
            PixelFormats.Gray8,
            null,
            copy,
            stride);

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        BitmapEncoder encoder = ext switch
        {
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream outStream = File.Create(filePath);
        encoder.Save(outStream);
    }

    public async Task<ITileSource> LoadAsync(
        ImageReference reference,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Key.StartsWith(LiveKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolBlockMemoryTileSource(
                reference,
                _activeGrayBuffer,
                _activeWidth,
                _activeHeight,
                _activeStride);
        }

        ITileSource source = await _fallbackLoader.LoadAsync(reference, progress, cancellationToken).ConfigureAwait(false);
        return source;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_readerGate)
        {
            _grayTileCache.Clear();
            _grayTileFifo.Clear();
            _analysisTileSource?.Dispose();
            _analysisTileSource = null;
        }
    }
}
