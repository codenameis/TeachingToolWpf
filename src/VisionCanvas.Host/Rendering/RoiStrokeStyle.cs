using System.Numerics;
using VisionCanvas.Core.Editing;

namespace VisionCanvas.Rendering;

/// <summary>Immutable display preference. Widths are DIP, independent of image zoom and DPI.</summary>
public sealed record RoiStrokeStyle
{
    public const float MinimumThickness = .5f;

    public const float MaximumThickness = 8;

    public Vector4 Color { get; }

    public Vector4 SelectedColor { get; }

    public Vector4 MultiSelectedColor { get; }

    public float ThicknessDip { get; }

    public RoiStrokeStyle(Vector4 color, Vector4 selectedColor, float thicknessDip, Vector4? multiSelectedColor = null)
    {
        ValidateColor(color, nameof(color));
        ValidateColor(selectedColor, nameof(selectedColor));
        ValidateColor(multiSelectedColor ?? selectedColor, nameof(multiSelectedColor));
        if (!float.IsFinite(thicknessDip) || thicknessDip < MinimumThickness || thicknessDip > MaximumThickness)
        {
            throw new ArgumentOutOfRangeException(nameof(thicknessDip), "선 두께는 0.5~8 DIP 범위여야 합니다.");
        }

        Color = color;
        SelectedColor = selectedColor;
        MultiSelectedColor = multiSelectedColor ?? selectedColor;
        ThicknessDip = thicknessDip;
    }

    public RoiStrokeStyle WithThickness(float thicknessDip) => new(Color, SelectedColor, thicknessDip, MultiSelectedColor);

    public Vector4 StrokeColor(bool selected, bool multiple = false)
    {
        if (!selected)
        {
            return Color;
        }

        return multiple ? MultiSelectedColor : SelectedColor;
    }

    public float StrokeThickness(bool selected) => ThicknessDip + (selected ? .5f : 0);

    private static void ValidateColor(Vector4 color, string name)
    {
        if (!Valid(color.X) || !Valid(color.Y) || !Valid(color.Z) || !Valid(color.W))
        {
            throw new ArgumentOutOfRangeException(name, "색상 채널은 0~1 범위의 유한한 값이어야 합니다.");
        }

        static bool Valid(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
    }
}
