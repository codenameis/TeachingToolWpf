using System.Reflection;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionInspection.Tests;

public sealed class PortableBoundaryTests
{
    [Fact]
    public void InspectionAndImagingHaveNoApplicationEditingOrRenderingAssemblyDependency()
    {
        var visited = new HashSet<string>();
        Visit(typeof(ShapeMatchingService).Assembly);
        Visit(typeof(ImageLoader).Assembly);
        Assert.DoesNotContain(visited, IsApplicationOrRendering);

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

    [Theory]
    [InlineData(typeof(IImageLoader))]
    [InlineData(typeof(IShapeMatchingService))]
    [InlineData(typeof(ITemplateMatchingService))]
    [InlineData(typeof(IPrimitiveShapeDetectionService))]
    public void PublicServiceContractsDoNotExposeApplicationEditingOrRenderingTypes(Type contract)
    {
        foreach (var method in contract.GetMethods())
        {
            AssertIndependent(method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                AssertIndependent(parameter.ParameterType);
            }
        }

        static void AssertIndependent(Type type)
        {
            Assert.False(IsApplicationOrRendering(type.Assembly.GetName().Name!), $"Public API exposes {type}.");
            if (type.HasElementType)
            {
                AssertIndependent(type.GetElementType()!);
            }
            foreach (var argument in type.GetGenericArguments())
            {
                AssertIndependent(argument);
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

    private static bool IsApplicationOrRendering(string name)
        => name.StartsWith("TeachingToolWpf", StringComparison.Ordinal) ||
           name.StartsWith("VisionCanvas", StringComparison.Ordinal) ||
           name.StartsWith("VisionCompute", StringComparison.Ordinal) ||
           name.StartsWith("Vortice.", StringComparison.Ordinal) ||
           name.StartsWith("ILGPU", StringComparison.Ordinal) ||
           name is "PresentationCore" or "PresentationFramework" or "WindowsBase";

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
