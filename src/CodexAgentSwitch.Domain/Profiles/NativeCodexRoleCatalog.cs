namespace CodexAgentSwitch.Domain.Profiles;

/// <summary>
/// CAS product roles backed by the native Codex model catalog. Runtime
/// availability and reasoning efforts still come from App Server model/list.
/// </summary>
public sealed record NativeCodexRoleDescriptor(
    string SlotName,
    string ModelId,
    string WorkerId,
    string AgentRole,
    string ConfigFile);

public static class NativeCodexRoleCatalog
{
    public static IReadOnlyList<NativeCodexRoleDescriptor> All { get; } =
    [
        new("Astra", "gpt-6-astra", "native-astra", "cas_astra_worker", "agents/cas-astra-worker.toml"),
        new("Sol", "gpt-5.6-sol", "native-sol", "cas_sol_worker", "agents/cas-sol-worker.toml"),
        new("Terra", "gpt-5.6-terra", "native-terra", "cas_terra_worker", "agents/cas-terra-worker.toml"),
        new("Luna", "gpt-5.6-luna", "native-luna", "cas_luna_worker", "agents/cas-luna-worker.toml"),
    ];

    public static NativeCodexRoleDescriptor Astra => All[0];

    public static NativeCodexRoleDescriptor Sol => All[1];

    public static NativeCodexRoleDescriptor Terra => All[2];

    public static NativeCodexRoleDescriptor Luna => All[3];

    public static NativeCodexRoleDescriptor? FindBySlot(string? slot) =>
        Find(role => string.Equals(role.SlotName, slot?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static NativeCodexRoleDescriptor? FindByModel(string? modelId) =>
        Find(role => string.Equals(role.ModelId, modelId?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static NativeCodexRoleDescriptor? FindByWorker(string? workerId) =>
        Find(role => string.Equals(role.WorkerId, workerId?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static NativeCodexRoleDescriptor? FindByAgentRole(string? agentRole) =>
        Find(role => string.Equals(role.AgentRole, agentRole?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static NativeCodexRoleDescriptor? FindByModelOrSlot(string? value) =>
        FindByModel(value) ?? FindBySlot(value);

    public static NativeCodexRoleDescriptor? FindByWorkerModelOrSlot(string? value) =>
        FindByWorker(value) ?? FindByModel(value) ?? FindBySlot(value);

    private static NativeCodexRoleDescriptor? Find(Func<NativeCodexRoleDescriptor, bool> predicate) =>
        All.FirstOrDefault(predicate);
}
