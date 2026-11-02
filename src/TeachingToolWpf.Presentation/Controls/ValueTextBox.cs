using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace TeachingToolWpf.Controls;

/// <summary>Displays rapidly changing inspector values without modifying an inactive TSF text store.</summary>
public sealed class ValueTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(ValueTextBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private bool _synchronizing;

    private static void ValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var field = (ValueTextBox)sender;
        if (field.IsKeyboardFocused)
        {
            field.SynchronizeEditor();
        }

        if (UIElementAutomationPeer.FromElement(field) is { } peer)
        {
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, e.OldValue, e.NewValue);
        }
    }

    private void SynchronizeEditor()
    {
        if (Text == Value)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            Text = Value;
            CaretIndex = Text.Length;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        SynchronizeEditor();
        base.OnGotKeyboardFocus(e);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (!_synchronizing && IsKeyboardFocused)
        {
            SetCurrentValue(ValueProperty, Text);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ValuePeer(this);

    private sealed class ValuePeer(ValueTextBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Value => this,
            PatternInterface.Text when !owner.IsKeyboardFocused => null,
            _ => base.GetPattern(patternInterface)
        };

        bool IValueProvider.IsReadOnly => owner.IsReadOnly;

        string IValueProvider.Value => owner.Value;

        void IValueProvider.SetValue(string value)
        {
            if (!owner.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }

            if (owner.IsReadOnly)
            {
                throw new InvalidOperationException("읽기 전용 필드입니다.");
            }

            owner.SetCurrentValue(ValueProperty, value);
        }
    }
}
