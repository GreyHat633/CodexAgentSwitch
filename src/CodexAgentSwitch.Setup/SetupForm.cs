namespace CodexAgentSwitch.Setup;

internal sealed class SetupForm : Form
{
    private readonly TextBox target = new() { Dock = DockStyle.Top, Text = SetupEngine.DefaultTarget(), AccessibleName = "安装目录" };
    private readonly Label status = new() { Dock = DockStyle.Fill, Padding = new Padding(12), Text = "选择安装目录，然后安装 Win10 x64 自包含版本。", AutoSize = false };
    private readonly Button install = new() { Text = "安装 / 升级", AutoSize = true };
    private readonly Button uninstall = new() { Text = "可恢复卸载", AutoSize = true };
    private readonly CheckBox deleteData = new() { Text = "卸载时删除本地 data（不可恢复）", AutoSize = true, Dock = DockStyle.Bottom };
    private readonly string payload;

    public SetupForm(string payload)
    {
        this.payload = payload;
        Text = "Codex Agent Switch Setup";
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(640, 300);
        StartPosition = FormStartPosition.CenterScreen;
        var pathPanel = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12) };
        pathPanel.Controls.Add(target);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(install);
        buttons.Controls.Add(uninstall);
        Controls.Add(status);
        Controls.Add(deleteData);
        Controls.Add(pathPanel);
        Controls.Add(buttons);
        install.Click += Install;
        uninstall.Click += Uninstall;
    }

    private async void Install(object? sender, EventArgs e)
    {
        if (MessageBox.Show($"安装到：\n{target.Text}\n\n现有安装会先备份。继续吗？", "确认安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => status.Text = message);
            var result = await Engine().InstallAsync(payload, target.Text, progress);
            status.Text = result.Message;
            MessageBox.Show(result.Message, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
            MessageBox.Show(exception.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Uninstall(object? sender, EventArgs e)
    {
        var warning = deleteData.Checked ? "\n\n已选择永久删除本地 data；此操作不可恢复。" : "\n\n本地 data 与凭据将保留。";
        if (MessageBox.Show($"把安装移到可恢复目录？\n{target.Text}{warning}", "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            status.Text = Engine().Uninstall(target.Text, deleteData.Checked).Message;
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
        }
    }

    private void SetBusy(bool busy)
    {
        install.Enabled = !busy;
        uninstall.Enabled = !busy;
        target.Enabled = !busy;
        deleteData.Enabled = !busy;
    }

    private static SetupEngine Engine()
    {
        var redirectedPrograms = Environment.GetEnvironmentVariable("CAS_START_MENU_ROOT");
        return new SetupEngine(new WindowsStartMenuShortcut(
            string.IsNullOrWhiteSpace(redirectedPrograms) ? null : Path.GetFullPath(redirectedPrograms)));
    }
}
