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
Render("homework-selected.png", HomeworkSelected(), ThemeVariant.Dark, 920, 420);
RenderToastActions();
RenderShell();// 全局缩放：确认整窗缩放后没有渲染异常（黑块 / 布局塌陷）
ContentZoom.Scale = 1.3;
RenderShell("shell-zoom130.png");
ContentZoom.Scale = 1.0;
Render("settings-appearance.png", SettingsView(), ThemeVariant.Dark, 920, 700, scrollTo: 620);
    Render("settings-colors.png", SettingsView(), ThemeVariant.Dark, 920, 700, scrollTo: 300);
    Render("settings-full.png", SettingsView(), ThemeVariant.Light, 920, 4200);   // 整页一图，方便逐段检查
Render("courseware-subjects.png", CoursewareSubjects(), ThemeVariant.Dark, 920, 340);
Render("courseware-timeline.png", CoursewareTimeline(), ThemeVariant.Dark, 920, 520);
Render("timeline-dark.png", TimelineView(), ThemeVariant.Dark, 920, 560);
Render("settings-gating.png", SettingsView(), ThemeVariant.Light, 920, 620);
Render("settings-narrow.png", SettingsView(), ThemeVariant.Light, 620, 620);

void RenderShell(string fileName = "shell-toasts.png")
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
    shell.CaptureRenderedFrame()!.Save(Path.Combine(outDir, fileName));
    Console.WriteLine("saved " + fileName);
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

Control CoursewareSubjects()
{
    var vm = BuildCoursewareVm();
    return new CoursewareListView { DataContext = vm };
}

Control CoursewareTimeline()
{
    var vm = BuildCoursewareVm();
    vm.OpenSubject("数学");
    return new CoursewareListView { DataContext = vm };
}

/// <summary>造一份真实的归档目录（含 meta.json），走 RebuildFromArchive 建索引。</summary>
CoursewareViewModel BuildCoursewareVm()
{
    var root = Path.Combine(Path.GetTempPath(), "ui-preview-archive");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    var seed = new (string Subject, string Teacher, string Name, string At)[]
    {
        ("数学", "张老师", "第3章 函数.pptx", "2026-09-26T10:05:00+08:00"),
        ("数学", "张老师", "第3章 函数（答案）.pdf", "2026-09-26T08:30:00+08:00"),
        ("数学", "张老师", "第2章 数列.pptx", "2026-09-24T15:20:00+08:00"),
        ("语文", "李老师", "古诗默写范围.docx", "2026-09-26T14:10:00+08:00"),
        ("物理", "王老师", "实验报告模板.docx", "2026-09-25T11:00:00+08:00")
    };
    var i = 0;
    foreach (var (subject, teacher, name, at) in seed)
    {
        var dir = Path.Combine(root, subject);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        var time = DateTimeOffset.Parse(at);
        // 用真实的 ArchiveMeta + 默认序列化（FileArchive 写出来就是这样，大小写要一致）
        File.WriteAllText(path + ".meta.json", System.Text.Json.JsonSerializer.Serialize(new ArchiveMeta
        {
            FileId = "f" + (++i), FileName = name, Size = 2_400_000 + i * 120_000,
            GroupId = 123456, SenderQq = 10001, SenderName = teacher, Subject = subject,
            Time = time, LocalPath = path
        }));
    }
    SmartClassroom.App.Runtime.Courseware.RebuildFromArchive(root);
    var vm = new CoursewareViewModel();
    vm.GroupBySubject(SmartClassroom.App.Runtime.Courseware.QueryAll()
        .Where(f => f.LocalPath is not null)
        .Select(f => (f.Subject ?? "", f.FileName, f.LocalPath!, f.Size, f.ArchivedAt)));
    return vm;
}

Control SettingsView(double scrollTo = 0)
{
    // 每次都从"什么都没配"的空白设置开始，这样看到的正是功能开关被拦下的状态。
    var path = Path.Combine(Path.GetTempPath(), "ui-preview-settings.json");
    if (File.Exists(path)) File.Delete(path);
    return new SmartClassroom.App.Views.SettingsView { DataContext = new SettingsViewModel(path) };
}

Control HomeworkSelected()
{
    var store = new SmartClassroom.Core.HomeworkStore();
    store.AddOrMerge(new SmartClassroom.Contracts.HomeworkItem
    {
        HomeworkId = "h1", Subject = "英语", Date = DateOnly.FromDateTime(DateTime.Now),
        Items = ["111"],
        Sender = new SmartClassroom.Contracts.SenderInfo { UserId = 0, TeacherName = "手动添加" },
        Source = new SmartClassroom.Contracts.MessageRef { GroupId = 0, MessageId = 0 }
    });
    var vm = new HomeworkViewModel(store, () => new Dictionary<string, string>());
    var view = new HomeworkView { DataContext = vm };
    vm.Select(vm.Items[0]);   // 选中第一张
    vm.Refresh();             // 模拟拖拽结束/定时刷新后的重建
    return view;
}

void RenderToastActions()
{
    // 一条带动作按钮的通知（"重启以更新 / 稍后重启"）长什么样
    SmartClassroom.App.Toasts.Items.Clear();
    // 只留这一条，免得被前面步骤的通知挤下去
    SmartClassroom.App.Toasts.ShowWithActions("更新 0.50.11 已就绪", "重启应用即可用上新版本",
        [
            new SmartClassroom.App.ToastAction("重启以更新", () => { }, Accent: true),
            new SmartClassroom.App.ToastAction("稍后重启", () => { })
        ],
        NoticeSeverity.Success);
    // Show 是 Post 到 UI 线程的，先把队列跑干净再加等动画
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    System.Threading.Thread.Sleep(400);
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    RenderShell("toast-actions.png");
    SmartClassroom.App.Toasts.Items.Clear();
}
