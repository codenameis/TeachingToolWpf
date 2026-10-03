using System.Globalization;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Presentation;

public sealed class RoiPropertiesViewModel(EditorSession editor) : ObservableObject
{
    private Guid? id;
    private string name = "", x = "", y = "", width = "", height = "", angle = "", relative = "—";
    public bool HasSingleSelection => id is not null;
    public string ShapeDescription => id is { } value ? editor.State.Rois[value].Geometry.ShapeName : "";
    public string SizeHint => id is { } value && editor.State.Rois[value].Geometry.Shape.Kind == RoiShapeKind.Circle
        ? "원: 너비·높이는 같은 지름으로 적용됩니다." : "원본 이미지 픽셀 기준";
    public string Name { get => name; set => Set(ref name, value); }
    public string X { get => x; set => Set(ref x, value); }
    public string Y { get => y; set => Set(ref y, value); }
    public string Width { get => width; set => Set(ref width, value); }
    public string Height { get => height; set => Set(ref height, value); }
    public string Angle { get => angle; set => Set(ref angle, value); }
    public string RelativeAngle { get => relative; private set => Set(ref relative, value); }
    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    public void Refresh()
    {
        id = editor.State.Selection.Count == 1 ? editor.State.Selection.Single() : null;
        Notify(nameof(HasSingleSelection));
        Notify(nameof(ShapeDescription));
        Notify(nameof(SizeHint));
        if (id is not { } value) { Name = X = Y = Width = Height = Angle = ""; RelativeAngle = "—"; return; }
        var roi = editor.State.Rois[value]; var geometry = editor.TransformPreview?.Geometry(roi) ?? roi.Geometry;
        Name = roi.Name; X = Format(geometry.Center.X); Y = Format(geometry.Center.Y);
        Width = Format(geometry.Width); Height = Format(geometry.Height); Angle = Format(geometry.Angle);
        RelativeAngle = roi.ParentId is { } parent ? Format(RoiGeometry.NormalizeAngle(geometry.Angle -
            (editor.TransformPreview?.Geometry(editor.State.Rois[parent]) ?? editor.State.Rois[parent].Geometry).Angle)) + "°" : "루트 ROI";
    }
    public void Apply()
    {
        if (id is not { } value) return;
        static double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
            ? value : throw new ArgumentException("속성에는 유한한 숫자를 입력하세요.");
        var original = editor.State.Rois[value].Geometry;
        var newWidth = Parse(Width); var newHeight = Parse(Height);
        if (original.Shape.Kind == RoiShapeKind.Circle)
        {
            if (Width != Format(original.Width)) newHeight = newWidth;
            else newWidth = newHeight;
        }
        editor.SetProperties(value, Name, original.WithFrame(new(Parse(X), Parse(Y)), newWidth, newHeight, Parse(Angle)));
    }
}
