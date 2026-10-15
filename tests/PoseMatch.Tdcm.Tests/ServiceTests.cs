using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace PoseMatch.Tdcm.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task InjectedBackendIsReusedAndDeviceChangeReleasesPreviousMatcher()
    {
        var loader = new Loader();
        var instances = new List<FakeMatcher>();
        using var service = new TdcmMatchingService(loader, () => null, options =>
        {
            Assert.Equal(TdcmBackend.TorchSharp, options.Backend);
            var matcher = new FakeMatcher();
            instances.Add(matcher);
            return matcher;
        });
        var model = await service.TrainAsync(loader.Template, false, new(new(15.5, 16.5), 31, 33, 0), default);
        var options = new TdcmSearchOptions { Backend = TdcmBackend.TorchSharp };
        await service.SearchAsync(loader.Scene, false, model, null, options, default);
        await service.SearchAsync(loader.Scene, false, model, null, options, default);
        Assert.Single(instances);
        await service.SearchAsync(loader.Scene, false, model, null, options with { Device = TdcmDevice.Cuda }, default);
        Assert.Equal(2, instances.Count);
        Assert.True(instances[0].Disposed);
        service.Dispose();
        Assert.True(instances[1].Disposed);
    }

    private sealed class FakeMatcher : ITdcmMatcher
    {
        public bool Disposed { get; private set; }
        public Prediction Predict(PixelImage image, PixelImage template, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public MatchOutput Find(PixelImage image, PixelImage template, MatchOptions? options = null, CancellationToken cancellationToken = default)
            => new([], false, false);
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task TrainingReductionPreservesThinLineArea(double angle)
    {
        // 각 4픽셀 중 한 줄만 밝다. 점 샘플링은 이 선을 모두 놓친다.
        var data = new byte[144 * 144 * 3];
        for (var y = 0; y < 144; y++)
        {
            for (var x = 0; x < 144; x += 4)
            {
                data.AsSpan((y * 144 + x) * 3, 3).Fill(255);
            }
        }
        var loader = new Loader(data, 144, 144);
        using var service = new TdcmMatchingService(loader, () => null);
        var trained = await service.TrainAsync(loader.Template, false, new(new(72, 72), 144, 144, angle), default);
        Assert.All(trained.Pixels.Pixels.ToArray(), value => Assert.InRange(value, (byte)63, (byte)64));
    }

    [Fact]
    public async Task ReusedSessionProducesSameMatchesAndDisposedServiceRejectsWork()
    {
        var loader = new Loader();
        using var service = new TdcmMatchingService(loader, () => null);
        var model = await service.TrainAsync(loader.Template, false, new(new(15.5, 16.5), 31, 33, 0), default);
        var first = await service.SearchAsync(loader.Scene, false, model, null, new(), default);
        var second = await service.SearchAsync(loader.Scene, false, model, null, new(), default);
        Assert.Equal(first.Result.Matches, second.Result.Matches);
        Assert.Equal(3, loader.Disposals); // 읽기/영상 결과는 캐시하지 않는다.
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SearchAsync(loader.Scene, false, model, null, new(), default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TrainAsync(loader.Template, false, model.TeachingRegion, default));
    }

    [Theory]
    [InlineData(338)]
    [InlineData(340)]
    public async Task TargetAcrossTileOwnershipBoundaryIsNotDropped(int offset)
    {
        var loader = new Loader(offset);
        using var service = new TdcmMatchingService(loader, () => null);
        var model = await service.TrainAsync(loader.Template, false, new(new(15.5, 16.5), 31, 33, 0), default);
        var result = await service.SearchAsync(loader.Scene with
        {
            Width = 900
        }, false, model, null, new(), default);
        Assert.Contains(result.Result.Matches, match => Math.Abs(match.X - (45 + offset)) < 5 && Math.Abs(match.Y - 95) < 5);
        Assert.False(result.Result.CandidatesTruncated);
    }

    [Fact]
    public async Task OriginalReaderTrainingAndSearchRestoreCoordinatesAndReleaseSources()
    {
        var loader = new Loader();
        using var service = new TdcmMatchingService(loader, () => null);
        var model = await service.TrainAsync(loader.Template, false, new(new(15.5, 16.5), 31, 33, 0), default);
        var result = await service.SearchAsync(loader.Scene, false, model, null, new(), default);
        Assert.Equal(1, result.Windows);
        Assert.False(result.Result.CandidatesTruncated);
        Assert.Contains(result.Result.Matches, match => Math.Abs(match.X - 45) < 4 && Math.Abs(match.Y - 95) < 4);
        Assert.Equal(2, loader.Disposals);
        var limited = await service.SearchAsync(loader.Scene with
        {
            Width = 900
        }, false, model, null,
            new()
            {
                MaximumWindows = 1
            }, default);
        Assert.True(limited.Result.CandidatesTruncated);
        Assert.Equal(1, limited.Windows);
        Assert.Equal(3, loader.Disposals);
    }

    [Fact]
    public async Task LargeRotatedTemplateNormalizesWithoutOversizedInferenceAndMasksAreRejected()
    {
        var loader = new Loader();
        using var service = new TdcmMatchingService(loader, () => null);
        var image = loader.Scene with
        {
            Width = 4096,
            Height = 4096
        };
        var roi = new RoiGeometry(new(2048, 2048), 2048, 2048, 30);
        var model = await service.TrainAsync(image, false, roi, default);
        Assert.Equal(32, model.Reduction);
        Assert.Equal(36, model.Pixels.Width);
        Assert.Equal(36, model.Pixels.Height);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.TrainAsync(image, false, roi, default,
            new(roi, [new(MaskShape.Rectangle, new(0, 0, .5, .5))])));
        Assert.Equal(1, loader.Disposals);
    }

    private sealed class Loader : IImageLoader
    {
        private readonly int _offset;
        private readonly byte[] _image;
        private readonly byte[] _template;
        private readonly int _templateWidth = 31;
        public int Disposals;
        public ImageReference Template { get; } = new("template", "template", 31, 33);
        public ImageReference Scene { get; } = new("scene", "scene", 153, 157);
        public Loader(int offset = 0)
        {
            _offset = offset;
            var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            _image = File.ReadAllBytes(Path.Combine(folder, "image.bgr"));
            _template = File.ReadAllBytes(Path.Combine(folder, "template.bgr"));
        }
        public Loader(byte[] template, int width, int height)
        {
            _template = template;
            _image = template;
            _templateWidth = width;
            Template = new("template", "template", width, height);
        }
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(this, reference, reference.Key == "template" ? _template : _image, reference.Key == "template" ? _templateWidth : 152, reference.Key == "template" ? 0 : _offset));
    }
    private sealed class Source(Loader owner, ImageReference reference, byte[] data, int width, int offset) : ITileSource
    {
        public ImageInfo Info { get; } = new(reference.Key, reference.Name, reference.Width, reference.Height, PixelKind.Gray8, 1);
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            var result = new PixelBuffer(Math.Min(512, reference.Width - key.X * 512), Math.Min(512, reference.Height - key.Y * 512), PixelKind.Gray8);
            result.Span.Clear();
            for (var y = 0; y < result.Height; y++)
            {
                for (var x = 0; x < result.Width; x++)
                {
                    var sx = x + key.X * 512 - offset;
                    var sy = y + key.Y * 512;
                    var i = (sy * width + sx) * 3;
                    if (sx >= 0 && sx < width && i + 2 < data.Length)
                    {
                        result.Span[y * result.Stride + x] = (byte)((data[i] * 29 + data[i + 1] * 150 + data[i + 2] * 77 + 128) >> 8);
                    }
                }
            }

            return result;
        }
        public void Dispose() => owner.Disposals++;
    }
}
