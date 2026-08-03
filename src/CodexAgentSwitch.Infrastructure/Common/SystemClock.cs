using CodexAgentSwitch.Application.Abstractions;

namespace CodexAgentSwitch.Infrastructure.Common;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
