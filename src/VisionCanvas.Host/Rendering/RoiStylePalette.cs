using System.Numerics;
using VisionCanvas.Core.Editing;

namespace VisionCanvas.Rendering;

/// <summary>역할별 표시 설정. ROI 형상과 편집 이력을 변경하지 않는다.</summary>
public sealed record RoiStylePalette
{
    private static readonly RoiStrokeStyle s_moduleDefault = new(new(.35f, .55f, 1, 1), new(.55f, .72f, 1, 1), 1, new(.35f, .55f, 1, 1));
    private static readonly RoiStrokeStyle s_rootDefault = new(new(1, .65f, .15f, 1), new(1, .85f, .35f, 1), 1, new(1, .65f, .15f, 1));
    public RoiStrokeStyle Module { get; }
    public RoiStrokeStyle Root { get; }
    public RoiStrokeStyle Default { get; }

    public RoiStrokeStyle Child { get; }

    public RoiStrokeStyle Parent { get; }

    public static RoiStylePalette Standard { get; } = new(
        new(new(.66f, .73f, .84f, 1), new(.22f, .52f, 1, 1), 1, new(1, .72f, .18f, 1)),
        new(new(.2f, .8f, .6f, 1), new(.1f, 1, .75f, 1), 1, new(.2f, .8f, .6f, 1)),
        new(new(244 / 255f, 114 / 255f, 182 / 255f, 1), new(214 / 255f, 51 / 255f, 132 / 255f, 1), 1, new(.95f, .45f, .72f, 1)));

    public RoiStylePalette(RoiStrokeStyle defaultStyle, RoiStrokeStyle childStyle, RoiStrokeStyle parentStyle,
        RoiStrokeStyle? moduleStyle = null, RoiStrokeStyle? rootStyle = null)
    {
        Default = defaultStyle ?? throw new ArgumentNullException(nameof(defaultStyle));
        Child = childStyle ?? throw new ArgumentNullException(nameof(childStyle));
        Parent = parentStyle ?? throw new ArgumentNullException(nameof(parentStyle));
        Module = moduleStyle ?? s_moduleDefault;
        Root = rootStyle ?? s_rootDefault;
    }

    public RoiStrokeStyle For(RoiRole role) => role switch
    {
        RoiRole.Default => Default,
        RoiRole.Child => Child,
        RoiRole.Parent => Parent,
        RoiRole.Module => Module,
        RoiRole.Root => Root,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
