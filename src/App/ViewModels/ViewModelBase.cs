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
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>设置页里的一行"科目配色"：改颜色时回调（"默认"表示恢复默认色）。</summary>
public sealed class SubjectColorRow : ViewModelBase
{
    public SubjectColorRow(string subject, string color)
    {
        Subject = subject;
        _color = color;
    }

    public string Subject { get; }
    public IReadOnlyList<string> Choices => SettingsViewModel.ColorChoices;

    public event Action<string, string>? Changed;

    private string _color;
    public string Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value))
                return;
            OnPropertyChanged(nameof(PickerColor));
            Changed?.Invoke(Subject, value);
        }
    }

    /// <summary>
    /// 选色盘用的颜色。设置里存的是 #RRGGBB 字符串，"默认"表示跟随自动配色 ——
    /// 这里映射成 Avalonia 的 Color，用户拖色盘时再写回字符串。
    /// </summary>
    public Avalonia.Media.Color PickerColor
    {
        // "默认"时显示**和卡片一样**的色板色（以前写死蓝色，两边看起来不一致）
        get => Avalonia.Media.Color.TryParse(Color, out var c)
            ? c
            : Avalonia.Media.Color.Parse(HomeworkCard.PaletteColorFor(Subject));
        set
        {
            var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (Color == hex)
                return;
            Color = hex;
            OnPropertyChanged();
        }
    }
}
