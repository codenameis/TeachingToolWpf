using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace VisionCanvas.Presentation;

/// <summary>모든 WPF 렌더 호스트가 공유하는 원본 픽셀 단위 방향키 이동.</summary>
public static class ViewportKeyboard
{
    /// <summary>텍스트 편집·목록 탐색 키가 ROI 이동으로 전파되지 않도록 View에서 확인한다.</summary>
    public static bool IsControlInput(IInputElement? focused)
    {
        var element = focused as DependencyObject;
        while (element is not null)
        {
            if (element is TextBoxBase or RangeBase or Selector or ButtonBase)
            {
                return true;
            }
            element = element is Visual ? VisualTreeHelper.GetParent(element) :
                (element as FrameworkContentElement)?.Parent;
        }
        return false;
    }

    public static bool Nudge(IViewportInput input, Key key, bool control, bool shift)
    {
        if (control || key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return false;
        }
        var step = shift ? 10 : 1;
        var delta = key switch
        {
            Key.Left => new PointD(-step, 0),
            Key.Right => new(step, 0),
            Key.Up => new(0, -step),
            _ => new(0, step)
        };
        return input.NudgeSelection(delta);
    }
}
