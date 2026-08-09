using CodexAgentSwitch.Application.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexAgentSwitch.App.ViewModels;

internal static class UiPresentation
{
    public static Brush ToneBrush(UiStatusTone tone) =>
        (Brush)Microsoft.UI.Xaml.Application.Current.Resources[tone switch
        {
            UiStatusTone.Success => "SuccessBrush",
            UiStatusTone.Info => "AccentFillColorDefaultBrush",
            UiStatusTone.Warning => "WarningBrush",
            UiStatusTone.Error => "DangerBrush",
            _ => "TextFillColorSecondaryBrush",
        }];

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm") ?? "暂无记录";

    public static string Duration(DateTimeOffset? startedAt, DateTimeOffset? completedAt = null)
    {
        if (startedAt is null)
        {
            return "尚未开始";
        }

        var elapsed = (completedAt ?? DateTimeOffset.Now) - startedAt.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
