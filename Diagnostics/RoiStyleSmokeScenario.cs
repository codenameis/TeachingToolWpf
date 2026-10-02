using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Rendering;
using TeachingToolWpf.Views;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Captures role strokes on the real renderer and verifies hierarchy/history independently of pixels.</summary>
internal static class RoiStyleSmokeScenario
{
    public static async Task Run(Window window, EditorViewModel model, Dx12Viewport viewport)
    {
        var editor = model.Session;
        Require(editor.State.Rois.Count == 0, "style fixture starts with an empty document");
        var originalView = editor.State.Viewport;
        var settings = model.StyleSettings;
        var originalDefault = settings.DefaultThickness;
        var originalChild = settings.ChildThickness;
        var originalParent = settings.ParentThickness;
        string? renderError = null;
        string? failure = null;
        var newErrors = new List<string>();
        long? baselineCapturedEndTicks = null, styleApplyStartTicks = null, styleCapturedEndTicks = null;
        var completed = false;
        void OnStatus(string status)
        {
            if (status.StartsWith("렌더 오류:")) renderError = status;
            if (status.StartsWith("렌더 오류:") || status.StartsWith("D3D12 ")) newErrors.Add(status);
        }
        viewport.RendererStatusChanged += OnStatus;
        try
        {
            model.Fit.Execute(null);
            var parent = editor.Add(new(new(2500, 3200), 2400, 2200, 0));
            editor.Rename(parent, "큰 영역");
            var child = editor.Add(new(new(2400, 3100), 600, 500, 0));
            editor.Rename(child, "내부 영역");
            var standalone = editor.Add(new(new(6000, 5400), 800, 600, 0));
            editor.Rename(standalone, "독립 영역");
            Require(!editor.State.Rois[parent].IsGroup, "a natural containing ROI is a parent without group metadata");
            AssertRoles(parent, child, standalone, attached: true);
            editor.Select([]);
            var panel = Descendants(window).OfType<RoiStylePanel>().Single();
            Require(ReferenceEquals(panel.DataContext, settings), "settings panel uses the active style view model");
            panel.BringIntoView(); window.UpdateLayout();
            await Capture("baseline");
            baselineCapturedEndTicks = Stopwatch.GetTimestamp();
            var beforeStyle = editor.State; var historyBytes = editor.HistoryBytes; var undoName = editor.UndoName;
            EnterThickness("일반 ROI 선 두께", "2");
            EnterThickness("자식 ROI 선 두께", "3");
            EnterThickness("부모 ROI 선 두께", "4");
            Keyboard.ClearFocus();
            Require(settings.DefaultThickness == "2" && settings.ChildThickness == "3" && settings.ParentThickness == "4",
                "actual ValueTextBox input updates all three bindings");
            var applyButton = Descendants(panel).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "ROI 표시 설정 적용");
            Require(ReferenceEquals(applyButton.Command, settings.Apply) && applyButton.IsEnabled, "visible Apply button uses the style command");
            var invoke = new ButtonAutomationPeer(applyButton).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Style Apply button does not provide an invoke pattern.");
            styleApplyStartTicks = Stopwatch.GetTimestamp();
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(settings.Palette.Default.ThicknessDip == 2 && settings.Palette.Child.ThicknessDip == 3 && settings.Palette.Parent.ThicknessDip == 4,
                "three independent width fields apply atomically");
            Require(ReferenceEquals(beforeStyle, editor.State) && historyBytes == editor.HistoryBytes && undoName == editor.UndoName,
                "style preferences do not edit the document or history");
            Require(settings.Palette.Default.Color == RoiStylePalette.Standard.Default.Color &&
                settings.Palette.Child.SelectedColor == RoiStylePalette.Standard.Child.SelectedColor &&
                settings.Palette.Parent.SelectedColor == RoiStylePalette.Standard.Parent.SelectedColor,
                "width changes preserve normal and selected role colors");
            Require(settings.Palette.Parent.StrokeThickness(true) == 4.5f && settings.Palette.Child.StrokeThickness(true) == 3.5f,
                "selection adds half a DIP independently of each role width");
            await Capture("roles");
            styleCapturedEndTicks = Stopwatch.GetTimestamp();
            panel.BringIntoView(); window.UpdateLayout(); await Task.Delay(100);
            WindowCapture.Save(window, viewport, "artifacts/roi-style-roles.png", "artifacts/roi-style-settings.png");
            editor.Select([parent, child, standalone]);
            await Capture("selected");
            editor.Select([parent]);
            await Capture("parent-selected");

            editor.Select([child]); model.SelectTool.Execute(null);
            var beforeMove = editor.State;
            var view = beforeMove.Viewport;
            var start = view.ImageToScreen(beforeMove.Rois[child].Geometry.Center);
            var end = view.ImageToScreen(new(5000, 3100));
            EditingSmokeScenario.Pointer(viewport, 0x201, start);
            EditingSmokeScenario.Pointer(viewport, 0x200, end);
            EditingSmokeScenario.Pointer(viewport, 0x202, end);
            await model.EditCompletion;
            Require(!model.Input.IsDragging && !editor.IsPreviewing, "native detach finishes one edit");
            Require(editor.State.Rois[child].Geometry != beforeMove.Rois[child].Geometry, "native drag moved the child");
            AssertRoles(parent, child, standalone, attached: false);
            var detached = editor.State;
            editor.Select([]); await Capture("detached");

            await model.Undo.ExecuteAsync();
            AssertExact(beforeMove, editor.State); AssertRoles(parent, child, standalone, attached: true);
            Require(settings.Palette.Parent.ThicknessDip == 4, "Undo preserves the independent style preference");
            await Capture("undo");
            await model.Redo.ExecuteAsync();
            AssertExact(detached, editor.State); AssertRoles(parent, child, standalone, attached: false);
            var beforeReset = editor.State; historyBytes = editor.HistoryBytes;
            settings.Reset.Execute(null);
            Require(settings.Palette == RoiStylePalette.Standard, "Reset restores the standard palette");
            Require(ReferenceEquals(beforeReset, editor.State) && historyBytes == editor.HistoryBytes, "Reset is not a document edit");
            await Capture("reset");

            editor.Select(editor.State.Rois.Keys); await model.Delete.ExecuteAsync();
            Require(editor.State.Rois.Count == 0, "style fixture cleanup preserves the remaining smoke sequence");
            completed = true;

            void EnterThickness(string name, string value)
            {
                var field = Descendants(panel).OfType<ValueTextBox>().Single(item => AutomationProperties.GetName(item) == name);
                field.BringIntoView(); Keyboard.Focus(field);
                Require(field.IsKeyboardFocused, "style thickness field receives keyboard focus: " + name);
                field.Text = value;
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            model.Input.Cancel();
            settings.DefaultThickness = originalDefault; settings.ChildThickness = originalChild; settings.ParentThickness = originalParent;
            settings.Apply.Execute(null);
            editor.SetViewport(originalView);
            viewport.RendererStatusChanged -= OnStatus;
            File.WriteAllText("artifacts/roi-style-check.json", JsonSerializer.Serialize(new
            {
                completed, baselineCapturedEndTicks, styleApplyStartTicks, styleCapturedEndTicks,
                stopwatchFrequency = Stopwatch.Frequency, adapter = model.GpuStatus, dpi = viewport.Dpi,
                defaultThicknessDip = 2, childThicknessDip = 3, parentThicknessDip = 4,
                settingsCapture = "artifacts/roi-style-settings.png", newErrors, error = failure
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void AssertRoles(Guid parent, Guid child, Guid standalone, bool attached)
        {
            Require(RoiStyleRoles.Resolve(editor.State, parent) == (attached ? RoiRole.Parent : RoiRole.Default), "parent role follows actual children");
            Require(RoiStyleRoles.Resolve(editor.State, child) == (attached ? RoiRole.Child : RoiRole.Default), "child role follows its current parent");
            Require(RoiStyleRoles.Resolve(editor.State, standalone) == RoiRole.Default, "unrelated ROI keeps default role");
        }

        async Task Capture(string name)
        {
            var path = Path.GetFullPath($"artifacts/roi-style-{name}.png");
            if (File.Exists(path)) File.Delete(path);
            viewport.Capture(path);
            for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++)
            {
                if (renderError is not null) throw new InvalidOperationException(renderError);
                await Task.Delay(50);
            }
            if (!File.Exists(path)) throw new TimeoutException("ROI style capture did not complete: " + name);
            if (renderError is not null) throw new InvalidOperationException(renderError);
        }
    }

    private static void AssertExact(DocumentState expected, DocumentState actual)
    {
        Require(expected.Rois.Count == actual.Rois.Count && expected.Selection.SetEquals(actual.Selection) &&
            expected.Rois.All(pair => actual.Rois.TryGetValue(pair.Key, out var roi) && roi == pair.Value),
            "Undo/Redo restores exact IDs, geometry, names, hierarchy and selection");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("ROI style smoke assertion: " + message); }
}
