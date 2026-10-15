using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RoiHierarchyRulesSmoke
{
    public static async Task Run(MainWindowViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        model.Fit.Execute(null);
        var root = editor.Add(new(new(4096, 4096), 6500, 6500, 0));
        var module = editor.AddChild(root, new(new(4096, 4096), 5000, 5000, 0));
        var group = editor.AddChild(module, new(new(4096, 4096), 3500, 3500, 0));
        var leaf = editor.AddChild(group, new(new(4096, 4096), 1500, 1500, 0));
        model.DrawTool.Execute(null);
        Require(model.Input.DrawParentId is null, "a leaf selection does not create a fifth level");
        model.SelectTool.Execute(null);
        editor.Select([]);
        await CaptureAndCheck("default");

        var before = editor.State;
        model.StyleSettings.Module.Color = "#12A4F0";
        model.StyleSettings.Root.Color = "#E89012";
        model.StyleSettings.Apply.Execute(null);
        Require(string.IsNullOrEmpty(model.StyleSettings.Error), "settings valid");
        Require(ReferenceEquals(before, editor.State), "color changes preserve document and history");
        await CaptureAndCheck("custom");

        await CheckGroupDrag(group, module, new(2900, 4096));
        await CheckGroupDrag(module, root, new(2000, 4096));
        await CheckGroupDrag(leaf, group, new(4096, 4096));
        await CheckSlideAndMultipleSelection();
        editor.Select([leaf]);
        var beforePick = editor.State.Rois;
        var pickHistory = editor.HistoryBytes;
        foreach (var expected in new[] { group, module, root, leaf })
        {
            var point = editor.State.Viewport.ImageToScreen(new(4096, 4096));
            EditingSmokeScenario.Pointer(viewport, 0x201, point);
            EditingSmokeScenario.Pointer(viewport, 0x202, point);
            EditingSmokeScenario.Pointer(viewport, 0x203, point);
            EditingSmokeScenario.Pointer(viewport, 0x202, point);
            await model.EditCompletion;
            Require(editor.State.Selection.SetEquals([expected]), "native double-click cycles nested groups without list selection");
            Require(ReferenceEquals(beforePick, editor.State.Rois) && pickHistory == editor.HistoryBytes, "selection preserves geometry and history");
        }
        editor.Select([module]);
        model.Properties.Width = "7000";
        model.Properties.Height = "7000";
        model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[root].Geometry.Covers(editor.State.Rois[module].Geometry), "numeric module resize stays inside root");
        await model.Undo.ExecuteAsync();
        editor.Select([module]);
        Require(model.Ungroup.CanExecute(null), "linked module can be ungrouped");
        await model.Ungroup.ExecuteAsync();
        Require(editor.State.Rois[group].ParentId == root, "ungroup releases ROI group from module and preserves outer root");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[group].ParentId == module, "ungroup Undo restores module membership");

        editor.Select([leaf]);
        model.Properties.SelectedType = 1;
        Require(editor.State.Rois[leaf].SearchRange is not null, "search type applies immediately");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[leaf].SearchRange is null, "type Undo");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[leaf].SearchRange is not null, "type Redo");

        var standalone = editor.Add(new(new(500, 500), 100, 100, 0));
        editor.Select([group, standalone]);
        var rejected = editor.State;
        var rejectedHistory = editor.HistoryBytes;
        try
        {
            await model.Group.ExecuteAsync();
            throw new InvalidOperationException("Expected expansion outside module to fail.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("상위 그룹"))
        {
        }
        Require(ReferenceEquals(rejected, editor.State) && rejectedHistory == editor.HistoryBytes,
            "expansion outside module rejects the whole grouping");
        editor.Select([standalone]);
        editor.TranslateSelection(new(1700, 3596));
        var standaloneGeometry = editor.State.Rois[standalone].Geometry;
        var groupGeometry = editor.State.Rois[group].Geometry;
        model.ChooseChildRois.Execute(null);
        editor.Select([group]);
        await model.AttachToParent.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId == group && editor.State.Rois[standalone].Geometry == standaloneGeometry &&
            editor.State.Rois[group].Geometry.Covers(standaloneGeometry) && editor.State.Rois[group].Geometry.Covers(groupGeometry),
            "existing group expands while the attached ROI stays in place");
        editor.Select([standalone, leaf]);
        Require(!model.Group.CanExecute(null), "parented children cannot regroup");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId is null && editor.State.Rois[group].Geometry == groupGeometry, "attach Undo restores group size");
        await model.Redo.ExecuteAsync();
        Require(editor.State.Rois[group].Geometry.Covers(standaloneGeometry), "attach Redo restores expansion");
        await model.Undo.ExecuteAsync();
        editor.Select([standalone]);
        editor.TranslateSelection(new(-1700, -3596));
        editor.Select([root, standalone]);
        var count = editor.State.Rois.Count;
        var originalRoot = editor.State.Rois[root];
        Require(model.Group.CanExecute(null), "existing parent accepts standalone");
        await model.Group.ExecuteAsync();
        Require(editor.State.Rois.Count == count && editor.State.Rois[root].Geometry.Covers(originalRoot.Geometry) &&
            editor.State.Rois[root].Geometry.Covers(editor.State.Rois[standalone].Geometry) &&
            editor.State.Rois[standalone].ParentId == root, "group expands existing parent without creating an ROI");
        await model.Undo.ExecuteAsync();
        Require(editor.State.Rois[standalone].ParentId is null, "group attachment Undo");
        editor.AddChild(standalone, new(new(500, 500), 20, 20, 0));
        editor.Select([root, standalone]);
        Require(!model.Group.CanExecute(null), "fifth-level group button disabled");
        await CheckFastBoundaryInput();
        File.AppendAllText("artifacts/smoke.log", "PASS: hierarchy colors/settings, leaf/group boundaries, wall sliding, no multi-selection rotation, native double-click cycle, fast move/rotation without jumps, outside attachment, depth and history" + Environment.NewLine);

        async Task CheckSlideAndMultipleSelection()
        {
            editor.Select([leaf]);
            var original = editor.State.Rois[leaf].Geometry;
            var start = editor.State.Viewport.ImageToScreen(original.Center);
            var target = editor.State.Viewport.ImageToScreen(original.Center + new PointD(3000, 600));
            EditingSmokeScenario.Pointer(viewport, 0x201, start);
            EditingSmokeScenario.Pointer(viewport, 0x200, target);
            var shown = editor.TransformPreview!.Geometry(editor.State.Rois[leaf]);
            Require(editor.State.Rois[group].Geometry.Covers(shown), "native slide stays in parent");
            Require(Math.Abs(shown.Center.X - 5096) < 20 && Math.Abs(shown.Center.Y - 4696) < 20,
                "native diagonal drag follows the wall to the requested Y");
            EditingSmokeScenario.Pointer(viewport, 0x202, target);
            await model.EditCompletion;
            Require(shown == editor.State.Rois[leaf].Geometry, $"native slide commit preserves preview: shown {shown}, committed {editor.State.Rois[leaf].Geometry}");
            await model.Undo.ExecuteAsync();
            Require(editor.State.Rois[leaf].Geometry == original, "native slide Undo");

            editor.Select([root, module]);
            Require(!model.RotateLeft.CanExecute(null) && !model.RotateRight.CanExecute(null) && !model.ResetAngle.CanExecute(null),
                "multiple selection disables rotation commands");
            var bounds = editor.State.SelectedSpatial.Bounds;
            var formerHandle = editor.State.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28);
            var bitmap = await CaptureBitmap(viewport, Path.GetFullPath("artifacts/hierarchy-multi-selection.png"));
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Require(!HasColor(pixels, bitmap.PixelWidth, bitmap.PixelHeight, formerHandle * viewport.Dpi,
                model.StyleSettings.Palette.Default.MultiSelectedColor), "multiple selection has no rotation handle pixels");
            var before = editor.State.Rois;
            EditingSmokeScenario.Pointer(viewport, 0x201, formerHandle);
            EditingSmokeScenario.Pointer(viewport, 0x200, formerHandle + new PointD(50, 30));
            Require(editor.TransformPreview is null || editor.TransformPreview.Transform.AngleDegrees == 0,
                "former multiple selection handle cannot rotate");
            EditingSmokeScenario.Pointer(viewport, 0x202, formerHandle + new PointD(50, 30));
            await model.EditCompletion;
            Require(ReferenceEquals(before, editor.State.Rois), "former handle preserves geometry");
        }

        async Task CheckFastBoundaryInput()
        {
            var parent = editor.AddParent(new(new(4096, 4096), 2200, 700, 0));
            var child = editor.AddChild(parent, new(new(4096, 4096), 1900, 300, 0));
            var before = editor.State;
            var pivot = before.Viewport.ImageToScreen(before.Rois[child].Geometry.Center);
            var handle = HandleGeometry.RotationHandle(before.Rois[child].Geometry, before.Viewport);
            EditingSmokeScenario.Pointer(viewport, 0x201, handle);
            foreach (var angle in new[] { 45d, 170, 180 })
            {
                EditingSmokeScenario.Pointer(viewport, 0x200, pivot + (handle - pivot).Rotate(angle));
                Require(Math.Abs(editor.TransformPreview!.Transform.AngleDegrees) < 20, "fast native rotation stops at first exit");
            }
            var shown = editor.TransformPreview!.Geometry(before.Rois[child]);
            EditingSmokeScenario.Pointer(viewport, 0x202, pivot + (handle - pivot).Rotate(180));
            await model.EditCompletion;
            Require((shown.Center - editor.State.Rois[child].Geometry.Center).Length < .001 &&
                Math.Abs(shown.Angle - editor.State.Rois[child].Geometry.Angle) < .001, "native rotation preview equals commit");
            await model.Undo.ExecuteAsync();

            var concave = editor.AddParent(RoiGeometry.Polygon([new(1000, 1000), new(7000, 1000), new(7000, 7000),
                new(5000, 7000), new(5000, 2500), new(3000, 2500), new(3000, 7000), new(1000, 7000)]));
            var inner = editor.AddChild(concave, new(new(2000, 5500), 400, 400, 0));
            var start = editor.State.Viewport.ImageToScreen(new(2000, 5500));
            EditingSmokeScenario.Pointer(viewport, 0x201, start);
            foreach (var x in new[] { 3100d, 6000, 9000, 5990 })
            {
                EditingSmokeScenario.Pointer(viewport, 0x200, editor.State.Viewport.ImageToScreen(new(x, 5500)));
                Require(editor.TransformPreview!.Geometry(editor.State.Rois[inner]).Center.X <= 2800.001, "fast native move cannot cross concave gap");
            }
            var displayed = editor.TransformPreview!.Geometry(editor.State.Rois[inner]);
            EditingSmokeScenario.Pointer(viewport, 0x202, editor.State.Viewport.ImageToScreen(new(5990, 5500)));
            await model.EditCompletion;
            Require(displayed == editor.State.Rois[inner].Geometry, "native move preview equals commit");
            await model.Undo.ExecuteAsync();
            Require(editor.State.Rois[inner].Geometry.Center == new PointD(2000, 5500), "native fast move Undo");

            EditingSmokeScenario.Pointer(viewport, 0x201, start);
            foreach (var waypoint in new PointD[] { new(2000, 1800), new(6000, 1800), new(6000, 5500) })
            {
                EditingSmokeScenario.Pointer(viewport, 0x200, editor.State.Viewport.ImageToScreen(waypoint));
                var preview = editor.TransformPreview!.Geometry(editor.State.Rois[inner]);
                Require((preview.Center - waypoint).Length < 20 && editor.State.Rois[concave].Geometry.Covers(preview),
                    "native drag can follow the interior around a concave gap");
            }
            displayed = editor.TransformPreview!.Geometry(editor.State.Rois[inner]);
            EditingSmokeScenario.Pointer(viewport, 0x202, editor.State.Viewport.ImageToScreen(new(6000, 5500)));
            await model.EditCompletion;
            Require(displayed == editor.State.Rois[inner].Geometry, "native bent path does not snap back on commit");
            await model.Undo.ExecuteAsync();
            Require(editor.State.Rois[inner].Geometry.Center == new PointD(2000, 5500), "native bent path Undo");
        }

        async Task CheckGroupDrag(Guid id, Guid parent, PointD point)
        {
            editor.Select([id]);
            var original = editor.State.Rois[id].Geometry;
            void Pointer(int message, PointD imagePoint) => EditingSmokeScenario.Pointer(viewport, message, editor.State.Viewport.ImageToScreen(imagePoint));
            Pointer(0x201, point);
            Pointer(0x200, point + new PointD(5000, 0));
            Pointer(0x202, point + new PointD(5000, 0));
            await model.EditCompletion;
            Require(editor.State.Rois[id].Geometry != original, "native group drag moves within available space");
            Require(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[id].Geometry), "native group drag remains contained");
            Require(editor.State.Rois[id].ParentId == parent, "drag preserves group membership");
            await model.Undo.ExecuteAsync();
            Require(editor.State.Rois[id].Geometry == original, "native constrained drag Undo");
            await model.Redo.ExecuteAsync();
            Require(editor.State.Rois[parent].Geometry.Covers(editor.State.Rois[id].Geometry), "native constrained drag Redo");
            await model.Undo.ExecuteAsync();
        }

        async Task CaptureAndCheck(string name)
        {
            var path = Path.GetFullPath($"artifacts/hierarchy-{name}.png");
            var bitmap = await CaptureBitmap(viewport, path);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            foreach (var (id, role) in new[] { (root, RoiRole.Root), (module, RoiRole.Module), (group, RoiRole.Parent), (leaf, RoiRole.Child) })
            {
                var geometry = editor.State.Rois[id].Geometry;
                var top = editor.State.Viewport.ImageToScreen(geometry.Center - new PointD(0, geometry.Height / 2));
                Require(HasColor(pixels, bitmap.PixelWidth, bitmap.PixelHeight, top * viewport.Dpi, model.StyleSettings.Palette.For(role).Color), $"{name} {role} stroke pixels");
            }
        }
    }

    internal static async Task<BitmapSource> CaptureBitmap(Dx12Viewport viewport, string path)
    {
        var timer = Stopwatch.StartNew();
        viewport.Capture(path);
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 32 && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                // 파일 생성 직후에는 캡처 worker가 쓰기 잠금을 보유한다. 공유 위반만 제한 시간 내 다시 읽는다.
            }
        }
        throw new TimeoutException($"ROI capture timed out: {path}");
    }

    internal static bool HasColor(byte[] pixels, int width, int height, PointD point, Vector4 expected)
    {
        for (var y = Math.Max(0, (int)point.Y - 5); y <= Math.Min(height - 1, (int)point.Y + 5); y++)
        {
            for (var x = Math.Max(0, (int)point.X - 5); x <= Math.Min(width - 1, (int)point.X + 5); x++)
            {
                var offset = ((y * width) + x) * 4;
                if (Math.Abs(pixels[offset] - (255 * expected.Z)) < 12 &&
                    Math.Abs(pixels[offset + 1] - (255 * expected.Y)) < 12 &&
                    Math.Abs(pixels[offset + 2] - (255 * expected.X)) < 12)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ROI hierarchy smoke: " + message);
        }
    }
}
