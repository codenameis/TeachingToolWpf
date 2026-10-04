using System.IO;
using SharpGen.Runtime;
using Vortice.D3DCompiler;

namespace TeachingToolWpf.Rendering;

/// <summary>셰이더 파일 읽기와 네이티브 컴파일러 호출을 연결하는 경계.</summary>
internal static class ShaderCompiler
{
    public static ReadOnlyMemory<byte> CompileFile(string path, string entryPoint, string profile)
    {
        // 네이티브 컴파일러는 문자 수가 아닌 바이트 수를 받는다. UTF-8 주석도 그대로 전달한다.
        var source = File.ReadAllBytes(path);
        var result = Compiler.Compile(source.AsSpan(), entryPoint, Path.GetFileName(path), profile,
            out var bytecode, out var diagnostics);
        try
        {
            if (result.Failure)
            {
                if (diagnostics is not null)
                {
                    throw new SharpGenException(result, diagnostics.AsString());
                }

                throw new SharpGenException(result);
            }

            return bytecode.AsMemory();
        }
        finally
        {
            bytecode?.Dispose();
            diagnostics?.Dispose();
        }
    }
}
