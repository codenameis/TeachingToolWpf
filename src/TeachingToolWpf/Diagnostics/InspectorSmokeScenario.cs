using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class InspectorSmokeScenario
{
    public static async Task Run(Window window, EditorViewModel model)
    {
        var editor = model.Session;
        editor.AddRange(Enumerable.Range(0, 4600).Select(i => new RoiGeometry(new(50 + (i % 80 * 80), 50 + (i / 80 * 80)), 30, 30, 0)));
        var rois = editor.State.Rois.Values.OrderBy(r => r.Order).ToArray();
        editor.Select([rois[0].Id]);
        model.NextRowPage.Execute(null);
        var page = model.VisibleRows;
        editor.SetViewport(editor.State.Viewport.Pan(new(2, 0)));
        Require(ReferenceEquals(page, model.VisibleRows), "pan preserves a manually chosen list page");
        editor.Select([rois[0].Id, rois[550].Id]);
        model.PreviousRowPage.Execute(null);
        window.UpdateLayout();
        var list = Descendants(window).OfType<RoiListBox>().Single();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = item
        });
        Require(editor.State.Selection.SetEquals([rois[0].Id]), "plain list click clears off-page selection even without SelectionChanged");
        var field = Descendants(window).OfType<ValueTextBox>().Single(f => AutomationProperties.GetName(f) == "ROI 이름");
        Keyboard.Focus(field);
        Require(field.IsKeyboardFocused && field.Text == model.Properties.Name, "inspector focus loads the current editable value");
        field.Text = "한글 부품 검사";
        Require(model.Properties.Name == "한글 부품 검사", "typing updates the bound property draft");
        Keyboard.ClearFocus();
        model.ApplyProperties.Execute(null);
        Require(editor.State.Rois[rois[0].Id].Name == "한글 부품 검사", "Apply commits the inspector value");
        await model.Undo.ExecuteAsync();
        Require(field.Value == rois[0].Name, "Undo refreshes the inactive displayed field");
        editor.Select(editor.State.Rois.Keys);
        await model.Delete.ExecuteAsync();
        Require(editor.State.Rois.Count == 0, "smoke fixture cleanup");
    }

    private static void Require(bool value, string description)
    {
        if (!value)
        {
            throw new InvalidOperationException("Inspector smoke assertion: " + description);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0;
i < VisualTreeHelper.GetChildrenCount(root);
i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
