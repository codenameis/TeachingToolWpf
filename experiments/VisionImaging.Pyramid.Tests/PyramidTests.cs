using System.Buffers.Binary;
using VisionImaging.Abstractions;

namespace VisionImaging.Pyramid.Tests;

public sealed class PyramidTests
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly IProgress<string> _progress = new Progress<string>();

    [Theory]
    [InlineData(PixelKind.Gray8, 4194304, 9)]
    [InlineData(PixelKind.Gray16, 2097152, 9)]
    [InlineData(PixelKind.Rgba32, 1048576, 9)]
    [InlineData(PixelKind.Gray8, 524289, 65)]
    [InlineData(PixelKind.Gray16, 262145, 65)]
    [InlineData(PixelKind.Rgba32, 131073, 65)]
    public void WideRowsStayWithinRentedWorkingBufferBudget(PixelKind kind, int width, int height)
    {
        var input = Input();
        var decoder = new PatternDecoder(width, height, kind);
        using (var source = Open(input, decoder))
        {
            Assert.InRange(source.PreparationBufferPeakBytes, 1, 64L * 1024 * 1024);
            var lastColumn = (width - 1) / 512;
            using var edge = source.Read(new(0, lastColumn, 0), default);
            var expected = new byte[edge.Length];
            PatternDecoder.Fill(lastColumn * 512, 0, edge.Width, edge.Height, kind, expected);
            Assert.Equal(expected, edge.Span.ToArray());
        }
        decoder.Fail = true;
        using var reopened = Open(input, decoder);
        Assert.Equal(0, reopened.PreparationBufferPeakBytes);
    }

    [Fact]
    public async Task ConcurrentImportsDecodeOnceAndPublishOneCompleteCache()
    {
        var input = Input();
        var decoder = new PatternDecoder(1024, 1024, PixelKind.Gray8);
        var operations = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var source = Open(input, decoder);
            using var tile = source.Read(new(0, 0, 0), CancellationToken.None);
            Assert.Equal(512, tile.Width);
        }));
        await Task.WhenAll(operations);
        Assert.Equal(1, decoder.Opens);
        Assert.Single(Directory.EnumerateFiles(Cache, "image.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void TileReadsCrossMappingWindowsInBothDirections()
    {
        using var source = Open(Input(), new PatternDecoder(24576, 521, PixelKind.Gray16));
        foreach (var key in new TileKey[] { new(0, 47, 1), new(0, 0, 0), new(0, 23, 1), new(0, 47, 0) })
        {
            using var tile = source.Read(key, CancellationToken.None);
            var expected = new byte[tile.Length];
            PatternDecoder.Fill(key.X * 512, key.Y * 512, tile.Width, tile.Height, PixelKind.Gray16, expected);
            Assert.Equal(expected, tile.Span.ToArray());
        }
    }

    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    public void OriginalAndOddEdgePixelsSurviveReopenWithoutDecoder(PixelKind kind)
    {
        var input = Input();
        var decoder = new PatternDecoder(1031, 519, kind);
        using (var first = Open(input, decoder))
        {
            using var corner = first.Read(new(0, 2, 1), CancellationToken.None);
            Assert.Equal(7, corner.Width);
            Assert.Equal(7, corner.Height);
            var expected = new byte[corner.Length];
            PatternDecoder.Fill(1024, 512, 7, 7, kind, expected);
            Assert.Equal(expected, corner.Span.ToArray());
        }
        decoder.Fail = true;
        using var reopened = Open(input, decoder);
        using var pixels = reopened.Read(new(1, 1, 0), CancellationToken.None);
        Assert.Equal(4, pixels.Width);
        Assert.Equal(260, pixels.Height);
        Assert.Equal(1, decoder.Opens);
    }

    [Theory]
    [InlineData(PixelKind.Gray8, 123)]
    [InlineData(PixelKind.Gray16, 49151)]
    public void ConstantSurvivesEveryPyramidLevel(PixelKind kind, int value)
    {
        using var source = Open(Input(), new PatternDecoder(1025, 1, kind) { Constant = value });
        for (var level = 0; level < source.Info.Levels; level++)
        {
            using var tile = source.Read(new(level, 0, 0), CancellationToken.None);
            for (var x = 0; x < tile.Width; x++)
            {
                Assert.Equal(value, kind == PixelKind.Gray8 ? tile.Bytes[x] : BinaryPrimitives.ReadUInt16LittleEndian(tile.Span.Slice(x * 2, 2)));
            }
        }
    }

    [Theory]
    [InlineData(1024, 1, 23)]
    [InlineData(1025, 1, 9)]
    [InlineData(1, 1024, 8)]
    [InlineData(1, 1025, 3)]
    public void ShrinkAlignsSampleCenterToRoundedOutputSize(int width, int height, int expected)
    {
        using var source = Open(Input(), new PatternDecoder(width, height, PixelKind.Gray8));
        using var tile = source.Read(new(1, 0, 0), CancellationToken.None);
        Assert.Equal(expected, tile.Bytes[0]);
    }

    [Fact]
    public void CancellationAfterDecodeDoesNotPublishCacheAndReleasesReader()
    {
        var input = Input();
        using var cancellation = new CancellationTokenSource();
        var decoder = new PatternDecoder(1024, 1024, PixelKind.Gray8) { AfterRead = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => RawPyramidSource.Open(input, Cache, decoder, _progress, cancellation.Token));
        Assert.True(decoder.Disposed);
        Assert.Empty(Directory.EnumerateFiles(Cache, "image.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Cache, "*.partial"));
        using var retry = Open(input, new PatternDecoder(1024, 1024, PixelKind.Gray8));
        Assert.Single(Directory.EnumerateFiles(Cache, "image.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void TruncatedOwnedCacheIsRebuilt()
    {
        var input = Input();
        var decoder = new PatternDecoder(520, 520, PixelKind.Gray8);
        using (var source = Open(input, decoder))
        {
        }
        var level = Directory.EnumerateFiles(Cache, "0.raw", SearchOption.AllDirectories).Single();
        using (var stream = File.OpenWrite(level))
        {
            stream.SetLength(1);
        }
        using var restored = Open(input, decoder);
        using var pixels = restored.Read(new(0, 0, 0), default);
        Assert.Equal(512 * 512, pixels.Length);
        Assert.Equal(2, decoder.Opens);
    }

    [Fact]
    public void CacheBudgetPreservesActiveReaderAndEvictsAfterRelease()
    {
        var input = Input();
        var decoder = new PatternDecoder(32, 32, PixelKind.Gray8);
        var active = RawPyramidSource.Open(input, Cache, decoder, _progress, default, 20000);
        var second = Path.Combine(_root, "second.fixture");
        File.WriteAllText(second, "second");
        Assert.Throws<IOException>(() => RawPyramidSource.Open(second, Cache, decoder, _progress, default, 20000));
        using (var pixels = active.Read(new(0, 0, 0), default))
        {
            Assert.Equal(32, pixels.Width);
        }
        active.Dispose();
        using var next = RawPyramidSource.Open(second, Cache, decoder, _progress, default, 20000);
        Assert.Single(Directory.EnumerateFiles(Cache, "image.json", SearchOption.AllDirectories));
        Assert.Equal("test", File.ReadAllText(input));
    }

    [Fact]
    public void CacheRecoveryPreservesUnknownFilesAndRejectsDamagedEntry()
    {
        var input = Input();
        var decoder = new PatternDecoder(32, 32, PixelKind.Gray8);
        using (var source = Open(input, decoder)) { }
        var entry = Path.GetDirectoryName(Directory.EnumerateFiles(Cache, "image.json", SearchOption.AllDirectories).Single())!;
        File.WriteAllText(Path.Combine(entry, "user-file.txt"), "keep");
        File.WriteAllText(Path.Combine(entry, "image.json"), "damaged");
        Assert.Throws<InvalidDataException>(() => Open(input, decoder));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(entry, "user-file.txt")));
    }

    [Fact]
    public void InvalidTileCancellationAndDisposedSourceAreRejected()
    {
        var source = Open(Input(), new PatternDecoder(513, 513, PixelKind.Gray8));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(new(-1, 0, 0), CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(new(0, 2, 0), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => source.Read(new(0, 0, 0), new CancellationToken(true)));
        source.Dispose();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Read(new(0, 0, 0), CancellationToken.None));
    }

    [Fact]
    public void ChangedSourceHasSeparateCache()
    {
        var input = Input();
        var decoder = new PatternDecoder(16, 16, PixelKind.Gray8);
        using (var first = Open(input, decoder))
        {
        }
        File.AppendAllText(input, "different");
        using var second = Open(input, decoder);
        Assert.Equal(2, decoder.Opens);
    }

    [Fact]
    public void CommonEngineHasNoPlatformOrNativeDependency()
    {
        var names = typeof(RawPyramidSource).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.All(names, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "VisionImaging.Abstractions", name));
    }

    private string Cache => Path.Combine(_root, "cache");
    private RawPyramidSource Open(string input, PatternDecoder decoder) => RawPyramidSource.Open(input, Cache, decoder, _progress, CancellationToken.None);
    private string Input()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "image.fixture");
        File.WriteAllText(path, "test");
        return path;
    }

    private sealed class PatternDecoder(int width, int height, PixelKind kind) : IRasterDecoder, IRasterReader
    {
        public int Opens
        {
            get; private set;
        }
        public bool Fail
        {
            get; set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public int? Constant
        {
            get; init;
        }
        public Action? AfterRead
        {
            get; init;
        }
        public string CacheIdentity => "pattern-v1";
        public int Width => width;
        public int Height => height;
        public PixelKind Kind => kind;
        public IRasterReader Open(string path, CancellationToken token)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Decoder must not run on cache reopen.");
            }
            Opens++;
            return this;
        }
        public void ReadRows(int top, int rows, Span<byte> destination, CancellationToken token)
        {
            Fill(0, top, width, rows, kind, destination, Constant);
            AfterRead?.Invoke();
        }
        public void Dispose() => Disposed = true;
        public static void Fill(int left, int top, int width, int height, PixelKind kind, Span<byte> target, int? constant = null)
        {
            var size = kind == PixelKind.Gray16 ? 2 : kind == PixelKind.Gray8 ? 1 : 4;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = constant ?? (((left + x) * 37) + ((top + y) * 13));
                    var index = ((y * width) + x) * size;
                    if (kind == PixelKind.Gray16)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(index, 2), (ushort)value);
                    }
                    else
                    {
                        for (var channel = 0; channel < size; channel++)
                        {
                            target[index + channel] = (byte)(value + channel);
                        }
                    }
                }
            }
        }
    }
}
