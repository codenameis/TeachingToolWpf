using System.Numerics;
using VisionCanvas.Layers;

namespace VisionCanvas.Tests;

public sealed class DrawingLayerTests
{
    private static LineGraphic Line(double x) => new(new(x, 0), new(x, 10), Vector4.One);

    [Fact]
    public void ItemIdsOrderAndSnapshotsSurviveIndependentEdits()
    {
        var changes = 0;
        var layers = new DrawingLayerCollection(() => { }, () => changes++);
        var results = layers.Create("results", LayerSpace.Image);
        var guide = layers.Create("guide", LayerSpace.Screen);
        var guideId = guide.Add(CrosshairGraphic.ScreenCenter(Vector4.One));
        var ids = results.AddRange([Line(1), Line(2), Line(3)]);
        var before = layers.Snapshot;
        Assert.True(results.Update(ids[1], Line(20)));
        Assert.True(results.Remove(ids[0]));
        Assert.False(results.Remove(ids[0]));
        Assert.False(results.Remove(guideId));
        Assert.False(results.Update(Guid.NewGuid(), Line(9)));
        Assert.Equal(new[] { ids[1], ids[2] }, results.Snapshot.Items.Values.Select(item => item.Id));
        Assert.Equal(3, before[0].Items.Count);
        Assert.Equal(Line(2), before[0].Items.Values.ElementAt(1).Graphic);
        Assert.Same(before[1], layers.Snapshot[1]);
        results.IsVisible = false;
        Assert.Equal(2, results.Count);
        results.Clear();
        Assert.Equal(0, results.Count);
        Assert.False(results.IsVisible);
        Assert.Equal(1, guide.Count);
        Assert.Same(results, layers["results"]);
        Assert.Equal(8, changes);
    }

    [Fact]
    public void BulkValidationAndEnumerationFailureAreAtomic()
    {
        var changes = 0;
        var layers = new DrawingLayerCollection(() => { }, () => changes++);
        var layer = layers.Create("results", LayerSpace.Image);
        var id = layer.Add(Line(1));
        var before = layers.Snapshot;
        Assert.Throws<ArgumentException>(() => layer.ReplaceAll([Line(2), Line(double.NaN)]));
        Assert.Throws<InvalidOperationException>(() => layer.AddRange(Failing()));
        Assert.Equal(before, layers.Snapshot);
        Assert.Equal(2, changes);
        var fresh = layer.ReplaceAll([Line(3), Line(4)]);
        Assert.Equal(3, changes);
        Assert.False(layer.Remove(id));
        Assert.Equal(2, fresh.Length);

        static IEnumerable<CanvasGraphic> Failing()
        {
            yield return Line(2);
            throw new InvalidOperationException("입력 열거 실패");
        }
    }

    [Fact]
    public void InvalidNamesSpacesAndGraphicsDoNotEnterTheRenderer()
    {
        var layers = new DrawingLayerCollection(() => { }, () => { });
        var layer = layers.Create("results", LayerSpace.Image);
        Assert.Throws<ArgumentException>(() => layers.Create("results", LayerSpace.Image));
        Assert.Throws<ArgumentException>(() => layers.Create(" ", LayerSpace.Screen));
        Assert.Throws<ArgumentOutOfRangeException>(() => layers.Create("bad", (LayerSpace)100));
        Assert.Throws<ArgumentException>(() => layer.Add(CrosshairGraphic.ScreenCenter(Vector4.One)));
        Assert.Throws<ArgumentException>(() => layer.Add(Line(1) with { Thickness = 0 }));
        Assert.Throws<ArgumentException>(() => layer.Add(Line(1) with { Color = new(1, 1, 1, float.NaN) }));
        Assert.Throws<ArgumentNullException>(() => layer.Add(null!));
        Assert.Throws<ArgumentNullException>(() => layer.Add(new ShapeGraphic(null!, Vector4.One)));
        Assert.Empty(layer.Snapshot.Items);
    }

    [Fact]
    public void AllMutationsCheckTheOwningThreadBeforeChangingState()
    {
        var allowed = true;
        var layers = new DrawingLayerCollection(() =>
        {
            if (!allowed)
            {
                throw new InvalidOperationException("다른 스레드");
            }
        }, () => { });
        var layer = layers.Create("results", LayerSpace.Image);
        var id = layer.Add(Line(1));
        allowed = false;
        Assert.Throws<InvalidOperationException>(() => layer.Add(Line(2)));
        Assert.Throws<InvalidOperationException>(() => layer.Update(id, Line(2)));
        Assert.Throws<InvalidOperationException>(() => layer.Remove(id));
        Assert.Throws<InvalidOperationException>(() => layer.Clear());
        Assert.Throws<InvalidOperationException>(() => layer.IsVisible = false);
        Assert.Throws<InvalidOperationException>(() => layers.Create("other", LayerSpace.Screen));
        Assert.Single(layer.Snapshot.Items);
    }
}
