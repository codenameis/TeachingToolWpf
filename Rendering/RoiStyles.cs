using System.Numerics;
using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Rendering;

public enum RoiRole
{
    Default = 0, Child = 1, Parent = 2
}

/// <summary>Immutable display preference. Widths are DIP, independent of image zoom and DPI.</summary>
public sealed record RoiStrokeStyle
{
    public const float MinimumThickness = .5f;

    public const float MaximumThickness = 8;

    public Vector4 Color
    {
        get;
    }

    public Vector4 SelectedColor
    {
        get;
    }

    public float ThicknessDip
    {
        get;
    }

    public RoiStrokeStyle(Vector4 color, Vector4 selectedColor, float thicknessDip)
    {
        ValidateColor(color, nameof(color));
        ValidateColor(selectedColor, nameof(selectedColor));
        if (!float.IsFinite(thicknessDip) || thicknessDip < MinimumThickness || thicknessDip > MaximumThickness)
        {
            throw new ArgumentOutOfRangeException(nameof(thicknessDip), "선 두께는 0.5~8 DIP 범위여야 합니다.");
        }

        Color = color;
        SelectedColor = selectedColor;
        ThicknessDip = thicknessDip;
    }

    public RoiStrokeStyle WithThickness(float thicknessDip) => new(Color, SelectedColor, thicknessDip);

    public Vector4 StrokeColor(bool selected) => selected ? SelectedColor : Color;

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

/// <summary>Three independent style slots; future palettes change no ROI geometry or edit history.</summary>
public sealed record RoiStylePalette
{
    public RoiStrokeStyle Default
    {
        get;
    }

    public RoiStrokeStyle Child
    {
        get;
    }

    public RoiStrokeStyle Parent
    {
        get;
    }

    public static RoiStylePalette Standard
    {
        get;
    } = new(
        new(new(.66f, .73f, .84f, 1), new(.22f, .52f, 1, 1), 1),
        new(new(.66f, .73f, .84f, 1), new(.22f, .52f, 1, 1), 1),
        new(new(244 / 255f, 114 / 255f, 182 / 255f, 1), new(214 / 255f, 51 / 255f, 132 / 255f, 1), 1));

    public RoiStylePalette(RoiStrokeStyle defaultStyle, RoiStrokeStyle childStyle, RoiStrokeStyle parentStyle)
    {
        Default = defaultStyle ?? throw new ArgumentNullException(nameof(defaultStyle));
        Child = childStyle ?? throw new ArgumentNullException(nameof(childStyle));
        Parent = parentStyle ?? throw new ArgumentNullException(nameof(parentStyle));
    }

    public RoiStrokeStyle For(RoiRole role) => role switch
    {
        RoiRole.Default => Default,
        RoiRole.Child => Child,
        RoiRole.Parent => Parent,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

internal static class RoiStyleRoles
{
    public static RoiRole Resolve(DocumentState state, Guid id)
    {
        var roi = state.Rois[id];
        // Actual containment is authoritative: group origin metadata does not imply live children.
        if (state.Snapshot.Children.TryGetValue(id, out var children) && !children.IsEmpty)
        {
            return RoiRole.Parent;
        }

        return roi.ParentId is not null ? RoiRole.Child : RoiRole.Default;
    }
}
