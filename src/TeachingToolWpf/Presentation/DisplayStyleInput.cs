using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.Presentation;

internal static class DisplayStyleInput
{
    internal static Vector4 ParseColor(string text, Vector4 current, string name)
    {
        var hex = text.Trim();
        if ((hex.Length != 7 && hex.Length != 9) || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{name} 색상은 #RRGGBB 또는 #AARRGGBB 형식으로 입력하세요.");
        }
        // 기본 팔레트의 float 정밀도는 색 입력을 바꾸지 않았을 때 보존한다.
        if (string.Equals(hex, FormatColor(current), StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }
        return new(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f,
            hex.Length == 9 ? (value >> 24) / 255f : 1);
    }

    internal static float ParseThickness(string text, string name)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            !float.IsFinite(value) || value < RoiStrokeStyle.MinimumThickness || value > RoiStrokeStyle.MaximumThickness)
        {
            throw new ArgumentException($"{name} 선 두께에 0.5~8 사이의 숫자를 입력하세요.");
        }
        return value;
    }

    internal static string FormatColor(Vector4 color)
        => color.W == 1 ? $"#{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}"
            : $"#{Channel(color.W):X2}{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}";

    internal static Brush Swatch(Vector4 color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(Channel(color.W), Channel(color.X), Channel(color.Y), Channel(color.Z)));
        brush.Freeze();
        return brush;
    }

    private static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
