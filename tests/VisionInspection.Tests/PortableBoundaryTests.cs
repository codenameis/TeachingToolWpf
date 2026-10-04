using System.Reflection;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

namespace VisionInspection.Tests;

public sealed class PortableBoundaryTests
{
    [Fact]
    public void InspectionAndImagingHaveNoWindowsOrRenderingAssemblyDependency()
    {
        var visited = new HashSet<string>();
        Visit(typeof(ShapeMatchingService).Assembly);
        Visit(typeof(ImageLoader).Assembly);
        Assert.DoesNotContain(visited, name => name is "TeachingToolWpf" or "VisionCanvas" or
            "PresentationCore" or "PresentationFramework" or "WindowsBase" || name.StartsWith("Vortice.", StringComparison.Ordinal));

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.GetName().Name!))
            {
                return;
            }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == "netstandard")
                {
                    continue;
                }
                Visit(Assembly.Load(reference));
            }
        }
    }

    [Fact]
    public async Task ExplicitGpuSelectionWithoutBackendFailsBeforeOpeningImage()
    {
        var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
        var loader = new RejectingLoader();
        var service = new ShapeMatchingService(loader, () => null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(
            new("missing", "missing", 8192, 8192), false, model, null,
            new()
            {
                ComputeMode = InspectionComputeMode.GpuPreprocessing
            }, default));
        Assert.Contains("등록되지", error.Message);
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public async Task TemplateGpuSelectionIsRejectedBeforeOpeningImage()
    {
        var pixels = new byte[16 * 16];
        new Random(42).NextBytes(pixels);
        var model = CorrelationTemplate.Train(new(pixels, 16, 16, 16), new(ReadOnlyMemory<byte>.Empty, new(8, 8)));
        var loader = new RejectingLoader();
        var service = new TemplateMatchingService(loader, () => null);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.SearchAsync(
            new("missing", "missing", 8192, 8192), false, model, null,
            new()
            {
                ComputeMode = InspectionComputeMode.GpuPreprocessing
            }, default));
        Assert.Contains("CPU", error.Message);
        Assert.Equal(0, loader.Calls);
    }

    private sealed class RejectingLoader : IImageLoader
    {
        public int Calls
        {
            get; private set;
        }

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("원본 입력을 열면 안 됩니다.");
        }
    }
}
