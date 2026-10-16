using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace VisionAnomaly.Controls;

/// <summary>Time/ShortTitle 표시 속성을 가진 외부 항목을 사용한다. 모델·알람 판정은 소유하지 않는다.</summary>
public partial class AlarmTimelineControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(AlarmTimelineControl));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(AlarmTimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value);
    }
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value);
    }
    public AlarmTimelineControl() => InitializeComponent();
}
