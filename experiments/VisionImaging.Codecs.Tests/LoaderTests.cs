using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionImaging.Codecs.Tests;

public sealed class LoaderTests
{
    [Fact]
    public async Task IndependentSourcesSurvivePeerDisposalAndRejectOldVersion()
    {
        var path = DecoderTests.Tiff(16, true);
        var loader = new RawImageLoader(Path.Combine(Path.GetDirectoryName(path)!, "cache"), new NativeRasterDecoder());
        var reference = new ImageReference(path, "test", 19, 7, ImageFileVersion.Read(path));
        var progress = new Progress<string>();
        var first = await loader.LoadAsync(reference, progress, default);
        using var second = await loader.LoadAsync(reference, progress, default);
        first.Dispose();
        using var pixels = second.Read(new(0, 0, 0), default);
        Assert.Equal(PixelKind.Gray16, pixels.Kind);
        Assert.Equal(19 * 7 * 2, pixels.Length);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(reference, progress, new CancellationToken(true)));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => loader.LoadAsync(reference, progress, default));
        Assert.NotEqual(reference.Version, ImageFileVersion.Read(path));
    }

    [Fact]
    public async Task GeneratedAndLiveSourcesNeverOpenFileDecoder()
    {
        var loader = new SourceImageLoader(new RejectFiles());
        using var sample = await loader.LoadAsync(new("sample:test", "sample", 8192, 8192), new Progress<string>(), default);
        using var live = await loader.LoadAsync(new("live:Gray16", "live", 8192, 8192), new Progress<string>(), default);
        Assert.Equal(8192, sample.Info.Width);
        Assert.Equal(PixelKind.Gray16, live.Info.Pixels);
        var assemblies = typeof(SourceImageLoader).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(assemblies, name => name.Contains("Vips") || name.Contains("Codecs") || name.Contains("OpenCv"));
    }

    private sealed class RejectFiles : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("생성 영상에 파일 코덱을 호출하면 안 됩니다.");
    }
}
