using PyramidComparison;
using VisionImaging.OpenCv;
using VisionImaging.Pyramid;

if (args is ["inspect", var file, var cache, var output])
{
    return MemoryInspection.Run("Own/OpenCV", file, cache, output,
        (path, cacheRoot, record) => RawPyramidSource.Open(path, cacheRoot,
            new ObservedDecoder(new OpenCvRasterDecoder(), record), new MemoryInspection.Progress(record), CancellationToken.None));
}
return Measurement.Run("Own/OpenCV", args,
    (path, cache) => RawPyramidSource.Open(path, cache, new OpenCvRasterDecoder(), new Progress<string>(), CancellationToken.None));
