using System.Text;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ConversationMessageView : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(ControlledTaskMessage),
        typeof(ConversationMessageView),
        new PropertyMetadata(null, OnMessageChanged));

    public ConversationMessageView()
    {
        InitializeComponent();
    }

    public ControlledTaskMessage? Message
    {
        get => (ControlledTaskMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ConversationMessageView view)
        {
            view.Render(args.NewValue as ControlledTaskMessage);
        }
    }

    private void Render(ControlledTaskMessage? message)
    {
        if (message is null)
        {
            MessageContent.Content = null;
            return;
        }

        ActorText.Text = message.Actor switch
        {
            TaskMessageActor.User => "你",
            TaskMessageActor.MainAgent => "主代理",
            TaskMessageActor.Worker => "工作代理",
            _ => KindLabel(message.Kind),
        };
        TimeText.Text = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        MessageBorder.HorizontalAlignment = message.Actor == TaskMessageActor.User
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        MessageBorder.Background = ResourceBrush(message.Actor == TaskMessageActor.User
            ? "AccentFillColorSecondaryBrush"
            : "CardBackgroundFillColorDefaultBrush");

        var content = BuildMarkdown(message.Content, message.Kind == TaskMessageKind.Diff);
        if (message.IsCollapsible || message.Kind is TaskMessageKind.ToolCall or TaskMessageKind.FileChange or TaskMessageKind.Diff or TaskMessageKind.WorkerProgress or TaskMessageKind.Usage)
        {
            MessageContent.Content = new Expander
            {
                Header = string.IsNullOrWhiteSpace(message.Metadata)
                    ? KindLabel(message.Kind)
                    : $"{KindLabel(message.Kind)} · {message.Metadata}",
                IsExpanded = false,
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else
        {
            MessageContent.Content = content;
        }
    }

    private static FrameworkElement BuildMarkdown(string content, bool forceDiff)
    {
        var panel = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Stretch };
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0)
                    {
                        code.AppendLine();
                    }

                    code.Append(lines[index++]);
                }

                if (index < lines.Length)
                {
                    index++;
                }

                panel.Children.Add(CodeBlock(code.ToString(), language, forceDiff || language.Equals("diff", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            if (LooksLikeTable(lines, index))
            {
                var tableLines = new List<string>();
                while (index < lines.Length && lines[index].Contains('|', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    tableLines.Add(lines[index++]);
                }

                panel.Children.Add(Table(tableLines));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            var heading = line.TakeWhile(character => character == '#').Count();
            if (heading is > 0 and <= 6 && line.Length > heading && char.IsWhiteSpace(line[heading]))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line[(heading + 1)..],
                    FontSize = Math.Max(16, 27 - heading * 2),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                index++;
                continue;
            }

            var isList = line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)
                || IsNumberedList(line);
            panel.Children.Add(new TextBlock
            {
                Text = isList ? NormalizeListItem(line) : line,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 22,
            });
            index++;
        }

        return panel;
    }

    private static FrameworkElement CodeBlock(string code, string language, bool diff)
    {
        var grid = new Grid { MinWidth = 0 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Padding = new Thickness(10, 6, 6, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = diff ? "Diff" : string.IsNullOrWhiteSpace(language) ? "代码" : language,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        var copy = new Button { Content = "复制", Padding = new Thickness(10, 4, 10, 4) };
        copy.Click += (_, _) => Copy(code);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        grid.Children.Add(header);

        var text = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(10),
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush"),
            Background = ResourceBrush("SubtleFillColorSecondaryBrush"),
            Child = grid,
        };
    }

    private static FrameworkElement Table(IReadOnlyList<string> lines)
    {
        var rows = lines
            .Where((_, index) => index != 1 || !lines[index].Replace("|", string.Empty, StringComparison.Ordinal).Trim().All(character => character is '-' or ':' or ' '))
            .Select(SplitCells)
            .ToArray();
        var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Length);
        var grid = new Grid();
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < rows[row].Length; column++)
            {
                var cell = new Border
                {
                    Padding = new Thickness(9, 6, 9, 6),
                    BorderThickness = new Thickness(0.5),
                    BorderBrush = ResourceBrush("CardStrokeColorDefaultBrush"),
                    Child = new TextBlock
                    {
                        Text = rows[row][column],
                        FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private static bool LooksLikeTable(IReadOnlyList<string> lines, int index) =>
        index + 1 < lines.Count
        && lines[index].Contains('|', StringComparison.Ordinal)
        && lines[index + 1].Contains('-', StringComparison.Ordinal)
        && lines[index + 1].Contains('|', StringComparison.Ordinal);

    private static string[] SplitCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static bool IsNumberedList(string line)
    {
        var trimmed = line.TrimStart();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < 5 && trimmed[..dot].All(char.IsDigit) && dot + 1 < trimmed.Length && char.IsWhiteSpace(trimmed[dot + 1]);
    }

    private static string NormalizeListItem(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return "• " + trimmed[2..];
        }

        return trimmed;
    }

    private static string KindLabel(TaskMessageKind kind) => kind switch
    {
        TaskMessageKind.ToolCall => "工具调用",
        TaskMessageKind.FileChange => "修改文件",
        TaskMessageKind.Diff => "Diff",
        TaskMessageKind.WorkerProgress => "Worker 进度",
        TaskMessageKind.Usage => "Usage",
        _ => "系统",
    };

    private static Brush ResourceBrush(string key) =>
        (Brush)(Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value)
            ? value
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent));

    private static void Copy(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
    }
}
