using System.Collections.Concurrent;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class DeepSeekControlledTaskEndToEndTests
{
    [Fact]
    [Trait("Category", "LiveExternal")]
    public async Task Project_conversation_uses_real_deepseek_then_resumes_and_recovers_after_restart()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_DEEPSEEK_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var installedDatabasePath = Environment.GetEnvironmentVariable("CAS_INSTALLED_DATABASE")
            ?? throw new InvalidOperationException("CAS_INSTALLED_DATABASE is required.");
        var e2eRoot = Environment.GetEnvironmentVariable("CAS_E2E_ROOT")
            ?? throw new InvalidOperationException("CAS_E2E_ROOT is required.");
        var root = Path.Combine(Path.GetFullPath(e2eRoot), $"deepseek-controlled-{Guid.NewGuid():N}");
        Assert.True(root.StartsWith("E:\\", StringComparison.OrdinalIgnoreCase), "Live E2E root must be on E drive.");
        Directory.CreateDirectory(root);
        CodexRuntimeManager? runtimeManager = null;
        try
        {
            var installedDatabase = new SqliteDatabase(Path.GetFullPath(installedDatabasePath));
            var installedProviders = new SqliteProviderRepository(installedDatabase);
            var provider = await installedProviders.GetAsync("deepseek-default")
                ?? throw new InvalidOperationException("已安装应用中不存在 deepseek-default Provider。");
            Assert.True(provider.IsEnabled, "DeepSeek Provider 未启用。");
            Assert.Equal(DeepSeekV4Catalog.FlashModelId, provider.ModelId);
            Assert.Equal("api.deepseek.com", provider.BaseUri?.Host);
            Assert.False(string.IsNullOrWhiteSpace(provider.CredentialReference));
            var credentials = new WindowsCredentialStore();
            Assert.True(await credentials.ExistsAsync(provider.CredentialReference!), "Windows Credential Manager 中没有当前 DeepSeek Key。");

            var clock = new SystemClock();
            var database = new SqliteDatabase(Path.Combine(root, "state.db"));
            await database.InitializeAsync();
            var profileRepository = new SqliteProfileRepository(database);
            var providerRepository = new SqliteProviderRepository(database);
            var taskRepository = new SqliteControlledTaskRepository(database);
            var projectRepository = new SqliteProjectRepository(database);
            var usageRepository = new SqliteUsageLedgerRepository(database);
            var now = clock.UtcNow;
            var profile = new Profile(
                Guid.NewGuid(),
                "LIVE Terra + DeepSeek",
                new AgentSelection("gpt-5.6-terra", "low"),
                new WorkerPolicy(true, WorkerSource.ExternalProvider, provider.Id, null, 1, RoutingMode.Performance, FallbackAction.StopDelegation),
                new BudgetLimits(null, null, null, null, null, "CNY"),
                true,
                now,
                now,
                now);
            await providerRepository.UpsertAsync(provider);
            await profileRepository.UpsertAsync(profile);
            var projectService = new ProjectService(projectRepository, clock);
            var project = await projectService.CreateAsync("LIVE DeepSeek E2E", root, profile.Id);

            var discovery = await new CodexCommandLocator().LocateAsync();
            Assert.True(discovery.IsAvailable, discovery.Status + Environment.NewLine + string.Join(Environment.NewLine, discovery.Attempts));
            runtimeManager = new CodexRuntimeManager(
                new CodexCommandLocator(),
                new CodexSchemaCache(Path.Combine(root, "protocol-cache")),
                clock);
            var controlledRuntime = new ControlledTaskRuntime(runtimeManager);
            var httpClient = new HttpClient();
            var externalClient = new OpenAiCompatibleClient(httpClient, credentials);
            var usageCollector = new WorkerUsageCollector(new CostCalculator());
            var service = new ControlledTaskService(
                taskRepository,
                profileRepository,
                controlledRuntime,
                new TaskProfileSnapshotFactory(providerRepository, clock),
                new DelegationDecisionService(clock),
                new WorkerOrchestrator(
                    new ExternalWorkerAdapterFactory(externalClient, clock),
                    controlledRuntime,
                    new ExternalProviderResolver()),
                usageRepository,
                usageCollector,
                clock,
                projectRepository);
            var states = new ConcurrentBag<ControlledTaskStatus>();
            service.TaskChanged += session =>
            {
                states.Add(session.Status);
                return Task.CompletedTask;
            };

            var conversation = await service.CreateConversationAsync(project.Id, root, "DeepSeek 真实执行链");
            await service.ContinueAsync(
                conversation.Id,
                "请先委派当前配置的 Worker 独立分析：列出项目化 AI 对话客户端必须具备的三个持久化不变量；然后由主代理审查并给出最终结论。",
                useWorker: true);
            var completed = await WaitForTerminalAsync(taskRepository, conversation.Id, TimeSpan.FromMinutes(15), 1);

            Assert.True(
                completed.Status == ControlledTaskStatus.Completed,
                $"首轮受控任务失败：{completed.ErrorMessage ?? "(无错误信息)"}");
            Assert.False(string.IsNullOrWhiteSpace(completed.MainThreadId));
            var firstTurn = completed.Turns[0];
            Assert.Equal(DelegationDecisionKind.InvokeWorker, firstTurn.Delegation?.Kind);
            Assert.False(string.IsNullOrWhiteSpace(firstTurn.Delegation?.DelegatedScope));
            Assert.False(string.IsNullOrWhiteSpace(firstTurn.Delegation?.Deliverable));
            Assert.NotEmpty(firstTurn.Delegation?.AcceptanceCriteria ?? []);
            Assert.NotNull(firstTurn.ProfileSnapshot);
            Assert.Equal(provider.Id, firstTurn.ProfileSnapshot!.Provider?.Id);
            Assert.Equal(DeepSeekV4Catalog.FlashModelId, firstTurn.ProfileSnapshot.Provider?.ModelId);
            var worker = Assert.Single(firstTurn.Workers);
            Assert.Equal("external:deepseek-default", worker.AdapterId);
            Assert.Equal("deepseek-default", worker.ProviderId);
            Assert.Equal("DeepSeek", worker.ProviderName);
            Assert.Equal(DeepSeekV4Catalog.FlashModelId, worker.ModelId);
            Assert.True(Uri.TryCreate(worker.RequestUri, UriKind.Absolute, out var workerRequestUri));
            Assert.Equal("api.deepseek.com", workerRequestUri!.Host);
            Assert.Equal(WorkerJobStatus.Completed, worker.Status);
            Assert.True(worker.Usage?.TotalTokens > 0, "DeepSeek 响应没有归档 Usage。");
            Assert.DoesNotContain(firstTurn.Workers, item => item.AdapterId == "native-codex");
            Assert.Contains(firstTurn.Messages, message => message.Actor == TaskMessageActor.Worker && !string.IsNullOrWhiteSpace(message.Content));
            Assert.Contains(firstTurn.Messages, message => message.Actor == TaskMessageActor.MainAgent && !string.IsNullOrWhiteSpace(message.Content));
            Assert.Contains(ControlledTaskStatus.WorkerRunning, states);

            var originalThread = completed.MainThreadId;
            await service.ContinueAsync(completed.Id, "继续上一轮：把这三个不变量压缩成一句中文验收标准。", useWorker: false);
            var resumed = await WaitForTerminalAsync(taskRepository, completed.Id, TimeSpan.FromMinutes(10), 2);
            Assert.True(
                resumed.Status == ControlledTaskStatus.Completed,
                $"继续对话失败：{resumed.ErrorMessage ?? "(无错误信息)"}");
            Assert.Equal(originalThread, resumed.MainThreadId);
            Assert.Equal(2, resumed.Turns.Count);
            Assert.Contains(resumed.Turns[1].Messages, message => message.Actor == TaskMessageActor.MainAgent && !string.IsNullOrWhiteSpace(message.Content));

            var usage = await usageRepository.ListUsageAsync(resumed.Id);
            Assert.True(usage.Count >= 4, "应归档委派判定、DeepSeek、主回复和继续对话 Usage。");
            Assert.Contains(usage, item => item.ProviderId == "deepseek-default" && item.ModelId == DeepSeekV4Catalog.FlashModelId);

            var restartedProject = await new SqliteProjectRepository(database).GetAsync(project.Id);
            var restartedConversation = await new SqliteControlledTaskRepository(database).GetAsync(resumed.Id);
            Assert.Equal(project.WorkingDirectory, restartedProject?.WorkingDirectory);
            Assert.Equal(project.Id, restartedConversation?.ProjectId);
            Assert.Equal(originalThread, restartedConversation?.MainThreadId);
            Assert.Equal(2, restartedConversation?.Turns.Count);
        }
        finally
        {
            if (runtimeManager is not null)
            {
                await runtimeManager.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<ControlledTaskSession> WaitForTerminalAsync(
        IControlledTaskRepository repository,
        string id,
        TimeSpan timeout,
        int minimumTurns)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var session = await repository.GetAsync(id, cancellation.Token)
                ?? throw new InvalidOperationException("Conversation disappeared during live E2E.");
            if (session.Turns.Count >= minimumTurns && session.Status is
                ControlledTaskStatus.Completed or ControlledTaskStatus.Failed or ControlledTaskStatus.Interrupted or ControlledTaskStatus.UnknownRecoverable)
            {
                return session;
            }

            await Task.Delay(250, cancellation.Token);
        }
    }
}
