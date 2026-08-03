namespace CodexAgentSwitch.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
