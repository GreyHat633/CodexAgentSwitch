using System.Diagnostics;
using System.Text.Json;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class CodexAppServerClient(CodexCommand command) : IAsyncDisposable
{
    private readonly List<string> _stderrTail = [];
    private Process? _process;
    private JsonLineRpcSession? _session;

    public event Func<string, JsonElement, Task>? NotificationReceived;

    public event Func<string, JsonElement, JsonElement, Task>? ServerRequestReceived;

    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return _stderrTail.ToList();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var prefixArgument in command.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Codex App Server.");
        _ = Task.Run(() => ReadStderrAsync(_process, cancellationToken), CancellationToken.None);
        _session = new JsonLineRpcSession(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        _session.NotificationReceived += OnNotificationAsync;
        _session.ServerRequestReceived += OnServerRequestAsync;
        _session.Start();

        await _session.SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new { name = "codex-agent-switch", title = "Codex Agent Switch", version = "0.1.1" },
                capabilities = (object?)null,
            },
            cancellationToken);
        await _session.SendNotificationAsync("initialized", cancellationToken: cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        return await _session!.SendRequestAsync(method, parameters, cancellationToken);
    }

    public async Task RespondAsync(JsonElement requestId, object response, CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            throw new InvalidOperationException("App Server is not running.");
        }

        await _session.RespondAsync(requestId, response, cancellationToken);
    }

    private Task OnNotificationAsync(string method, JsonElement parameters) =>
        NotificationReceived?.Invoke(method, parameters) ?? Task.CompletedTask;

    private Task OnServerRequestAsync(string method, JsonElement requestId, JsonElement parameters) =>
        ServerRequestReceived?.Invoke(method, requestId, parameters) ?? Task.CompletedTask;

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            lock (_stderrTail)
            {
                _stderrTail.Add(line.Length > 1000 ? line[..1000] : line);
                if (_stderrTail.Count > 40)
                {
                    _stderrTail.RemoveAt(0);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }
}
