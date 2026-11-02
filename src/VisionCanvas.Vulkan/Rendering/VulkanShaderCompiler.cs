using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.ShaderCompiler;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>shaderc에는 UTF-16 문자 수가 아닌 UTF-8 바이트 수를 전달한다. 한글 주석도 보존한다.</summary>
internal static unsafe class VulkanShaderCompiler
{
    private const string Library = "shaderc_shared";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compiler_initialize")]
    private static extern nint ShadercCompilerInitialize();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compiler_release")]
    private static extern void ShadercCompilerRelease(nint compiler);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_initialize")]
    private static extern nint ShadercCompileOptionsInitialize();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_release")]
    private static extern void ShadercCompileOptionsRelease(nint options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_source_language")]
    private static extern void ShadercCompileOptionsSetSourceLanguage(nint options, SourceLanguage language);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_target_env")]
    private static extern void ShadercCompileOptionsSetTargetEnv(nint options, TargetEnvironment environment, uint version);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_optimization_level")]
    private static extern void ShadercCompileOptionsSetOptimizationLevel(nint options, OptimizationLevel level);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_hlsl_offsets")]
    private static extern void ShadercCompileOptionsSetHlslOffsets(nint options, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_auto_map_locations")]
    private static extern void ShadercCompileOptionsSetAutoMapLocations(nint options, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_invert_y")]
    private static extern void ShadercCompileOptionsSetInvertY(nint options, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_options_set_hlsl_register_set_and_binding")]
    private static extern void ShadercCompileOptionsSetHlslRegisterSetAndBinding(nint options, byte* register, byte* set, byte* binding);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_compile_into_spv")]
    private static extern nint ShadercCompileIntoSpv(nint compiler, byte* source, nuint length, uint stage, byte* name, byte* entry, nint options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_result_get_compilation_status")]
    private static extern CompilationStatus ShadercResultGetCompilationStatus(nint result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_result_get_error_message")]
    private static extern nint ShadercResultGetErrorMessage(nint result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_result_get_length")]
    private static extern nuint ShadercResultGetLength(nint result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_result_get_bytes")]
    private static extern byte* ShadercResultGetBytes(nint result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "shaderc_result_release")]
    private static extern void ShadercResultRelease(nint result);

    public static byte[] Compile(string path, ShaderKind stage, string entry)
    {
        var compiler = ShadercCompilerInitialize();
        var options = ShadercCompileOptionsInitialize();
        nint result = 0;
        try
        {
            if (compiler == 0 || options == 0)
            {
                throw new InvalidOperationException("shaderc 초기화에 실패했습니다.");
            }
            ShadercCompileOptionsSetSourceLanguage(options, SourceLanguage.HLSL);
            ShadercCompileOptionsSetTargetEnv(options, TargetEnvironment.Vulkan, (uint)TargetEnvironmentVersion.Vulkan_1_1);
            ShadercCompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            ShadercCompileOptionsSetHlslOffsets(options, true);
            ShadercCompileOptionsSetAutoMapLocations(options, true);
            ShadercCompileOptionsSetInvertY(options, stage == ShaderKind.VertexShader);
            string[] registers = ["t0", "t1", "b0", "s0", "s1"];
            using var set = new Utf8Name("0");
            for (var index = 0; index < registers.Length; index++)
            {
                using var register = new Utf8Name(registers[index]);
                using var binding = new Utf8Name(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ShadercCompileOptionsSetHlslRegisterSetAndBinding(options, register.Pointer, set.Pointer, binding.Pointer);
            }
            var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path));
            using var name = new Utf8Name(path);
            using var entryName = new Utf8Name(entry);
            fixed (byte* source = bytes)
            {
                result = ShadercCompileIntoSpv(compiler, source, (nuint)bytes.Length, (uint)stage, name.Pointer, entryName.Pointer, options);
            }
            if (result == 0 || ShadercResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                var error = result == 0 ? "결과 없음" : Marshal.PtrToStringUTF8(ShadercResultGetErrorMessage(result));
                throw new InvalidOperationException($"{Path.GetFileName(path)}/{entry}: {error}");
            }
            return new ReadOnlySpan<byte>(ShadercResultGetBytes(result), checked((int)ShadercResultGetLength(result))).ToArray();
        }
        finally
        {
            if (result != 0)
            {
                ShadercResultRelease(result);
            }
            if (options != 0)
            {
                ShadercCompileOptionsRelease(options);
            }
            if (compiler != 0)
            {
                ShadercCompilerRelease(compiler);
            }
        }
    }
}
