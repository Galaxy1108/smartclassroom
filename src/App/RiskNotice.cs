using SmartClassroom.Core;
using SmartClassroom.App.ViewModels;
using SmartClassroom.App.Views;

namespace SmartClassroom.App;

/// <summary>
/// 首次启动的封号风险警告。
///
/// 封号风险必须让用户在动手之前看到，而不是等他点了「启动」才弹；
/// 但只自动弹一次——每次启动都弹就是骚扰，而且「启动」按钮仍然会把关。
/// </summary>
public static class RiskNotice
{
    /// <summary>是否需要在启动时弹（纯逻辑，可测）。</summary>
    public static bool ShouldShow(AppSettings settings) => !settings.RiskWarningShown;

    /// <summary>启动时调用：弹一次风险警告，并把"已弹过"落盘。</summary>
    public static async Task ShowOnFirstLaunchAsync()
    {
        var settings = Runtime.Settings;
        if (!ShouldShow(settings))
            return;

        settings.RiskWarningShown = true;      // 弹过就不再自动弹（无论用户选哪个）
        Save(settings);

        var accepted = await Dialogs.ConfirmAsync("风险警告",
            SettingsViewModel.RiskWarningText, "我已知晓并接受", "以后再说");

        if (accepted)
        {
            settings.RiskAccepted = true;      // 接受后「启动」不再重复确认
            Save(settings);
            Toasts.Success("已确认风险");
        }
        else
        {
            Runtime.Feed.Append("ui", "风险警告已展示，尚未接受", "点「启动」时仍会再次确认",
                ActivitySeverity.Info);
        }
    }

    private static void Save(AppSettings settings)
    {
        try { SettingsStore.Save(settings); } catch { /* 落盘失败不影响启动 */ }
    }
}
