using System.Windows.Input;

namespace VisionCanvas.Commands;

/// <summary>
/// Contract for MVVM commands whose <see cref="ICommand.CanExecute"/> state can be explicitly refreshed.
/// </summary>
public interface IRefreshableCommand : ICommand
{
    void Refresh();
}
