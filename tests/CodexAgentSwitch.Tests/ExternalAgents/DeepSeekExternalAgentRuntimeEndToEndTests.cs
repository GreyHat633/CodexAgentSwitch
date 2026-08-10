using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.ExternalAgents;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.ExternalAgents;

public sealed class DeepSeekExternalAgentRuntimeEndToEndTests
{
    [Fact]
    [Trait("Category", "LiveExternalTools")]
    public async Task Real_deepseek_completes_text_tool_file_repair_and_permission_ladder()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_DEEPSEEK_TOOL_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var installedDatabasePath = Environment.GetEnvironmentVariable("CAS_INSTALLED_DATABASE")
            ?? throw new InvalidOperationException("CAS_INSTALLED_DATABASE is required.");
        var configuredRoot = Environment.GetEnvironmentVariable("CAS_E2E_ROOT")
            ?? throw new InvalidOperationException("CAS_E2E_ROOT is required.");
        var e2eRoot = Path.GetFullPath(configuredRoot);
        Assert.StartsWith("E:", e2eRoot, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(e2eRoot);
        var projectPath = Path.Combine(e2eRoot, "deepseek-tools-" + Guid.NewGuid().ToString("N"));
        var outsidePath = Path.Combine(e2eRoot, "deepseek-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(outsidePath);

        try
        {
            var provider = await LoadProviderAsync(installedDatabasePath);
            var credentials = new WindowsCredentialStore();
            Assert.True(await credentials.ExistsAsync(provider.CredentialReference!), "Windows Credential Manager 中没有当前 DeepSeek Key。");
            using var httpClient = new HttpClient();
            var client = new OpenAiCompatibleClient(httpClient, credentials);
            var runtime = new OpenAiCompatibleExternalAgentRuntime(
                client,
                new LocalExternalToolHost(),
                new ExternalAgentRuntimeOptions(MaxProviderTurns: 10, MaxToolCalls: 16, MaxWallClock: TimeSpan.FromMinutes(5)));

            var text = await client.CompleteAsync(
                provider,
                provider.ModelId!,
                "Return exactly SOL_DS_022_TEXT_OK and nothing else.");
            Assert.Equal("SOL_DS_022_TEXT_OK", text.Content.Trim());
            Assert.True(text.Usage?.TotalTokens > 0);

            var readOnly = await runtime.ExecuteAsync(
                provider,
                provider.ModelId!,
                "You MUST call the shell tool with command Get-Location exactly once. After observing the result, return exactly SOL_DS_022_SHELL_OK.",
                Session("live-read-only", projectPath, ExternalToolPermissionMode.ReadOnly, []));
            Assert.Equal(ExternalAgentRuntimeState.Completed, readOnly.State);
            Assert.Equal("SOL_DS_022_SHELL_OK", readOnly.Content?.Trim());
            Assert.True(readOnly.ToolCalls >= 1);
            Assert.Equal(0, readOnly.DeniedToolCalls);

            var create = await runtime.ExecuteAsync(
                provider,
                provider.ModelId!,
                "You MUST call apply_patch to create sol-ds-022.txt with exactly one line SOL_DS_022_FILE_OK. After the tool succeeds, return exactly SOL_DS_022_FILE_FINAL_OK.",
                Session("live-file-create", projectPath, ExternalToolPermissionMode.WorkspaceFullAccess, [projectPath]));
            Assert.True(create.State == ExternalAgentRuntimeState.Completed, Describe(create));
            Assert.Equal("SOL_DS_022_FILE_FINAL_OK", create.Content?.Trim());
            Assert.Equal("SOL_DS_022_FILE_OK\n", await File.ReadAllTextAsync(Path.Combine(projectPath, "sol-ds-022.txt")));
            Assert.Contains("sol-ds-022.txt", create.ChangedFiles, StringComparer.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(Path.Combine(projectPath, "SelfRepair.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projectPath, "Program.cs"), "System.Console.WriteLine(\"BROKEN\")\n");
            var repair = await runtime.ExecuteAsync(
                provider,
                provider.ModelId!,
                "In the current project you MUST: (1) call shell with dotnet build --nologo and observe the compiler failure; (2) call apply_patch to add the missing semicolon in Program.cs; (3) call shell with dotnet build --nologo again and observe success; (4) return exactly SOL_DS_022_SELF_REPAIR_OK.",
                Session("live-self-repair", projectPath, ExternalToolPermissionMode.WorkspaceFullAccess, [projectPath]));
            Assert.True(repair.State == ExternalAgentRuntimeState.Completed, Describe(repair));
            Assert.Equal("SOL_DS_022_SELF_REPAIR_OK", repair.Content?.Trim());
            Assert.True(repair.ToolCalls >= 3);
            Assert.True(repair.FailedToolCalls >= 1);
            Assert.Equal("System.Console.WriteLine(\"BROKEN\");\n", await File.ReadAllTextAsync(Path.Combine(projectPath, "Program.cs")));

            var outsideMarker = Path.Combine(outsidePath, "outside-marker.txt");
            await File.WriteAllTextAsync(outsideMarker, "SOL_DS_022_OUTSIDE_OK");
            var denied = await runtime.ExecuteAsync(
                provider,
                provider.ModelId!,
                $"You MUST call shell with command Get-Content -LiteralPath \"{outsideMarker}\". When the tool denies access, return exactly SOL_DS_022_WORKSPACE_DENIED_OK.",
                Session("live-workspace-denial", projectPath, ExternalToolPermissionMode.WorkspaceFullAccess, [projectPath]));
            Assert.True(denied.State == ExternalAgentRuntimeState.Completed, Describe(denied));
            Assert.Equal("SOL_DS_022_WORKSPACE_DENIED_OK", denied.Content?.Trim());
            Assert.True(denied.DeniedToolCalls >= 1);

            var fullAccess = await runtime.ExecuteAsync(
                provider,
                provider.ModelId!,
                $"You MUST call shell with command Get-Content -LiteralPath \"{outsideMarker}\". After observing SOL_DS_022_OUTSIDE_OK, return exactly SOL_DS_022_FULL_ACCESS_OK.",
                Session("live-full-access", projectPath, ExternalToolPermissionMode.FullAccess, [projectPath]));
            Assert.True(fullAccess.State == ExternalAgentRuntimeState.Completed, Describe(fullAccess));
            Assert.Equal("SOL_DS_022_FULL_ACCESS_OK", fullAccess.Content?.Trim());
            Assert.True(fullAccess.ToolCalls >= 1);
            Assert.Equal(0, fullAccess.DeniedToolCalls);

            var evidencePath = Environment.GetEnvironmentVariable("CAS_DEEPSEEK_TOOL_EVIDENCE_PATH");
            if (!string.IsNullOrWhiteSpace(evidencePath))
            {
                var fullEvidencePath = Path.GetFullPath(evidencePath);
                Assert.StartsWith("E:", fullEvidencePath, StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
                await File.WriteAllTextAsync(fullEvidencePath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    Provider = provider.Name,
                    provider.ModelId,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Text = new { Passed = true, Usage = text.Usage },
                    ReadOnlyShell = Evidence(readOnly),
                    FileCreate = Evidence(create),
                    SelfRepair = Evidence(repair),
                    WorkspaceDenied = Evidence(denied),
                    FullAccess = Evidence(fullAccess),
                    TotalProviderTurns = readOnly.ProviderTurns + create.ProviderTurns + repair.ProviderTurns + denied.ProviderTurns + fullAccess.ProviderTurns + 1,
                    TotalToolCalls = readOnly.ToolCalls + create.ToolCalls + repair.ToolCalls + denied.ToolCalls + fullAccess.ToolCalls,
                    TotalTokens = new long?[] { text.Usage?.TotalTokens, readOnly.Usage?.TotalTokens, create.Usage?.TotalTokens, repair.Usage?.TotalTokens, denied.Usage?.TotalTokens, fullAccess.Usage?.TotalTokens }.Sum(value => value ?? 0),
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            if (Directory.Exists(projectPath)) Directory.Delete(projectPath, recursive: true);
            if (Directory.Exists(outsidePath)) Directory.Delete(outsidePath, recursive: true);
        }
    }

    private static async Task<ProviderConfiguration> LoadProviderAsync(string databasePath)
    {
        var repository = new SqliteProviderRepository(new SqliteDatabase(Path.GetFullPath(databasePath)));
        var provider = await repository.GetAsync("deepseek-default")
            ?? throw new InvalidOperationException("已安装应用中不存在 deepseek-default Provider。");
        Assert.True(provider.IsEnabled, "DeepSeek Provider 未启用。");
        Assert.Equal(ProviderKind.DeepSeek, provider.Kind);
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, provider.ModelId);
        Assert.Equal("api.deepseek.com", provider.BaseUri?.Host);
        Assert.False(string.IsNullOrWhiteSpace(provider.CredentialReference));
        return provider;
    }

    private static ExternalToolSession Session(
        string taskId,
        string projectPath,
        ExternalToolPermissionMode permission,
        IReadOnlyList<string> writeScope) => new(
            taskId,
            projectPath,
            projectPath,
            permission,
            [projectPath],
            writeScope,
            DateTimeOffset.UtcNow);

    private static string Describe(ExternalAgentRuntimeResult result) => System.Text.Json.JsonSerializer.Serialize(new
    {
        State = result.State.ToString(),
        result.Content,
        result.ProviderTurns,
        result.ToolCalls,
        result.FailedToolCalls,
        result.DeniedToolCalls,
        result.Risks,
        result.Activity,
    });

    private static object Evidence(ExternalAgentRuntimeResult result) => new
    {
        State = result.State.ToString(),
        result.ProviderTurns,
        result.ToolCalls,
        result.FailedToolCalls,
        result.DeniedToolCalls,
        DurationMilliseconds = result.Duration.TotalMilliseconds,
        result.Usage,
        result.ChangedFiles,
    };
}
