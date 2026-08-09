#if DEBUG
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.App.ViewModels;

internal sealed class MockAgentSwitchUiStateSource(string scenario) : IAgentSwitchUiStateSource
{
    public Task<AgentSwitchUiSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var normalized = scenario.Trim().ToLowerInvariant();
        var state = normalized switch
        {
            "working-native" or "working-external" or "reviewing" or "failed" => SchedulerState.Working,
            "paused" => SchedulerState.Paused,
            "faulted" => SchedulerState.Faulted,
            _ => SchedulerState.Ready,
        };
        var task = normalized switch
        {
            "working-native" => MockTask("CAS-UI-NATIVE-020", "Worker 执行中", UiStatusTone.Info, "原生 Worker", "GPT-5.6 Luna", "SOL + LUNA", now, true),
            "working-external" => MockTask("CAS-UI-EXTERNAL-020", "Worker 执行中", UiStatusTone.Info, "外部 Worker", "DeepSeek V4 Flash 0731", "SOL + DSV4F", now, true),
            "reviewing" => MockTask("CAS-UI-REVIEW-020", "主代理审查中", UiStatusTone.Info, "外部 Worker", "DeepSeek V4 Flash 0731", "SOL + DSV4F", now, true),
            "failed" => MockTask("CAS-UI-FAILED-020", "失败", UiStatusTone.Error, "外部 Worker", "DeepSeek V4 Flash 0731", "SOL + DSV4F", now, false, "Provider 返回服务不可用。"),
            _ => null,
        };
        var tasks = task is null ? Array.Empty<WorkerTaskUiStatus>() : new[] { task };
        var project = new ProjectUiStatus(
            "mock-testspace",
            "TestSpace",
            "E:\\AISPace\\TestSpace",
            true,
            task?.ProfileName ?? "SOL + LUNA",
            "GPT-5.6 Sol · High",
            task?.WorkerName ?? "GPT-5.6 Luna · Medium",
            "经济优先",
            task?.StateLabel ?? "已就绪",
            task?.Tone ?? UiStatusTone.Success,
            now.AddMinutes(-24),
            task?.IsActive == true ? 1 : 0,
            task?.StateLabel);
        var activeCount = tasks.Count(item => item.IsActive);
        var stateLabel = state switch
        {
            SchedulerState.Working => "Agent Switch 正在工作",
            SchedulerState.Paused => "Agent Switch 已暂停",
            SchedulerState.Faulted => "Agent Switch 运行异常",
            _ => "Agent Switch 已就绪",
        };
        var detail = state switch
        {
            SchedulerState.Working => $"{activeCount} 个活动任务 · 1 个项目",
            SchedulerState.Paused => "不会接受新的 Worker 委派。",
            SchedulerState.Faulted => "用于视觉验收的后台异常状态。",
            _ => "当前没有活动任务。",
        };
        var tone = state switch
        {
            SchedulerState.Working => UiStatusTone.Info,
            SchedulerState.Paused => UiStatusTone.Warning,
            SchedulerState.Faulted => UiStatusTone.Error,
            _ => UiStatusTone.Success,
        };

        return Task.FromResult(new AgentSwitchUiSnapshot(
            state,
            stateLabel,
            detail,
            tone,
            activeCount,
            activeCount > 0 ? 1 : 0,
            [project],
            tasks,
            new UsageUiSummary(
                "可取得",
                UiStatusTone.Success,
                860,
                245,
                1105,
                0.0024m,
                "CNY",
                3,
                task?.WorkerKind ?? "外部 Worker",
                normalized == "failed" ? "失败" : "成功",
                now.AddMinutes(-3),
                "Luna 独立 Token Usage：当前 Codex Native Worker 接口未提供。",
                "Provider 返回的实际 Token Usage。"),
            [
                new ProviderUiStatus("native-codex", "原生 Codex", ProviderKind.NativeCodex, true, true, true, "Sol / Terra / Luna", "已连接", UiStatusTone.Success, "Codex Desktop 认证", "无需 API 调用", null),
                new ProviderUiStatus("deepseek-default", "DeepSeek", ProviderKind.DeepSeek, true, true, normalized == "working-external" || normalized == "reviewing", "DeepSeek V4 Flash 0731", "已启用", UiStatusTone.Success, "已安全配置", "成功 · 19:31", now.AddMinutes(-3)),
            ],
            state == SchedulerState.Faulted ? "用于视觉验收的后台异常状态。" : null));
    }

    private static WorkerTaskUiStatus MockTask(
        string id,
        string state,
        UiStatusTone tone,
        string workerKind,
        string worker,
        string profile,
        DateTimeOffset now,
        bool active,
        string? failure = null) => new(
            id,
            "mock-testspace",
            "TestSpace",
            "整理项目配置并输出可验证结果",
            profile,
            workerKind,
            worker,
            workerKind == "外部 Worker" ? "deepseek-default" : "native-codex",
            state,
            tone,
            now.AddSeconds(-34),
            now.AddSeconds(-31),
            now.AddSeconds(-2),
            active ? null : now.AddSeconds(-2),
            failure,
            active);
}
#endif
