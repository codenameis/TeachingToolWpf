using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

/// <summary>검사 도구가 쓰는 단일 선택 해석 규칙. 두 검사 패널이 같은 값을 받는지 확인한다.</summary>
public sealed class InspectionSelectionTests
{
    [Fact]
    public void EmptySelectionHasNoTarget()
    {
        var editor = CreateEditor();
        editor.Add(new(new(100, 100), 50, 50, 0));
        editor.Select([]);

        var selection = InspectionSelection.Resolve(editor.State);

        Assert.Null(selection.Selection);
        Assert.Null(selection.SearchSelection);
        Assert.Null(selection.Exclusion);
        Assert.Null(selection.RoiId);
    }

    [Fact]
    public void MultipleSelectionHasNoTarget()
    {
        var editor = CreateEditor();
        var first = editor.Add(new(new(100, 100), 50, 50, 0));
        var second = editor.Add(new(new(300, 300), 50, 50, 0));
        editor.Select([first, second]);

        Assert.Null(InspectionSelection.Resolve(editor.State).Selection);
    }

    [Fact]
    public void GroupRoiHasNoTarget()
    {
        var editor = CreateEditor();
        var group = editor.AddParent(new(new(100, 100), 200, 200, 0));
        editor.AddChild(group, new(new(100, 100), 20, 20, 0));
        editor.Select([group]);

        var selection = InspectionSelection.Resolve(editor.State);

        Assert.Null(selection.Selection);
        Assert.Null(selection.RoiId);
    }

    [Fact]
    public void SingleRoiWithoutSearchRangeReusesTheSameGeometryInstance()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 40, 15));
        editor.Select([id]);

        var selection = InspectionSelection.Resolve(editor.State);

        Assert.Same(editor.State.Rois[id].Geometry, selection.Selection);
        // 검색 여유가 없으면 확장 사본을 만들지 않고 같은 인스턴스를 쓴다.
        Assert.Same(selection.Selection, selection.SearchSelection);
        Assert.Equal(id, selection.RoiId);
        Assert.Null(selection.Exclusion);
    }

    [Fact]
    public void SearchRangeExpandsOnlyTheSearchSelection()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 40, 15));
        editor.SetProperties(id, "검색 여유", editor.State.Rois[id].Geometry, new(20, 10), true);
        editor.Select([id]);

        var selection = InspectionSelection.Resolve(editor.State);

        Assert.Equal(50, selection.Selection!.Width);
        Assert.Equal(40, selection.Selection.Height);
        Assert.Equal(90, selection.SearchSelection!.Width);
        Assert.Equal(60, selection.SearchSelection.Height);
        Assert.Equal(15, selection.SearchSelection.Angle);
    }

    [Fact]
    public void MaskCreatesExclusionFromTheUnExpandedGeometry()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 40, 0));
        editor.SetProperties(id, "검색 여유", editor.State.Rois[id].Geometry, new(20, 20), true);
        editor.AddMask(id, new MaskRegion(MaskShape.Rectangle, new(.1, .1, .3, .3)));
        editor.Select([id]);

        var selection = InspectionSelection.Resolve(editor.State);

        Assert.NotNull(selection.Exclusion);
        // 마스크는 편집한 도형 기준이고, 검색 여유가 적용된 사본을 쓰지 않는다.
        Assert.Same(selection.Selection, selection.Exclusion!.Owner);
        Assert.Single(selection.Exclusion.Regions);
    }

    [Fact]
    public void RepeatedResolveWithoutSearchRangeOrMaskDoesNotAllocate()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 40, 0));
        editor.Select([id]);
        var state = editor.State;

        for (var i = 0; i < 100; i++)
        {
            _ = InspectionSelection.Resolve(state);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = InspectionSelection.Resolve(state);
        }

        // 프레임마다 호출되는 경로다. 검색 여유·마스크가 없으면 힙 할당이 없어야 한다.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024,
            "InspectionSelection.Resolve가 프레임 경로에서 할당했습니다.");
    }

    private static EditorSession CreateEditor()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
        return editor;
    }
}
