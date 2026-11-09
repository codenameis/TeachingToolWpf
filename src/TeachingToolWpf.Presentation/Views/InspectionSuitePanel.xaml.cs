using System.Windows.Controls;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Views;

public partial class InspectionSuitePanel : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public InspectionSuitePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is InspectionSuiteViewModel previous)
            {
                previous.SetInputError(false);
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
        if (DataContext is InspectionSuiteViewModel model)
        {
            model.SetInputError(_errors.Count > 0);
        }
    }
}
