using System.Windows;
using System.Windows.Controls;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Views;

public partial class PrimitiveShapeDetectionPanel : UserControl
{
    private readonly HashSet<ValidationError> _errors = [];

    public PrimitiveShapeDetectionPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is PrimitiveShapeDetectionViewModel previous)
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
        if (DataContext is PrimitiveShapeDetectionViewModel model)
        {
            model.HasInputErrors = _errors.Count > 0;
        }
    }
}
