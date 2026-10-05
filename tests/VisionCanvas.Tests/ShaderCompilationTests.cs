using System.IO;
using System.Text;
using SharpGen.Runtime;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Imaging.Tests;

public class ShaderCompilationTests
{
    [Theory]
    [InlineData("Roi.hlsl", "VSMain", "vs_5_1")]
    [InlineData("Roi.hlsl", "PSMain", "ps_5_1")]
    [InlineData("Viewport.hlsl", "VSMain", "vs_5_1")]
    [InlineData("Viewport.hlsl", "PSMain", "ps_5_1")]
    public void ApplicationShadersCompileWithoutCreatingAGpuDevice(string file, string entryPoint, string profile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", file);
        Assert.False(ShaderCompiler.CompileFile(path, entryPoint, profile).IsEmpty);
    }

    [Theory]
    [InlineData("VSMain", "vs_5_1")]
    [InlineData("PSMain", "ps_5_1")]
    public void Utf8CommentsDoNotTruncateTheShaderSource(string entryPoint, string profile)
    {
        var comments = string.Concat(Enumerable.Repeat("// 화면 좌표와 회전, 선택 색상 설명\n", 20));
        WithShaderFile(comments + "float4 VSMain(float4 p : POSITION) : SV_POSITION { return p; }\n"
            + "float4 PSMain() : SV_TARGET { return float4(1, 0, 0, 1); }\n", path =>
        {
            Assert.False(ShaderCompiler.CompileFile(path, entryPoint, profile).IsEmpty);
        });
    }

    [Fact]
    public void InvalidSourceKeepsTheCompilerDiagnostic()
    {
        WithShaderFile("invalid shader syntax", path =>
        {
            var error = Assert.Throws<SharpGenException>(() => ShaderCompiler.CompileFile(path, "VSMain", "vs_5_1"));
            Assert.Contains(Path.GetFileName(path), error.Message);
        });
    }

    private static void WithShaderFile(string source, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teaching-shader-{Guid.NewGuid():N}.hlsl");
        try
        {
            File.WriteAllText(path, source, new UTF8Encoding(false));
            action(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
