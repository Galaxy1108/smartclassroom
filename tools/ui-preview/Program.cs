// 界面预览工具：在 headless + Skia 下把页面渲染成 PNG，用来肉眼检查配色与排版
// （不需要桌面环境，也不会真的连 QQ / ClassIsland）。
//
// 用法：dotnet run --project tools/ui-preview/UiPreview.csproj -- artifacts
//
// 注意：headless 下 Window.Show() 的首帧用的**不是** Width 指定的尺寸，
// 所以必须在 Show 之后改一次尺寸触发重新布局+重绘，捕获到的画面才和真实窗口一致。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SmartClassroom.App;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;
using SmartClassroom.Core;

var outDir = args.Length > 0 ? args[0] : "artifacts";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<SmartClassroom.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

// headless Skia 建不出 FluentAvalonia 内嵌 Symbols 字体的 glyphTypeface，换系统字体顶上。
Application.Current!.Resources["SymbolThemeFontFamily"] = new FontFamily("DejaVu Sans");

Render("timeline-light.png", TimelineView(), ThemeVariant.Light, 920, 560);
RenderShell();
Render("settings-full.png", SettingsView(), ThemeVariant.Light, 920, 3200);   // 整页一图，方便逐段检查
Render("timeline-dark.png", TimelineView(), ThemeVariant.Dark, 920, 560);
Render("settings-gating.png", SettingsView(), ThemeVariant.Light, 920, 620);
Render("settings-narrow.png", SettingsView(), ThemeVariant.Light, 620, 620);

void RenderShell()
{
    Toasts.Items.Clear();
    Toasts.Success("SnowLuma 已停止", "注入已关闭，QQ 恢复原状。");
    Toasts.Error("AI 测试失败", "AI 请求失败：400: MissingSessionID（该端点要求 x-opencode-session 路由头）");
    Toasts.Warn("注入的 QQ 账号与所选不一致", "当前在线 10002，你在设置里选的是 10001。");

    var shell = new MainWindow { DataContext = new MainViewModel(), RequestedThemeVariant = ThemeVariant.Dark };
    shell.Width = 920;
    shell.Height = 560;
    shell.Show();
    shell.Width = 921;
    shell.Width = 920;
    Settle();
    shell.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "shell-toasts.png"));
    Console.WriteLine("saved shell-toasts.png");
    shell.Close();
}

void Render(string fileName, Control content, ThemeVariant variant, int width, int height, double scrollTo = 0)
{
    var window = new Window
    {
        Width = 800,          // 故意先给个别的尺寸：Show() 之后改尺寸才会真正重排
        Height = height,
        RequestedThemeVariant = variant,
        Background = new SolidColorBrush(variant == ThemeVariant.Dark
            ? Color.Parse("#202020") : Color.Parse("#F3F3F3")),
        Content = content
    };
    window.Show();
    window.Width = width;
    window.Height = height;
    Settle();
    if (scrollTo > 0 && ((Visual)content).GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sv)
    {
        sv.Offset = new Vector(0, scrollTo);
        Settle();
    }
    window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, fileName));
    Console.WriteLine("saved " + fileName);
    window.Close();
}

void Settle()
{
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}

Control TimelineView()
{
    var feed = new ActivityFeed();
    feed.Append("ai", "AI 引擎：内置直连", "deepseek-chat", ActivitySeverity.Info);
    feed.Append("state", "已恢复上次状态", "作业 3 条 / 待处理 1 条 / 事件 12 条", ActivitySeverity.Info);
    feed.Append("courseware", "本节课没有科目信息，已跳过课件弹窗", "可在教师映射里补上该老师的科目",
        ActivitySeverity.Warning);
    feed.Append("file", "已归档：第3章 函数.pptx", "来自张老师", ActivitySeverity.Success);
    feed.Append("crash", "后台任务异常",
        "InvalidOperationException: org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable",
        ActivitySeverity.Error);
    return new EventsView { DataContext = new EventsViewModel(feed, new PendingStore(), null) };
}

Control SettingsView(double scrollTo = 0)
{
    // 每次都从"什么都没配"的空白设置开始，这样看到的正是功能开关被拦下的状态。
    var path = Path.Combine(Path.GetTempPath(), "ui-preview-settings.json");
    if (File.Exists(path)) File.Delete(path);
    return new SmartClassroom.App.Views.SettingsView { DataContext = new SettingsViewModel(path) };
}
