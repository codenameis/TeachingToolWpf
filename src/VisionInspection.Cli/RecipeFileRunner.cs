using System.Text.Json;

namespace VisionInspection.Cli;

/// <summary>레시피를 읽고 완성된 결과만 같은 디렉터리의 임시 파일에서 원자적으로 교체한다.</summary>
public sealed class RecipeFileRunner(RecipeRunner? runner = null)
{
    private readonly RecipeRunner _runner = runner ?? new();

    public async Task<InspectionRunResult> RunAsync(string recipePath, string outputPath, CancellationToken cancellationToken = default)
    {
        recipePath = Path.GetFullPath(recipePath);
        outputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(recipePath)!;
        OutputPathGuard.EnsureDistinct(outputPath, [recipePath]);
        InspectionRecipe recipe;
        await using (var stream = File.OpenRead(recipePath))
        {
            recipe = await JsonSerializer.DeserializeAsync<InspectionRecipe>(stream, RecipeJson.CreateOptions(), cancellationToken)
                .ConfigureAwait(false) ?? throw new JsonException("레시피 JSON은 객체여야 합니다.");
        }
        var protectedPaths = new List<string> { recipePath };
        foreach (var input in new[] { recipe.TrainingImage, recipe.InspectionImage })
        {
            if (!string.IsNullOrWhiteSpace(input))
            {
                protectedPaths.Add(Path.GetFullPath(input, directory));
            }
        }
        OutputPathGuard.EnsureDistinct(outputPath, protectedPaths);
        var result = await _runner.RunAsync(recipe, directory, cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(outputPath, protectedPaths, result).ConfigureAwait(false);
        return result;
    }

    private static async Task WriteAtomicAsync(string outputPath, IReadOnlyList<string> protectedPaths, InspectionRunResult result)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                // 취소 결과도 완성된 JSON으로 남겨야 하므로 이미 종료된 검사 token을 쓰지 않는다.
                await JsonSerializer.SerializeAsync(stream, result, RecipeJson.CreateOptions()).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            OutputPathGuard.EnsureDistinct(outputPath, protectedPaths);
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

internal static class OutputPathGuard
{
    public static void EnsureDistinct(string outputPath, IEnumerable<string> protectedPaths)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var destination = ResolveLinks(outputPath);
        foreach (var path in protectedPaths)
        {
            if (string.Equals(destination, ResolveLinks(path), comparison))
            {
                throw new ArgumentException("결과 파일로 레시피 또는 원본 이미지 파일을 덮어쓸 수 없습니다.");
            }
        }
    }

    private static string ResolveLinks(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        foreach (var segment in absolute[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("결과 또는 입력 경로의 심볼릭 링크를 확인할 수 없습니다.");
            }
        }
        return Path.GetFullPath(current);
    }
}
