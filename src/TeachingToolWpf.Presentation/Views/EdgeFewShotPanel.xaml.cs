using System.Windows.Controls;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Views;

public partial class EdgeFewShotPanel : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public EdgeFewShotPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is EdgeFewShotViewModel previous)
            {
                previous.HasInputErrors = false;
            }
            SynchronizeErrors();
        };
    }

    private void OnValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (e.Action == ValidationErrorEventAction.Added)
        {
            _errors.Add(e.Error);
        }
        else
        {
            _errors.Remove(e.Error);
        }
        SynchronizeErrors();
    }

    private void SynchronizeErrors()
    {
        if (DataContext is EdgeFewShotViewModel model)
        {
            model.HasInputErrors = _errors.Count > 0;
        }
    }
}
