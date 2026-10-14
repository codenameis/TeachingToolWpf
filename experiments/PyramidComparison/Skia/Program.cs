using VisionImaging;
using PyramidComparison;
using VisionImaging.Pyramid;

if (args.Length == 4 && args[0] == "compare") return PixelComparison.Run(args[1], args[2], args[3]);
return Measurement.Run("Own/Skia-jpeg-scanline", args,
    (path, cache) => RawPyramidSource.Open(path, cache, new SkiaFileDecoder(), new Progress<string>(), CancellationToken.None),
    source => new { ((RawPyramidSource)source).PreparationBufferPeakBytes });
