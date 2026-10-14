using PyramidComparison;
using VisionImaging;


if (args is ["large-codec-fixtures", var largeCodecDirectory])
{
    CodecFixtures.CreateLarge(largeCodecDirectory);
    return 0;
}
if (args is ["diagnostic-fixtures", var root])
{
    var large = Path.Combine(root, "large-images");
    var memory = Path.Combine(root, "memory-fixtures");
    Directory.CreateDirectory(large);
    Directory.CreateDirectory(memory);
    foreach (var bits in new[] { 8, 16 })
    {
        var path = Path.Combine(large, $"24k-{bits}bit.tif");
        if (!File.Exists(path)) DiagnosticImageFixtures.CreateLarge(path, bits);
    }
    var rgba = Path.Combine(memory, "8k-rgba.png");
    var gray = Path.Combine(memory, "24k-gray16.tif");
    if (!File.Exists(rgba)) DiagnosticImageFixtures.CreateReplacement(rgba, 7680, 4320, false);
    if (!File.Exists(gray)) DiagnosticImageFixtures.CreateReplacement(gray, 24576, 4096, true);
    return 0;
}
if (args is ["codec-fixtures", var fixtureDirectory])
{
    CodecFixtures.Create(fixtureDirectory);
    return 0;
}
if (args is ["compare-streaming", var streamingFile, var streamingCache, var streamingOutput])
{
    throw new NotSupportedException("기존 NetVips 비교는 제거되었습니다. Compare-Skia.ps1로 독립 디코더를 비교하세요.");
}

if (args is ["inspect", var inspectedFile, var inspectedCache, var inspectedOutput])
{
    return MemoryInspection.Run("NativeReference", inspectedFile, inspectedCache, inspectedOutput,
        (path, cacheRoot, record) => DiskPyramidSource.Open(path, cacheRoot, new MemoryInspection.Progress(record), CancellationToken.None));
}
if (args is ["fixtures", var directory])
{
    Fixtures.Create(directory);
    return 0;
}
if (args is ["compare", var file, var cache, var output])
{
    return PixelComparison.Run(file, cache, output);
}
return Measurement.Run("NativeReference", args,
    (path, cache) => DiskPyramidSource.Open(path, cache, new Progress<string>(), CancellationToken.None));
