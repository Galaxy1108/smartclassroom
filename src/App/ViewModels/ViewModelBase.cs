using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartClassroom.App.ViewModels;

/// <summary>v0.1 起使用手写 INPC，避免源生成器对新版编译器的依赖。</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
