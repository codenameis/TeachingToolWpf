using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeachingToolWpf.Presentation;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T storage, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value; Notify(property); return true;
    }
}
