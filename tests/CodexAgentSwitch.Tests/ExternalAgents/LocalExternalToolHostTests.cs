using System.Net;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.ExternalAgents;
using CodexAgentSwitch.Infrastructure.ExternalProviders;

namespace CodexAgentSwitch.Tests.ExternalAgents;

public sealed class LocalExternalToolHostTests
{
    [Fact]
    public async Task Provider_loop_observes_failed_build_patches_source_and_rebuilds_successfully()
    {
        var root = CreateTempDirectory();
        try
        {
            var createPatch = """
                --- /dev/null
                +++ b/SelfRepair.csproj
                @@ -0,0 +1,6 @@
                +<Project Sdk="Microsoft.NET.Sdk">
                +  <PropertyGroup>
                +    <OutputType>Exe</OutputType>
                +    <TargetFramework>net8.0</TargetFramework>
                +  </PropertyGroup>
                +</Project>
                --- /dev/null
                +++ b/Program.cs
                @@ -0,0 +1 @@
                +Console.WriteLine("BROKEN")
                """;
            var repairPatch = """
                --- a/Program.cs
                +++ b/Program.cs
                @@ -1 +1 @@
                -Console.WriteLine("BROKEN")
                +System.Console.WriteLine("SOL_DS_022_REPAIRED");
                """;
            var toolResults = new List<JsonElement>();
            var callCount = 0;
            var handler = new StubHandler(async (request, _) =>
            {
                callCount++;
                var payloadText = await request.Content!.ReadAsStringAsync();
                if (callCount > 1)
                {
                    using var payload = JsonDocument.Parse(payloadText);
                    var messages = payload.RootElement.GetProperty("messages");
                    var content = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString();
                    using var toolResult = JsonDocument.Parse(content!);
                    toolResults.Add(toolResult.RootElement.Clone());
                }

                return callCount switch
                {
                    1 => ToolCallResponse("call-create", "apply_patch", JsonSerializer.Serialize(new { patch = createPatch })),
                    2 => ToolCallResponse("call-build-fail", "shell", JsonSerializer.Serialize(new { command = "dotnet build --nologo", timeout = 120 })),
                    3 => ToolCallResponse("call-repair", "apply_patch", JsonSerializer.Serialize(new { patch = repairPatch })),
                    4 => ToolCallResponse("call-build-pass", "shell", JsonSerializer.Serialize(new { command = "dotnet build --nologo", timeout = 120 })),
                    _ => Json("""{"model":"tool-model","choices":[{"finish_reason":"stop","message":{"content":"SOL_DS_022_SELF_REPAIR_OK"}}]}"""),
                };
            });
            var runtime = new OpenAiCompatibleExternalAgentRuntime(
                new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
                new LocalExternalToolHost());
            var session = new ExternalToolSession(
                "test-self-repair-loop",
                root,
                root,
                ExternalToolPermissionMode.WorkspaceFullAccess,
                [root],
                [root],
                DateTimeOffset.UtcNow);

            var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Build the project and repair failures.", session);

            Assert.Equal(ExternalAgentRuntimeState.Completed, result.State);
            Assert.Equal("SOL_DS_022_SELF_REPAIR_OK", result.Content);
            Assert.Equal(5, result.ProviderTurns);
            Assert.Equal(4, result.ToolCalls);
            Assert.Equal(["Program.cs", "SelfRepair.csproj"], result.ChangedFiles);
            Assert.Equal(4, toolResults.Count);
            Assert.Equal(0, toolResults[0].GetProperty("exitCode").GetInt32());
            Assert.NotEqual(0, toolResults[1].GetProperty("exitCode").GetInt32());
            Assert.Equal(0, toolResults[2].GetProperty("exitCode").GetInt32());
            Assert.Equal(0, toolResults[3].GetProperty("exitCode").GetInt32());
            Assert.Equal(1, result.FailedToolCalls);
            Assert.Equal("System.Console.WriteLine(\"SOL_DS_022_REPAIRED\");\n", await File.ReadAllTextAsync(Path.Combine(root, "Program.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_tool_call_creates_file_with_apply_patch_and_returns_changed_files_for_final_turn()
    {
        var root = CreateTempDirectory();
        try
        {
            var patch = """
                --- /dev/null
                +++ b/sol-ds-022.txt
                @@ -0,0 +1 @@
                +SOL_DS_022_FILE_OK
                """;
            var callArguments = JsonSerializer.Serialize(new { patch });
            string? toolResultContent = null;
            var callCount = 0;
            var handler = new StubHandler(async (request, _) =>
            {
                callCount++;
                var payloadText = await request.Content!.ReadAsStringAsync();
                if (callCount == 2)
                {
                    using var payload = JsonDocument.Parse(payloadText);
                    toolResultContent = payload.RootElement.GetProperty("messages")[3].GetProperty("content").GetString();
                }

                return callCount == 1
                    ? Json(JsonSerializer.Serialize(new
                    {
                        model = "tool-model",
                        choices = new[]
                        {
                            new
                            {
                                finish_reason = "tool_calls",
                                message = new
                                {
                                    content = (string?)null,
                                    tool_calls = new[]
                                    {
                                        new { id = "call-patch", type = "function", function = new { name = "apply_patch", arguments = callArguments } },
                                    },
                                },
                            },
                        },
                    }))
                    : Json("""{"model":"tool-model","choices":[{"finish_reason":"stop","message":{"content":"SOL_DS_022_FILE_FINAL_OK"}}]}""");
            });
            var runtime = new OpenAiCompatibleExternalAgentRuntime(
                new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
                new LocalExternalToolHost());
            var session = new ExternalToolSession(
                "test-file-loop",
                root,
                root,
                ExternalToolPermissionMode.WorkspaceFullAccess,
                [root],
                [root],
                DateTimeOffset.UtcNow);

            var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Create the acceptance file.", session);

            Assert.Equal(ExternalAgentRuntimeState.Completed, result.State);
            Assert.Equal("SOL_DS_022_FILE_FINAL_OK", result.Content);
            Assert.Equal(["sol-ds-022.txt"], result.ChangedFiles);
            Assert.Equal("SOL_DS_022_FILE_OK\n", await File.ReadAllTextAsync(Path.Combine(root, "sol-ds-022.txt")));
            Assert.NotNull(toolResultContent);
            using var toolResult = JsonDocument.Parse(toolResultContent);
            Assert.Equal("sol-ds-022.txt", toolResult.RootElement.GetProperty("changedFiles")[0].GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_creates_file_with_project_relative_changed_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = await ApplyPatch(root, root, """
                --- /dev/null
                +++ b/new.txt
                @@ -0,0 +1,2 @@
                +one
                +two
                """);

            Assert.True(result.Succeeded);
            Assert.Equal(["new.txt"], result.ChangedFiles);
            Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_accepts_standard_git_diff_with_terminal_newline()
    {
        var root = CreateTempDirectory();
        try
        {
            var patch = """
                diff --git a/trailing.txt b/trailing.txt
                new file mode 100644
                --- /dev/null
                +++ b/trailing.txt
                @@ -0,0 +1 @@
                +TRAILING_NEWLINE_OK
                """ + "\n";

            var result = await ApplyPatch(root, root, patch);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal("TRAILING_NEWLINE_OK\n", await File.ReadAllTextAsync(Path.Combine(root, "trailing.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_accepts_codex_add_file_format()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = await ApplyPatch(root, root, """
                *** Begin Patch
                *** Add File: custom.txt
                +CUSTOM_OK
                *** End Patch
                """);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal(["custom.txt"], result.ChangedFiles);
            Assert.Equal("CUSTOM_OK\n", await File.ReadAllTextAsync(Path.Combine(root, "custom.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_accepts_codex_unnumbered_update_hunk()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "Program.cs");
            await File.WriteAllTextAsync(target, "System.Console.WriteLine(\"BROKEN\")\n");
            var result = await ApplyPatch(root, root, """
                *** Begin Patch
                *** Update File: Program.cs
                @@
                -System.Console.WriteLine("BROKEN")
                +System.Console.WriteLine("BROKEN");
                *** End Patch
                """);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal(["Program.cs"], result.ChangedFiles);
            Assert.Equal("System.Console.WriteLine(\"BROKEN\");\n", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_accepts_codex_delete_file_format()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "obsolete.txt");
            await File.WriteAllTextAsync(target, "obsolete\n");
            var result = await ApplyPatch(root, root, """
                *** Begin Patch
                *** Delete File: obsolete.txt
                *** End Patch
                """);

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal(["obsolete.txt"], result.ChangedFiles);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_denies_codex_paths_outside_project_without_writing()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = await ApplyPatch(root, root, """
                *** Begin Patch
                *** Add File: ../escape-custom.txt
                +nope
                *** End Patch
                """);

            Assert.True(result.Denied);
            Assert.Null(result.ExitCode);
            Assert.False(File.Exists(Path.Combine(root, "..", "escape-custom.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_modifies_existing_file_and_reports_normalized_path()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "old\n");
            var result = await ApplyPatch(root, root, """
                --- a/note.txt
                +++ b/note.txt
                @@ -1 +1 @@
                -old
                +new
                """);

            Assert.True(result.Succeeded);
            Assert.Equal(["note.txt"], result.ChangedFiles);
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(root, "note.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_denies_read_only_without_writing()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = await ApplyPatch(root, root, """
                --- /dev/null
                +++ b/denied.txt
                @@ -0,0 +1 @@
                +nope
                """, ExternalToolPermissionMode.ReadOnly);

            Assert.True(result.Denied);
            Assert.Null(result.ExitCode);
            Assert.False(File.Exists(Path.Combine(root, "denied.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_denies_paths_outside_allowed_scope_and_project()
    {
        var root = CreateTempDirectory();
        var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed")).FullName;
        try
        {
            var outsideScope = await ApplyPatch(root, allowed, """
                --- /dev/null
                +++ b/other.txt
                @@ -0,0 +1 @@
                +nope
                """);
            Assert.True(outsideScope.Denied);

            var traversal = await ApplyPatch(root, root, """
                --- /dev/null
                +++ b/../escape.txt
                @@ -0,0 +1 @@
                +nope
                """);
            Assert.True(traversal.Denied);
            Assert.False(File.Exists(Path.Combine(root, "other.txt")));
            Assert.False(File.Exists(Path.Combine(root, "..", "escape.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_rejects_malformed_patch_without_partial_changes()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep\n");
            var result = await ApplyPatch(root, root, """
                --- a/keep.txt
                +++ b/keep.txt
                @@ -1 +1 @@
                -different
                +changed
                """);

            Assert.False(result.Succeeded);
            Assert.Equal(1, result.ExitCode);
            Assert.False(result.Denied);
            Assert.Equal("keep\n", await File.ReadAllTextAsync(Path.Combine(root, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_rejects_absolute_header_without_writing()
    {
        var root = CreateTempDirectory();
        try
        {
            var result = await ApplyPatch(root, root, """
                --- /dev/null
                +++ C:\\absolute-target.txt
                @@ -0,0 +1 @@
                +nope
                """);

            Assert.True(result.Denied);
            Assert.Null(result.ExitCode);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_rejects_creation_over_existing_file()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "existing.txt");
            await File.WriteAllTextAsync(target, "original\n");
            var result = await ApplyPatch(root, root, """
                --- /dev/null
                +++ b/existing.txt
                @@ -0,0 +1 @@
                +replacement
                """);

            Assert.False(result.Succeeded);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("original\n", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_rejects_mismatched_old_and_new_paths()
    {
        var root = CreateTempDirectory();
        try
        {
            var oldPath = Path.Combine(root, "old.txt");
            await File.WriteAllTextAsync(oldPath, "old\n");
            var result = await ApplyPatch(root, root, """
                --- a/old.txt
                +++ b/new.txt
                @@ -1 +1 @@
                -old
                +new
                """);

            Assert.True(result.Denied);
            Assert.Equal("old\n", await File.ReadAllTextAsync(oldPath));
            Assert.False(File.Exists(Path.Combine(root, "new.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_tool_call_executes_real_read_only_shell_and_returns_result_for_final_turn()
    {
        var requestPayloads = new List<string>();
        string? toolResultContent = null;
        var handler = new StubHandler(async (request, _) =>
        {
            requestPayloads.Add(await request.Content!.ReadAsStringAsync());
            if (requestPayloads.Count == 2)
            {
                using var payload = JsonDocument.Parse(requestPayloads[1]);
                toolResultContent = payload.RootElement.GetProperty("messages")[3].GetProperty("content").GetString();
            }
            return requestPayloads.Count == 1
                ? Json("""{"model":"tool-model","choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"call-shell","type":"function","function":{"name":"shell","arguments":"{\"command\":\"Get-Location\"}"}}]}}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""")
                : Json("""{"model":"tool-model","choices":[{"finish_reason":"stop","message":{"content":"SOL_DS_022_SHELL_OK"}}],"usage":{"prompt_tokens":7,"completion_tokens":1,"total_tokens":8}}""");
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore());
        var runtime = new OpenAiCompatibleExternalAgentRuntime(client, new LocalExternalToolHost());
        var projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var session = new ExternalToolSession(
            "test-shell-loop",
            projectPath,
            projectPath,
            ExternalToolPermissionMode.ReadOnly,
            [projectPath],
            [],
            DateTimeOffset.UtcNow);

        var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Report the current location.", session);

        Assert.Equal(ExternalAgentRuntimeState.Completed, result.State);
        Assert.Equal("SOL_DS_022_SHELL_OK", result.Content);
        Assert.Equal(2, result.ProviderTurns);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(13, result.Usage?.TotalTokens);
        Assert.Contains("\"tool_call_id\":\"call-shell\"", requestPayloads[1]);
        Assert.NotNull(toolResultContent);
        using var toolResult = JsonDocument.Parse(toolResultContent);
        Assert.Equal(0, toolResult.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains(Path.GetFileName(projectPath), toolResult.RootElement.GetProperty("stdout").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_only_denies_mutating_shell_command_without_starting_it()
    {
        var projectPath = Path.GetFullPath(AppContext.BaseDirectory);
        var session = new ExternalToolSession(
            "test-read-only-denial",
            projectPath,
            projectPath,
            ExternalToolPermissionMode.ReadOnly,
            [projectPath],
            [],
            DateTimeOffset.UtcNow);
        var result = await new LocalExternalToolHost().ExecuteAsync(
            session,
            new ExternalToolExecutionRequest("call-denied", "shell", "{\"command\":\"Set-Content denied.txt nope\"}"));

        Assert.True(result.Denied);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(Path.Combine(projectPath, "denied.txt")));
    }

    [Fact]
    public async Task Workspace_full_access_denies_harmless_read_outside_project_scope()
    {
        var projectPath = CreateTempDirectory();
        var outsidePath = CreateTempDirectory();
        try
        {
            var markerPath = Path.Combine(outsidePath, "outside-marker.txt");
            await File.WriteAllTextAsync(markerPath, "OUTSIDE");
            var session = new ExternalToolSession(
                "test-workspace-outside-denial",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.WorkspaceFullAccess,
                [projectPath],
                [projectPath],
                DateTimeOffset.UtcNow);

            var result = await new LocalExternalToolHost().ExecuteAsync(
                session,
                new ExternalToolExecutionRequest(
                    "call-outside-denied",
                    "shell",
                    JsonSerializer.Serialize(new { command = $"Get-Content \"{markerPath}\"" })));

            Assert.True(result.Denied);
            Assert.Null(result.ExitCode);
            Assert.Contains("outside the project scope", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public async Task Workspace_full_access_allows_harmless_read_inside_project_scope()
    {
        var projectPath = CreateTempDirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(projectPath, "folder with spaces"));
            var markerPath = Path.Combine(folder.FullName, "inside-marker.txt");
            await File.WriteAllTextAsync(markerPath, "WORKSPACE_INSIDE_OK");
            var session = new ExternalToolSession(
                "test-workspace-inside-read",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.WorkspaceFullAccess,
                [projectPath],
                [projectPath],
                DateTimeOffset.UtcNow);

            var result = await new LocalExternalToolHost().ExecuteAsync(
                session,
                new ExternalToolExecutionRequest(
                    "call-inside-allowed",
                    "shell",
                    JsonSerializer.Serialize(new { command = $"Get-Content \"{markerPath}\"" })));

            Assert.False(result.Denied, result.StandardError);
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("WORKSPACE_INSIDE_OK", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task Full_access_allows_harmless_read_outside_project_scope()
    {
        var projectPath = CreateTempDirectory();
        var outsidePath = CreateTempDirectory();
        try
        {
            var markerPath = Path.Combine(outsidePath, "outside-marker.txt");
            await File.WriteAllTextAsync(markerPath, "FULL_ACCESS_OUTSIDE_OK");
            var session = new ExternalToolSession(
                "test-full-access-outside-read",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.FullAccess,
                [projectPath, outsidePath],
                [projectPath],
                DateTimeOffset.UtcNow);

            var result = await new LocalExternalToolHost().ExecuteAsync(
                session,
                new ExternalToolExecutionRequest(
                    "call-outside-allowed",
                    "shell",
                    JsonSerializer.Serialize(new { command = $"Get-Content \"{markerPath}\"" })));

            Assert.False(result.Denied);
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("FULL_ACCESS_OUTSIDE_OK", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public async Task Workspace_full_access_denies_nested_or_composed_shell_commands()
    {
        var projectPath = CreateTempDirectory();
        try
        {
            var session = new ExternalToolSession(
                "test-workspace-composition-denial",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.WorkspaceFullAccess,
                [projectPath],
                [projectPath],
                DateTimeOffset.UtcNow);
            var host = new LocalExternalToolHost();

            var composed = await host.ExecuteAsync(
                session,
                new ExternalToolExecutionRequest("call-composed", "shell", "{\"command\":\"Get-Location; Get-ChildItem\"}"));
            var nested = await host.ExecuteAsync(
                session,
                new ExternalToolExecutionRequest("call-nested", "shell", "{\"command\":\"powershell -Command Get-Location\"}"));

            Assert.True(composed.Denied);
            Assert.True(nested.Denied);
            Assert.Contains("one direct command", composed.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without Full Access", nested.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_timeout_kills_process_before_it_can_write_in_background()
    {
        var projectPath = CreateTempDirectory();
        try
        {
            var markerPath = Path.Combine(projectPath, "timeout-marker.txt");
            var session = new ExternalToolSession(
                "test-shell-timeout-kill",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.FullAccess,
                [projectPath],
                [projectPath],
                DateTimeOffset.UtcNow);

            var result = await new LocalExternalToolHost().ExecuteAsync(
                session,
                new ExternalToolExecutionRequest(
                    "call-timeout",
                    "shell",
                    JsonSerializer.Serialize(new
                    {
                        command = $"Start-Sleep -Seconds 2; Set-Content -LiteralPath \"{markerPath}\" -Value SHOULD_NOT_EXIST",
                        timeout = 1,
                    })));

            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            await Task.Delay(TimeSpan.FromMilliseconds(1_500));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_cancellation_kills_process_before_it_can_write_in_background()
    {
        var projectPath = CreateTempDirectory();
        try
        {
            var markerPath = Path.Combine(projectPath, "cancel-marker.txt");
            var session = new ExternalToolSession(
                "test-shell-cancel-kill",
                projectPath,
                projectPath,
                ExternalToolPermissionMode.FullAccess,
                [projectPath],
                [projectPath],
                DateTimeOffset.UtcNow);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalExternalToolHost().ExecuteAsync(
                session,
                new ExternalToolExecutionRequest(
                    "call-cancel",
                    "shell",
                    JsonSerializer.Serialize(new
                    {
                        command = $"Start-Sleep -Seconds 2; Set-Content -LiteralPath \"{markerPath}\" -Value SHOULD_NOT_EXIST",
                        timeout = 30,
                    })),
                cancellation.Token));

            await Task.Delay(TimeSpan.FromMilliseconds(2_200));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task Runtime_blocks_repeated_identical_tool_call_loop()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(
            """{"model":"tool-model","choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"call-shell","type":"function","function":{"name":"shell","arguments":"{\"command\":\"Get-Location\"}"}}]}}]}""")));
        var runtime = new OpenAiCompatibleExternalAgentRuntime(
            new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
            new LocalExternalToolHost(),
            new ExternalAgentRuntimeOptions(MaxRepeatedIdenticalToolCalls: 2));
        var projectPath = Path.GetFullPath(AppContext.BaseDirectory);

        var result = await runtime.ExecuteAsync(
            Provider(),
            "tool-model",
            "Keep trying.",
            Session(projectPath));

        Assert.Equal(ExternalAgentRuntimeState.Blocked, result.State);
        Assert.Equal(3, result.ProviderTurns);
        Assert.Equal(2, result.ToolCalls);
        Assert.Contains(result.Risks, risk => risk.Contains("repeated identical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runtime_silently_extends_soft_provider_lease_and_completes_after_turn_24()
    {
        var requestCount = 0;
        var handler = new StubHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(requestCount < 28
                ? ToolCallResponse($"call-{requestCount}", "shell", JsonSerializer.Serialize(new { command = $"step-{requestCount}" }))
                : CompletionResponse("completed-after-extension"));
        });
        var runtime = new OpenAiCompatibleExternalAgentRuntime(
            new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
            new ImmediateToolHost());

        var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Finish all steps.", Session(Path.GetFullPath(AppContext.BaseDirectory)));

        Assert.Equal(ExternalAgentRuntimeState.Completed, result.State);
        Assert.Equal(28, result.ProviderTurns);
        Assert.Equal(27, result.ToolCalls);
        Assert.Equal(1, result.LeaseExtensionCount);
        Assert.Null(result.HardLimitReason);
    }

    [Fact]
    public async Task Runtime_hard_provider_stop_allows_exactly_one_toolless_finalization()
    {
        var requestCount = 0;
        var finalizationRequests = 0;
        var handler = new StubHandler(async (request, _) =>
        {
            requestCount++;
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var isFinalization = !payload.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0;
            if (isFinalization)
            {
                finalizationRequests++;
                var messages = payload.RootElement.GetProperty("messages");
                var lastMessage = messages[ messages.GetArrayLength() - 1 ].GetProperty("content").GetString();
                Assert.Contains("Runtime hard stop", lastMessage, StringComparison.Ordinal);
                return CompletionResponse("completed work only");
            }

            return ToolCallResponse($"call-{requestCount}", "shell", JsonSerializer.Serialize(new { command = $"step-{requestCount}" }));
        });
        var runtime = new OpenAiCompatibleExternalAgentRuntime(
            new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
            new ImmediateToolHost(),
            new ExternalAgentRuntimeOptions
            {
                InitialProviderTurnSoftLimit = 2,
                ProviderTurnLeaseIncrement = 1,
                HardProviderTurnLimit = 3,
                InitialToolCallSoftLimit = 10,
                HardToolCallLimit = 10,
            });

        var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Keep working.", Session(Path.GetFullPath(AppContext.BaseDirectory)));

        Assert.Equal(ExternalAgentRuntimeState.Blocked, result.State);
        Assert.Equal("provider-turn-limit", result.HardLimitReason);
        Assert.Equal(3, result.ProviderTurns);
        Assert.Equal(3, result.ToolCalls);
        Assert.Equal(4, requestCount);
        Assert.Equal(1, finalizationRequests);
        Assert.True(result.FinalizationAttempted);
        Assert.True(result.FinalizationSucceeded);
        Assert.Equal("completed work only", result.Content);
        Assert.False(result.CostVerified);
    }

    [Fact]
    public async Task Runtime_enforces_hard_tool_cap_before_executing_oversized_batch()
    {
        var requestCount = 0;
        var handler = new StubHandler(async (request, _) =>
        {
            requestCount++;
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (!payload.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0)
            {
                return CompletionResponse("bounded final status");
            }

            return ToolCallsResponse(requestCount * 10, 2);
        });
        var runtime = new OpenAiCompatibleExternalAgentRuntime(
            new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
            new ImmediateToolHost(),
            new ExternalAgentRuntimeOptions
            {
                InitialProviderTurnSoftLimit = 10,
                HardProviderTurnLimit = 10,
                InitialToolCallSoftLimit = 2,
                ToolCallLeaseIncrement = 1,
                HardToolCallLimit = 3,
            });

        var result = await runtime.ExecuteAsync(Provider(), "tool-model", "Use tools.", Session(Path.GetFullPath(AppContext.BaseDirectory)));

        Assert.Equal(ExternalAgentRuntimeState.Blocked, result.State);
        Assert.Equal("tool-call-limit", result.HardLimitReason);
        Assert.Equal(2, result.ToolCalls);
        Assert.True(result.FinalizationSucceeded);
    }

    [Fact]
    public async Task Runtime_uses_each_tasks_configured_monetary_budget_and_marks_unavailable_usage_unverified()
    {
        var pricing = new ProviderPricing(1m, 0m, "CNY", null);
        var lowBudget = new BudgetLimits(0.5m, null, null, null, null, "CNY");
        var highBudget = lowBudget with { PerTask = 1m };

        async Task<(ExternalAgentRuntimeResult Result, int Requests)> Execute(BudgetLimits budget, bool includeUsage)
        {
            var requests = 0;
            var handler = new StubHandler((_, _) =>
            {
                requests++;
                if (requests == 1)
                {
                    return Task.FromResult(includeUsage
                        ? ToolCallResponseWithUsage("call-budget", 600_000, 0)
                        : ToolCallResponse("call-budget", "shell", "{\"command\":\"step\"}"));
                }
                return Task.FromResult(includeUsage
                    ? CompletionResponseWithUsage("within configured budget", 100_000, 0)
                    : CompletionResponse("within configured budget"));
            });
            var runtime = new OpenAiCompatibleExternalAgentRuntime(
                new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore()),
                new ImmediateToolHost());
            var result = await runtime.ExecuteAsync(
                Provider(pricing),
                "tool-model",
                "Work within budget.",
                Session(Path.GetFullPath(AppContext.BaseDirectory)),
                budgetSnapshot: budget);
            return (result, requests);
        }

        var low = await Execute(lowBudget, includeUsage: true);
        var high = await Execute(highBudget, includeUsage: true);
        var unknown = await Execute(lowBudget, includeUsage: false);

        Assert.Equal(ExternalAgentRuntimeState.Blocked, low.Result.State);
        Assert.Equal("monetary-budget", low.Result.HardLimitReason);
        Assert.Equal(1, low.Requests);
        Assert.True(low.Result.CostVerified);
        Assert.Equal(lowBudget, low.Result.BudgetSnapshot);
        Assert.Equal(ExternalAgentRuntimeState.Completed, high.Result.State);
        Assert.Equal(2, high.Requests);
        Assert.True(high.Result.CostVerified);
        Assert.Equal(ExternalAgentRuntimeState.Completed, unknown.Result.State);
        Assert.Equal(2, unknown.Requests);
        Assert.False(unknown.Result.CostVerified);
    }

    [Fact]
    public async Task Runtime_distinguishes_user_cancellation_from_wall_clock_timeout()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentialStore());
        var projectPath = Path.GetFullPath(AppContext.BaseDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelledRuntime = new OpenAiCompatibleExternalAgentRuntime(client, new LocalExternalToolHost());

        var cancelled = await cancelledRuntime.ExecuteAsync(
            Provider(),
            "tool-model",
            "Wait.",
            Session(projectPath),
            cancellation.Token);

        Assert.Equal(ExternalAgentRuntimeState.Cancelled, cancelled.State);

        var timeoutRuntime = new OpenAiCompatibleExternalAgentRuntime(
            client,
            new LocalExternalToolHost(),
            new ExternalAgentRuntimeOptions(MaxWallClock: TimeSpan.FromMilliseconds(50)));
        var timedOut = await timeoutRuntime.ExecuteAsync(
            Provider(),
            "tool-model",
            "Wait.",
            Session(projectPath));

        Assert.Equal(ExternalAgentRuntimeState.Timeout, timedOut.State);
    }

    private static ExternalToolSession Session(string projectPath) => new(
        "test-runtime",
        projectPath,
        projectPath,
        ExternalToolPermissionMode.ReadOnly,
        [projectPath],
        [],
        DateTimeOffset.UtcNow);

    private static ProviderConfiguration Provider(ProviderPricing? pricing = null) => new(
        "tool-provider",
        "Tool Provider",
        ProviderKind.OpenAiCompatible,
        new Uri("https://provider.test/v1"),
        "credential-ref",
        "tool-model",
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(5),
        true,
        pricing,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static async Task<ExternalToolExecutionResult> ApplyPatch(
        string projectPath,
        string allowedWriteScope,
        string patch,
        ExternalToolPermissionMode permissionMode = ExternalToolPermissionMode.WorkspaceFullAccess)
    {
        var session = new ExternalToolSession(
            "test-apply-patch",
            projectPath,
            projectPath,
            permissionMode,
            [projectPath],
            [allowedWriteScope],
            DateTimeOffset.UtcNow);
        return await new LocalExternalToolHost().ExecuteAsync(
            session,
            new ExternalToolExecutionRequest("call-apply-patch", "apply_patch", JsonSerializer.Serialize(new { patch })));
    }

    private static string CreateTempDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(configuredRoot), "CAS_TEST_ROOT must be set for filesystem tests.");
        var root = Path.GetFullPath(configuredRoot!);
        Assert.StartsWith("E:", root, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "apply-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ToolCallResponse(string id, string name, string arguments) => Json(JsonSerializer.Serialize(new
    {
        model = "tool-model",
        choices = new[]
        {
            new
            {
                finish_reason = "tool_calls",
                message = new
                {
                    content = (string?)null,
                    tool_calls = new[] { new { id, type = "function", function = new { name, arguments } } },
                },
            },
        },
    }));

    private static HttpResponseMessage ToolCallResponseWithUsage(string id, long inputTokens, long outputTokens) => Json(JsonSerializer.Serialize(new
    {
        model = "tool-model",
        usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens, total_tokens = inputTokens + outputTokens },
        choices = new[]
        {
            new
            {
                finish_reason = "tool_calls",
                message = new
                {
                    content = (string?)null,
                    tool_calls = new[] { new { id, type = "function", function = new { name = "shell", arguments = "{\"command\":\"step\"}" } } },
                },
            },
        },
    }));

    private static HttpResponseMessage ToolCallsResponse(int firstId, int count) => Json(JsonSerializer.Serialize(new
    {
        model = "tool-model",
        choices = new[]
        {
            new
            {
                finish_reason = "tool_calls",
                message = new
                {
                    content = (string?)null,
                    tool_calls = Enumerable.Range(firstId, count).Select(id => new
                    {
                        id = $"call-{id}",
                        type = "function",
                        function = new { name = "shell", arguments = JsonSerializer.Serialize(new { command = $"step-{id}" }) },
                    }).ToArray(),
                },
            },
        },
    }));

    private static HttpResponseMessage CompletionResponse(string content) => Json(JsonSerializer.Serialize(new
    {
        model = "tool-model",
        choices = new[] { new { finish_reason = "stop", message = new { content } } },
    }));

    private static HttpResponseMessage CompletionResponseWithUsage(string content, long inputTokens, long outputTokens) => Json(JsonSerializer.Serialize(new
    {
        model = "tool-model",
        usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens, total_tokens = inputTokens + outputTokens },
        choices = new[] { new { finish_reason = "stop", message = new { content } } },
    }));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }

    private sealed class ImmediateToolHost : IExternalToolHost
    {
        public Task<ExternalToolExecutionResult> ExecuteAsync(
            ExternalToolSession session,
            ExternalToolExecutionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ExternalToolExecutionResult(
                request.ToolCallId,
                request.ToolName,
                "ok",
                string.Empty,
                0,
                false,
                false,
                false));
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-secret");

        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
