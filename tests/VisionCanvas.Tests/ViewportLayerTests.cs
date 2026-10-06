using System.Numerics;
using System.Reflection;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public sealed class ViewportLayerTests
{
    [Fact]
    public void ClearInspectionSurvivesNavigationAndNewResultsReplaceIt() => OnSta(() =>
    {
        using var viewport = new Dx12Viewport();
        var editor = new EditorSession();
        editor.Add(new(new(20, 20), 10, 10, 0));
        var original = editor.State;
        var undo = editor.UndoName;
        var result = new ResultOverlay("결과", Vector4.One, [], []);
        var scene = new RenderScene(original, null, 0, 200, 200, 1, new(), Inspection: result);
        viewport.Submit(scene);
        Assert.Same(result, Current(viewport).Inspection);
        Assert.True(Current(viewport).InspectionInLayer);
        var layer = viewport.Layers["Inspection"];
        var firstId = layer.Snapshot.Items.Values.Single().Id;
        var guide = viewport.Layers.Create("Guide", LayerSpace.Screen);
        guide.Add(CrosshairGraphic.ScreenCenter(Vector4.One));
        layer.Clear();
        Assert.Null(Current(viewport).Inspection);
        viewport.Submit(scene with
        {
            Width = 400
        });
        Assert.Null(Current(viewport).Inspection);
        Assert.Equal(0, layer.Count);
        Assert.Equal(1, guide.Count);
        viewport.Submit(scene with
        {
            Inspection = result with
            {
                Summary = "새 결과"
            }
        });
        Assert.Equal("새 결과", Current(viewport).Inspection!.Summary);
        Assert.False(layer.Remove(firstId));
        var id = layer.Snapshot.Items.Values.Single().Id;
        layer.IsVisible = false;
        Assert.Null(Current(viewport).Inspection);
        Assert.Equal(1, layer.Count);
        layer.IsVisible = true;
        Assert.NotNull(Current(viewport).Inspection);
        Assert.True(layer.Remove(id));
        Assert.Null(Current(viewport).Inspection);
        Assert.Same(original, editor.State);
        Assert.Equal(undo, editor.UndoName);
    });

    [Fact]
    public void ChangesPublishWithoutAHostSubmitAndKeepPreviousRenderDataImmutable() => OnSta(() =>
    {
        using var viewport = new Dx12Viewport();
        viewport.Submit(new(new EditorSession().State, null, 0, 200, 200, 1, new()));
        var layer = viewport.Layers.Create("results", LayerSpace.Image);
        var id = layer.Add(new LineGraphic(new(10, 10), new(50, 10), Vector4.One));
        var oldScene = Current(viewport);
        layer.Update(id, new LineGraphic(new(10, 20), new(50, 20), Vector4.One));
        Assert.NotSame(oldScene, Current(viewport));
        Assert.Equal(10, Assert.IsType<LineGraphic>(oldScene.Layers[1].Items.Values.Single().Graphic).Start.Y);
        Assert.Equal(20, Assert.IsType<LineGraphic>(Current(viewport).Layers[1].Items.Values.Single().Graphic).Start.Y);
    });

    private static RenderScene Current(Dx12Viewport viewport)
        => Assert.IsType<RenderScene>(typeof(Dx12Viewport).GetField("_scene", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
