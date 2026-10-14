using PyramidComparison;
using VisionImaging.Codecs;
using VisionImaging.Pyramid;

if (args is ["decode-probe", var probeFile, var budgetMb, var cancelMs, var report])
{
    return DecoderProbe.Run(probeFile, long.Parse(budgetMb, System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024,
        int.Parse(cancelMs, System.Globalization.CultureInfo.InvariantCulture), report);
}
if (args is ["open-shared", var sharedFile, var sharedCache, var sharedReport])
{
    using var source = RawPyramidSource.Open(sharedFile, sharedCache, new NativeRasterDecoder(), new Progress<string>(), default);
    using var pixels = source.Read(new(0, 0, 0), default);
    File.WriteAllText(sharedReport, System.Text.Json.JsonSerializer.Serialize(new { Status = "PASS", source.Info, FirstPixel = pixels.Bytes[0] }));
    return 0;
}
if (args is ["inspect", var file, var cache, var output])
{
    return MemoryInspection.Run("Own/Streaming", file, cache, output,
        (path, cacheRoot, record) => RawPyramidSource.Open(path, cacheRoot,
            new ObservedDecoder(new NativeRasterDecoder(), record), new MemoryInspection.Progress(record), CancellationToken.None));
}
return Measurement.Run("Own/Streaming", args,
    (path, cache) => RawPyramidSource.Open(path, cache, new NativeRasterDecoder(), new Progress<string>(), CancellationToken.None),
    source => new { ((RawPyramidSource)source).PreparationBufferPeakBytes });
