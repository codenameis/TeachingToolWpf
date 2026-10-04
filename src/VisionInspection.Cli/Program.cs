using VisionCompute.ILGPU;

namespace VisionInspection.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("VisionInspection.Cli --recipe <recipe.json> --output <result.json>");
            Console.WriteLine("종료 코드: 0 완료, 2 불완전 검색, 3 취소/시간 초과, 1 오류. 제품 OK/NG 판정은 수행하지 않습니다.");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var (recipe, output) = ParseArguments(args);
            var runner = new RecipeRunner(createGpuPreprocessor: () => new CudaImagePreprocessor());
            var result = await new RecipeFileRunner(runner).RunAsync(recipe, output, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine($"{result.Status}: {result.Matches.Length}개, {result.Diagnostics.TotalMilliseconds:F1} ms · {Path.GetFullPath(output)}");
            if (result.Error is not null)
            {
                Console.Error.WriteLine(result.Error);
            }
            return result.ExitCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("레시피를 읽기 전에 실행이 취소됐습니다. 결과 파일을 변경하지 않았습니다.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static (string Recipe, string Output) ParseArguments(string[] args)
    {
        string? recipe = null;
        string? output = null;
        if (args.Length != 4)
        {
            throw new ArgumentException("사용법: --recipe <recipe.json> --output <result.json>");
        }
        for (var index = 0; index < args.Length; index += 2)
        {
            if (string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("레시피와 결과 파일 경로를 지정하세요.");
            }
            switch (args[index])
            {
                case "--recipe" when recipe is null:
                    recipe = args[index + 1];
                    break;
                case "--output" when output is null:
                    output = args[index + 1];
                    break;
                default:
                    throw new ArgumentException($"중복되거나 지원하지 않는 인자입니다: {args[index]}");
            }
        }
        return (recipe ?? throw new ArgumentException("--recipe가 필요합니다."), output ?? throw new ArgumentException("--output이 필요합니다."));
    }
}
