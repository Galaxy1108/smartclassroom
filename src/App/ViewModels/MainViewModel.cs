namespace SmartClassroom.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private string _statusText = "运行中 · QQ 未连接 · ClassIsland 未连接";

    public string Title => "智慧课堂";

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }
}
