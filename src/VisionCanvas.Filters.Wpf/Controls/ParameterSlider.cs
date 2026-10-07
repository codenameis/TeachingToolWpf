using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VisionCanvas.Filters.Wpf.Controls;

/// <summary>Translates input gesture boundaries into MVVM commands; it owns no recipe or processing state.</summary>
public sealed class ParameterSlider : Slider
{
    public static readonly DependencyProperty BeginEditCommandProperty = DependencyProperty.Register(
        nameof(BeginEditCommand), typeof(ICommand), typeof(ParameterSlider));
    public static readonly DependencyProperty EndEditCommandProperty = DependencyProperty.Register(
        nameof(EndEditCommand), typeof(ICommand), typeof(ParameterSlider));
    public static readonly DependencyProperty CancelEditCommandProperty = DependencyProperty.Register(
        nameof(CancelEditCommand), typeof(ICommand), typeof(ParameterSlider));
    private bool _editing;

    public ICommand? BeginEditCommand
    {
        get => (ICommand?)GetValue(BeginEditCommandProperty);
        set => SetValue(BeginEditCommandProperty, value);
    }
    public ICommand? EndEditCommand
    {
        get => (ICommand?)GetValue(EndEditCommandProperty);
        set => SetValue(EndEditCommandProperty, value);
    }
    public ICommand? CancelEditCommand
    {
        get => (ICommand?)GetValue(CancelEditCommandProperty);
        set => SetValue(CancelEditCommandProperty, value);
    }

    public ParameterSlider() => Unloaded += (_, _) => Finish(cancel: true);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Begin();
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        Finish();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsMouseCaptureWithin)
        {
            Finish();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _editing)
        {
            Finish(cancel: true);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            Begin();
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            Finish();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Finish();
    }

    private void Begin()
    {
        if (!_editing && BeginEditCommand?.CanExecute(null) == true)
        {
            _editing = true;
            BeginEditCommand.Execute(null);
        }
    }

    private void Finish(bool cancel = false)
    {
        if (!_editing)
        {
            return;
        }
        _editing = false;
        var command = cancel ? CancelEditCommand : EndEditCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
