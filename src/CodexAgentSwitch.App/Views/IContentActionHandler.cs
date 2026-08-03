using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public interface IContentActionHandler
{
    Task HandleContentActionAsync(string action, Button source);
}
