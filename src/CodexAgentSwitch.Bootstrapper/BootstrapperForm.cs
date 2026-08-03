namespace CodexAgentSwitch.Bootstrapper;

internal sealed class BootstrapperForm : Form
{
    private readonly BootstrapperService service;
    private readonly string appDirectory;
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(16), Font = new Font("Segoe UI", 11), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button install = new() { Text = "Install Runtime", AutoSize = true };
    private readonly Button launch = new() { Text = "Launch Codex Agent Switch", AutoSize = true };

    public BootstrapperForm(BootstrapperService service, string appDirectory)
    {
        this.service = service; this.appDirectory = appDirectory;
        Text = "Codex Agent Switch ? Runtime Check"; MinimumSize = new Size(560, 260); StartPosition = FormStartPosition.CenterScreen;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(launch); buttons.Controls.Add(install); Controls.Add(status); Controls.Add(buttons);
        install.Click += (_, _) => { var ok = service.InstallAfterConfirmation(path => MessageBox.Show($"Windows App Runtime 1.8 x64 is missing or mismatched. Start the bundled official installer?\n\n{path}", "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes, out var message); MessageBox.Show(message, "Runtime", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning); RefreshStatus(); };
        launch.Click += (_, _) => { if (!service.LaunchMainApp(appDirectory, out var message)) MessageBox.Show(message, "Launch", MessageBoxButtons.OK, MessageBoxIcon.Warning); else Close(); };
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var current = service.Inspect(); status.Text = $"{current.Message}\r\n\r\nDetected OS: {current.Os.Version} ({current.Os.Architecture})\r\nRuntime entries: {current.Installations.Count}";
        install.Enabled = current.SupportedOs && !current.RuntimePresent; launch.Enabled = current.SupportedOs && current.RuntimePresent;
    }
}
