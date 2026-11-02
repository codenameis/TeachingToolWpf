using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCompute.ILGPU;
using VisionImaging;
using VisionImaging.Abstractions;

if (args.Contains("--poses"))
{
    PoseSnapshot.Run();
    return;
}

var size = args.Length > 0 ? int.Parse(args[0]) : 32768;
var limit = args.Length > 1 ? int.Parse(args[1]) : 0;
var repetitions = args.Length > 2 ? int.Parse(args[2]) : 3;
var useGpu = args.Contains("--gpu");
var hollow = args.Contains("--hollow");
var pcb = args.Contains("--pcb");
var mixed = args.Contains("--mixed") || hollow || pcb;
var file = args.Contains("--ken32");
var prefilled = args.Contains("--prefill");
var loader = new GridLoader(size, mixed, hollow, pcb);
IImageLoader sourceLoader = file
    ? new ImageLoader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachingToolWpf", "ImageCache"))
    : loader;
if (prefilled)
{
    var preparation = Stopwatch.StartNew();
    sourceLoader = await PrefilledLoader.CreateAsync(sourceLoader,
        file ? new ImageReference(Path.GetFullPath("ken32.png"), "ken32", 17280, 30720)
            : new ImageReference("grid", "procedural shapes", size, size));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        preparationMilliseconds = preparation.Elapsed.TotalMilliseconds
    }));
}
var timedLoader = new TimedLoader(sourceLoader);
var service = new PrimitiveShapeDetectionService(timedLoader, () => null, () => new CudaPrimitivePreprocessor());
var reference = file ? new ImageReference(Path.GetFullPath("ken32.png"), "ken32", 17280, 30720)
    : new ImageReference("grid", "procedural shapes", size, size);
var options = new PrimitiveDetectionOptions
{
    MaximumResults = limit,
    UseGpu = useGpu,
    MaximumParallelism = args.Contains("--serial") ? 1 : 4
};
if (args.Contains("--preview"))
{
    if (size > 2048)
    {
        throw new ArgumentException("Preview must be at most 2048px.");
    }

    using var source = await loader.LoadAsync(reference, new Progress<string>(), default);
    var pixels = new byte[size * size];
    for (var y = 0; y < (size + 511) / 512; y++)
    {
        for (var x = 0; x < (size + 511) / 512; x++)
        {
            using var tile = source.Read(new(0, x, y), default);
            for (var row = 0; row < tile.Height; row++)
            {
                tile.Span.Slice(row * tile.Stride, tile.Width).CopyTo(pixels.AsSpan((((y * 512) + row) * size) + (x * 512)));
            }
        }
    }

    VisionImaging.Fixtures.ImageFixtureWriter.Write(Path.GetFullPath("artifacts/pcb-marks-preview.png"), size, size, 1, 8, pixels);
}
for (var run = 0; run <= repetitions; run++)
{
    loader.ReadTicks = 0;
    loader.Reads = 0;
    timedLoader.ReadTicks = 0;
    timedLoader.Reads = 0;
    var allocated = GC.GetTotalAllocatedBytes();
    var requestTimer = Stopwatch.StartNew();
    var result = await service.DetectAsync(reference, false, null, options, default);
    var requestMilliseconds = requestTimer.Elapsed.TotalMilliseconds;
    var detectionAllocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
    using var data = new MemoryStream();
    using var writer = new BinaryWriter(data);
    var maxError = 0d;
    var duplicateCount = 0;
    var kindErrors = 0;
    var maxAngleErrorDegrees = 0d;
    var cells = new HashSet<(int, int, PrimitiveShapeKind)>();
    foreach (var shape in result.Shapes.OrderBy(shape => shape.Center.Y).ThenBy(shape => shape.Center.X))
    {
        var cellX = (int)shape.Center.X / GridLoader.Pitch;
        var cellY = (int)shape.Center.Y / GridLoader.Pitch;
        if (!file)
        {
            var expected = GridLoader.Center(cellX, cellY, size, mixed);
            maxError = Math.Max(maxError, Math.Max(Math.Abs(shape.Center.X - expected.X), Math.Abs(shape.Center.Y - expected.Y)));
            duplicateCount += cells.Add((cellX, cellY, shape.Kind)) ? 0 : 1;
            var kind = mixed ? GridLoader.Kind(cellX, cellY, hollow, pcb) : PrimitiveShapeKind.Rectangle;
            if (hollow && shape.InnerContours.IsEmpty)
            {
                kind = kind == PrimitiveShapeKind.Ring ? PrimitiveShapeKind.Circle : PrimitiveShapeKind.Rectangle;
            }
            kindErrors += shape.Kind == kind ? 0 : 1;
            if (pcb || kind is PrimitiveShapeKind.Rectangle or PrimitiveShapeKind.Cross or PrimitiveShapeKind.RectangleFrame)
            {
                var expectedAngle = mixed ? GridLoader.Angle(cellX, cellY) : 0;
                var period = kind switch
                {
                    PrimitiveShapeKind.Diamond or PrimitiveShapeKind.DoubleSquare => 180,
                    PrimitiveShapeKind.Star => 72,
                    PrimitiveShapeKind.Asterisk => GridLoader.Rays(cellX, cellY) == 8 ? 45 : 60,
                    _ => 90
                };
                maxAngleErrorDegrees = Math.Max(maxAngleErrorDegrees, Math.Abs(Math.IEEERemainder(
                    (shape.AngleRadians * 180 / Math.PI) - expectedAngle, period)));
            }
        }
        writer.Write((int)shape.Kind);
        writer.Write(shape.Center.X);
        writer.Write(shape.Center.Y);
        writer.Write(shape.Score);
        writer.Write(shape.AreaPixels);
        writer.Write(shape.AngleRadians);
        writer.Write(shape.Contour.Length);
        foreach (var point in shape.Contour)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
        }
        writer.Write(shape.InnerContours.Length);
        foreach (var hole in shape.InnerContours)
        {
            writer.Write(hole.Length);
            foreach (var point in hole)
            {
                writer.Write(point.X);
                writer.Write(point.Y);
            }
        }
        writer.Write(shape.AdditionalContours.Length);
        foreach (var component in shape.AdditionalContours)
        {
            writer.Write(component.Length);
            foreach (var point in component)
            {
                writer.Write(point.X);
                writer.Write(point.Y);
            }
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        run,
        reference.Width,
        reference.Height,
        limit,
        useGpu,
        mixed,
        hollow,
        pcb,
        file,
        prefilled,
        options.MaximumParallelism,
        expected = file ? (int?)null : size / GridLoader.Pitch * (size / GridLoader.Pitch) * (hollow ? 2 : 1),
        found = result.Shapes.Length,
        result.Incomplete,
        result.ClippedContours,
        result.Windows,
        result.Milliseconds,
        requestMilliseconds,
        tileReadMs = loader.ReadTicks * 1000d / Stopwatch.Frequency,
        loader.Reads,
        result.ReadMilliseconds,
        result.DetectionWorkMilliseconds,
        result.DetectionMilliseconds,
        result.ComputeInitializationMilliseconds,
        result.Gpu,
        sourceReadMs = timedLoader.ReadTicks * 1000d / Stopwatch.Frequency,
        sourceReads = timedLoader.Reads,
        timedLoader.Kind,
        maxError = file ? (double?)null : maxError,
        duplicateCount = file ? (int?)null : duplicateCount,
        kindErrors = file ? (int?)null : kindErrors,
        maxAngleErrorDegrees = file ? (double?)null : maxAngleErrorDegrees,
        counts = result.Shapes.GroupBy(shape => shape.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        missingByKindAndAngle = file ? null : Enumerable.Range(0, size / GridLoader.Pitch).SelectMany(y =>
            Enumerable.Range(0, size / GridLoader.Pitch).SelectMany(x =>
            {
                var kind = mixed ? GridLoader.Kind(x, y, hollow, pcb) : PrimitiveShapeKind.Rectangle;
                var kinds = hollow ? new[] { kind, kind == PrimitiveShapeKind.Ring ? PrimitiveShapeKind.Circle : PrimitiveShapeKind.Rectangle } : [kind];
                return kinds.Where(expectedKind => !cells.Contains((x, y, expectedKind)))
                    .Select(expectedKind => $"{expectedKind}@{(mixed ? GridLoader.Angle(x, y) : 0)}");
            })).GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count()),
        hash = Convert.ToHexString(SHA256.HashData(data.ToArray())),
        allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
        detectionAllocatedBytes,
        peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
    }));
}

internal sealed class TimedLoader(IImageLoader loader) : IImageLoader
{
    public long ReadTicks;
    public int Reads;
    public PixelKind Kind;

    public async Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken token)
    {
        var source = await loader.LoadAsync(image, progress, token);
        Kind = source.Info.Pixels;
        return source is IOriginalGrayTileSource direct ? new DirectSource(this, source, direct) : new Source(this, source);
    }

    private class Source(TimedLoader owner, ITileSource source) : ITileSource
    {
        protected TimedLoader Owner { get; } = owner;
        public ImageInfo Info => source.Info;

        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            var start = Stopwatch.GetTimestamp();
            var result = source.Read(key, token);
            Owner.ReadTicks += Stopwatch.GetTimestamp() - start;
            Owner.Reads++;
            return result;
        }

        public void Dispose() => source.Dispose();
    }

    private sealed class DirectSource(TimedLoader owner, ITileSource source, IOriginalGrayTileSource direct)
        : Source(owner, source), IOriginalGrayTileSource
    {
        public PixelBuffer ReadOriginalGray8(int tileX, int tileY, CancellationToken token)
        {
            var start = Stopwatch.GetTimestamp();
            var result = direct.ReadOriginalGray8(tileX, tileY, token);
            Owner.ReadTicks += Stopwatch.GetTimestamp() - start;
            Owner.Reads++;
            return result;
        }
    }
}

internal sealed class GridLoader(int size, bool mixed, bool hollow, bool pcb) : IImageLoader
{
    public const int Pitch = 256;
    public long ReadTicks;
    public int Reads;

    // Deliberately vary location; several centers cross the service's 768px ownership seams.
    public static (int X, int Y) Center(int x, int y, int size, bool mixed)
    {
        var centerX = (x * Pitch) + 48 + (((x * 31) + (y * 17)) % 160);
        var centerY = (y * Pitch) + 48 + (((x * 13) + (y * 43)) % 160);
        return mixed ? (Math.Clamp(centerX, 72, size - 72), Math.Clamp(centerY, 72, size - 72)) : (centerX, centerY);
    }

    public static PrimitiveShapeKind Kind(int x, int y, bool hollow, bool pcb) => pcb
        ? (((x + y) % 6) switch
        {
            0 => PrimitiveShapeKind.Diamond,
            1 => PrimitiveShapeKind.CrossHash,
            2 => PrimitiveShapeKind.DoubleSquare,
            3 => PrimitiveShapeKind.Star,
            _ => PrimitiveShapeKind.Asterisk
        })
        : hollow
        ? ((x + (y * 3)) % 3 == 0 ? PrimitiveShapeKind.Ring : PrimitiveShapeKind.RectangleFrame)
        : (PrimitiveShapeKind)(1 << ((x + y) % 4));
    public static double Angle(int x, int y) => ((x * 17) + (y * 23)) % 90;
    public static int Rays(int x, int y) => (x + y) % 6 == 5 ? 8 : 6;

    public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken token)
        => Task.FromResult<ITileSource>(new Source(this, size, mixed, hollow, pcb));

    private sealed class Source(GridLoader owner, int size, bool mixed, bool hollow, bool pcb) : ITileSource
    {
        public ImageInfo Info { get; } = new("grid", "grid", size, size, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            var left = key.X * 512;
            var top = key.Y * 512;
            var buffer = new PixelBuffer(Math.Min(512, size - left), Math.Min(512, size - top), PixelKind.Gray8);
            buffer.Span.Fill(25);
            for (var cy = Math.Max(0, (top / Pitch) - 1); cy < Math.Min(size / Pitch, ((top + buffer.Height) / Pitch) + 1); cy++)
            {
                for (var cx = Math.Max(0, (left / Pitch) - 1); cx < Math.Min(size / Pitch, ((left + buffer.Width) / Pitch) + 1); cx++)
                {
                    var center = Center(cx, cy, size, mixed);
                    if (mixed)
                    {
                        DrawShape(buffer, left, top, cx, cy, size, hollow, pcb);
                        continue;
                    }
                    var first = Math.Max(left, center.X - 30);
                    var last = Math.Min(left + buffer.Width, center.X + 30);
                    for (var y = Math.Max(top, center.Y - 20); first < last && y < Math.Min(top + buffer.Height, center.Y + 20); y++)
                    {
                        buffer.Span.Slice(((y - top) * buffer.Stride) + first - left, last - first).Fill(225);
                    }
                }
            }
            owner.ReadTicks += Stopwatch.GetTimestamp() - start;
            owner.Reads++;
            return buffer;
        }

        public void Dispose()
        {
        }

        private static void DrawShape(PixelBuffer buffer, int left, int top, int cellX, int cellY, int size, bool hollow, bool pcb)
        {
            var center = Center(cellX, cellY, size, true);
            var kind = Kind(cellX, cellY, hollow, pcb);
            var angle = Angle(cellX, cellY) * Math.PI / 180;
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            for (var y = Math.Max(top, center.Y - 72); y < Math.Min(top + buffer.Height, center.Y + 72); y++)
            {
                for (var x = Math.Max(left, center.X - 72); x < Math.Min(left + buffer.Width, center.X + 72); x++)
                {
                    var dx = x + .5 - center.X;
                    var dy = y + .5 - center.Y;
                    var localX = (cosine * dx) + (sine * dy);
                    var localY = (-sine * dx) + (cosine * dy);
                    var inside = kind switch
                    {
                        PrimitiveShapeKind.Circle => (localX * localX) + (localY * localY) <= 1600,
                        PrimitiveShapeKind.Ring => (localX * localX) + (localY * localY) is <= 256 or (>= 625 and <= 2500),
                        PrimitiveShapeKind.RectangleFrame => Math.Abs(localX) <= 48 && Math.Abs(localY) <= 36 &&
                            (Math.Abs(localX) >= 30 || Math.Abs(localY) >= 20 || (Math.Abs(localX) <= 16 && Math.Abs(localY) <= 11)),
                        PrimitiveShapeKind.Rectangle => Math.Abs(localX) <= 48 && Math.Abs(localY) <= 30,
                        PrimitiveShapeKind.Cross => (Math.Abs(localX) <= 14 && Math.Abs(localY) <= 48) ||
                            (Math.Abs(localX) <= 48 && Math.Abs(localY) <= 14),
                        PrimitiveShapeKind.Triangle => localY >= -30 && localY <= 60 && Math.Abs(localX) <= (60 - localY) / 2,
                        _ => pcb && VisionAlgorithm.Tests.PcbMarkFixture.Contains(kind, localX, localY, Rays(cellX, cellY))
                    };
                    if (inside)
                    {
                        buffer.Span[((y - top) * buffer.Stride) + x - left] = 225;
                    }
                }
            }
        }
    }
}
