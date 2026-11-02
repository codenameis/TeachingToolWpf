using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace VisionCanvas.Tests;

/// <summary>클릭이 무엇을 잡는지 정하는 규칙. 입력 컨트롤러 없이 문서만으로 확인한다.</summary>
public sealed class RoiHitTesterTests
{
    [Fact]
    public void EmptyAreaHitsNothing()
    {
        var editor = CreateEditor();
        editor.Add(new(new(100, 100), 50, 50, 0));

        Assert.Null(Tester(editor).HitOrdinary(new(500, 500)));
    }

    [Fact]
    public void ClickInsideRoiReturnsIt()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 50, 0));

        Assert.Equal(id, Tester(editor).HitOrdinary(new(100, 100)));
    }

    [Fact]
    public void ToleranceIsThreeDipAroundTheBoundary()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 50, 0));
        var tester = Tester(editor);

        // 오른쪽 경계는 x=125. 허용 반경 3 안쪽은 잡고, 4 이상 밖은 놓친다.
        Assert.Equal(id, tester.HitOrdinary(new(127, 100)));
        Assert.Null(tester.HitOrdinary(new(129, 100)));
    }

    [Fact]
    public void OverlappingPicksTheSmallerArea()
    {
        var editor = CreateEditor();
        editor.Add(new(new(100, 100), 100, 100, 0));
        var small = editor.Add(new(new(100, 100), 20, 20, 0));

        Assert.Equal(small, Tester(editor).HitOrdinary(new(100, 100)));
    }

    [Fact]
    public void AltCycleMovesToTheNextCandidate()
    {
        var editor = CreateEditor();
        var big = editor.Add(new(new(100, 100), 100, 100, 0));
        var small = editor.Add(new(new(100, 100), 20, 20, 0));
        editor.Select([small]);

        Assert.Equal(big, Tester(editor).HitOrdinary(new(100, 100), cycleSelection: true));
    }

    [Fact]
    public void OverMinimapNeverHitsOrdinaryRoi()
    {
        var editor = CreateEditor();
        editor.Add(new(new(100, 100), 50, 50, 0));

        Assert.Null(new RoiHitTester(editor.State, overMinimap: true).HitOrdinary(new(100, 100)));
    }

    [Fact]
    public void HitOrdinaryReturnsTopCandidate()
    {
        var editor = CreateEditor();
        var id = editor.Add(new(new(100, 100), 50, 50, 0));

        var ordinary = Tester(editor).HitOrdinary(new(100, 100), cycleSelection: false);

        Assert.Equal(id, ordinary);
    }

    private static RoiHitTester Tester(EditorSession editor) => new(editor.State, overMinimap: false);

    private static EditorSession CreateEditor()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
        return editor;
    }
}
