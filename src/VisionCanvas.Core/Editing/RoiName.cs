using System.Globalization;

namespace VisionCanvas.Core.Editing;

/// <summary>ROI 표시 이름의 길이 검증·정규화·기본 이름 생성 규칙을 한 곳에서 관리한다.</summary>
public static class RoiName
{
    public const string CopySuffix = " 복사";

    public static string CreateDefault(long order)
        => string.Create(CultureInfo.InvariantCulture, $"ROI {order:000}");

    public static string CreateGroupDefault(int level, long order)
        => string.Create(CultureInfo.InvariantCulture, $"{RoiHierarchy.LevelName(level)} {order:000}");

    public static string CreateCopyName(string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        int maxBaseLength = Roi.MaximumNameLength - CopySuffix.Length;
        return sourceName[..Math.Min(sourceName.Length, maxBaseLength)] + CopySuffix;
    }

    public static bool IsValid(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= Roi.MaximumNameLength;

    public static string ValidateAndTrim(string? name, string? paramName = null)
    {
        if (!IsValid(name))
        {
            throw paramName is null
                ? new ArgumentException($"이름은 1–{Roi.MaximumNameLength}자여야 합니다.")
                : new ArgumentException($"이름은 1–{Roi.MaximumNameLength}자여야 합니다.", paramName);
        }

        return name!.Trim();
    }
}
