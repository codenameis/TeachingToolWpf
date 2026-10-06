using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class ClipboardContentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(90)]
    [InlineData(237)]
    public void CachedAssemblyBoundsMatchRotatedCorners(double angle)
    {
        var editor = new EditorSession();
        editor.Add(new(new(120, 160), 100, 30, angle));
        editor.Add(new(new(450, 390), 25, 140, angle + 31));
        editor.Select(editor.State.Rois.Keys);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        var content = clipboard.Capture();
        var expected = BoundsD.FromPoints(content.Entries.SelectMany(r => r.Geometry.FrameCorners));

        Assert.Equal(expected.Left, content.Bounds.Left, 9);
        Assert.Equal(expected.Top, content.Bounds.Top, 9);
        Assert.Equal(expected.Right, content.Bounds.Right, 9);
        Assert.Equal(expected.Bottom, content.Bounds.Bottom, 9);
        Assert.Equal(1, content.PasteNumber);

        var target = new ImageReference("small", "small", 80, 60);
        var placed = content.Place(target, new(20, 20));
        var scale = placed[0].Geometry.Width / content.Entries[0].Geometry.Width;
        for (var i = 0; i < placed.Length; i++)
        {
            var original = content.Entries[i];
            var copy = placed[i];
            Assert.Equal(original.Id, copy.Id);
            Assert.Equal(original.Name, copy.Name);
            Assert.Equal(original.ParentId, copy.ParentId);
            Assert.Equal(original.Filter, copy.Filter);
            Assert.Equal(original.Geometry.Angle, copy.Geometry.Angle);
            Assert.Equal(scale, copy.Geometry.Height / original.Geometry.Height, 9);
            Assert.All(copy.Geometry.FrameCorners, p =>
            {
                Assert.InRange(p.X, -1e-7, target.Width + 1e-7);
                Assert.InRange(p.Y, -1e-7, target.Height + 1e-7);
            });
        }
        Assert.True((placed[1].Geometry.Center - placed[0].Geometry.Center -
            ((content.Entries[1].Geometry.Center - content.Entries[0].Geometry.Center) * scale)).Length < 1e-7);
    }

    [Fact]
    public async Task CapturedPlacementSurvivesLaterPasteCopyAndSourceDeletion()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var id = editor.Add(new(new(100, 100), 40, 40, 0));
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        var first = clipboard.Capture();
        editor.Paste(clipboard);
        var second = clipboard.Capture();
        Assert.Equal(1, first.PasteNumber);
        Assert.Equal(2, second.PasteNumber);
        Assert.True(first.Entries.Equals(second.Entries));
        var replacement = editor.Add(new(new(800, 800), 20, 20, 45));
        clipboard.Copy(editor.State);
        editor.Select(editor.State.Rois.Keys);
        editor.DeleteSelection();

        var image = new ImageReference("target", "target", 1000, 1000);
        var firstPlaced = await Task.Run(() => first.Place(image, new(20, 20)));
        var secondPlaced = second.Place(image, new(20, 20));
        Assert.Equal(id, Assert.Single(firstPlaced).Id);
        Assert.Equal(new PointD(120, 120), firstPlaced[0].Geometry.Center);
        Assert.Equal(new PointD(140, 140), secondPlaced[0].Geometry.Center);
        var current = clipboard.Capture();
        Assert.Equal(replacement, Assert.Single(current.Entries).Id);
        Assert.Equal(1, current.PasteNumber);
    }

    [Fact]
    public void EmptyCopyPreservesExistingEntriesBoundsAndPasteNumber()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var clipboard = new RoiClipboard();
        Assert.Empty(clipboard.Capture().Place(editor.State.Image!, new(20, 20)));
        editor.Add(new(new(200, 200), 100, 40, 30));
        clipboard.Copy(editor.State);
        editor.Paste(clipboard);
        var before = clipboard.Capture();
        editor.Select([]);
        Assert.Equal(0, clipboard.Copy(editor.State));
        var after = clipboard.Capture();
        Assert.True(before.Entries.Equals(after.Entries));
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(before.PasteNumber, after.PasteNumber);
        Assert.True(clipboard.HasContent);
    }

    [Fact]
    public void CancelledPlacementDoesNotChangeSnapshotOrAdvancePasteNumber()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 40, 40, 10));
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        var content = clipboard.Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var target = new ImageReference("target", "target", 1000, 1000);
        Assert.ThrowsAny<OperationCanceledException>(() => content.Place(target, new(20, 20), cancellation.Token));
        Assert.Equal(1, clipboard.Capture().PasteNumber);
        Assert.Equal(editor.State.Rois.Values.Single(), Assert.Single(content.Entries));
        Assert.Single(content.Place(target, new(20, 20)));
    }
}
