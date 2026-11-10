using System;
using System.Windows;
using System.Windows.Input;
using ToolBlock.Simulator.ViewModels;
using VisionCanvas.Presentation;
using VisionCanvas.Skia.Controls;

namespace ToolBlock.Simulator.Views;

public partial class MainSimulatorWindow : Window
{
    public MainSimulatorWindow()
    {
        InitializeComponent();
    }

    public SkiaViewport Viewport => Canvas;

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (!ViewportKeyboard.IsControlInput(Keyboard.FocusedElement) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
            DataContext is MainSimulatorViewModel viewModel)
        {
            args.Handled = viewModel.HandleKey(
                args.Key,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        Canvas.Dispose();
    }
}
