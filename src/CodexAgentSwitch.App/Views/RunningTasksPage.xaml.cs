using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class RunningTasksPage : Page, IContentActionHandler
{
    public RunningTasksPage()
    {
        InitializeComponent();
    }

    public Task HandleContentActionAsync(string action, Button source)
    {
        TaskActionBar.IsOpen = true;
        switch (action)
        {
            case "task:details":
                TaskActionBar.Severity = InfoBarSeverity.Informational;
                TaskActionBar.Title = "工作代理 1 详情已展开";
                TaskActionBar.Message = "当前进度 62%；使用量不可用；范围仍在已登记边界内。";
                break;
            case "task:continue":
                TaskActionBar.Severity = InfoBarSeverity.Success;
                TaskActionBar.Title = "已继续等待当前任务";
                TaskActionBar.Message = "保持原任务线程，不创建重复工作代理。";
                break;
            case "task:refine":
                MainTaskStatusText.Text = "已纠偏";
                TaskActionBar.Severity = InfoBarSeverity.Success;
                TaskActionBar.Title = "定向纠偏已记录";
                TaskActionBar.Message = "后续消息将发送到同一个工作代理线程。";
                break;
            case "task:cancel":
                MainTaskProgress.IsIndeterminate = false;
                MainTaskProgress.Value = 100;
                MainTaskStatusText.Text = "已取消";
                source.IsEnabled = false;
                TaskActionBar.Severity = InfoBarSeverity.Warning;
                TaskActionBar.Title = "任务已取消";
                TaskActionBar.Message = "示例任务状态已经发生变化。";
                break;
            case "task:approval-details":
                TaskActionBar.Severity = InfoBarSeverity.Warning;
                TaskActionBar.Title = "审批请求详情";
                TaskActionBar.Message = "请求读取任务范围外文件；默认保持拒绝。";
                break;
            case "task:reject":
                source.IsEnabled = false;
                source.Content = "已拒绝";
                TaskActionBar.Severity = InfoBarSeverity.Success;
                TaskActionBar.Title = "越界请求已拒绝";
                TaskActionBar.Message = "已记录纠偏，未扩大工作代理权限。";
                break;
        }

        return Task.CompletedTask;
    }
}
