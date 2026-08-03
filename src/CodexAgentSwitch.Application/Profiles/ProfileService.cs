using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.Profiles;

public sealed class ProfileService(
    IProfileRepository repository,
    ProfileValidator validator,
    IClock clock)
{
    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<Profile> EnsureDefaultAsync(CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var profile = Profile.CreateDefault(clock.UtcNow);
        await repository.UpsertAsync(profile, cancellationToken);
        return profile;
    }

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileValidationException(validation.Issues);
        }

        var normalized = profile with
        {
            Name = profile.Name.Trim(),
            UpdatedAt = clock.UtcNow,
        };
        await repository.UpsertAsync(normalized, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Profile {id} does not exist.");
        if (profile.IsDefault)
        {
            throw new InvalidOperationException("默认配置方案必须先选择替代方案后才能删除。");
        }

        await repository.DeleteAsync(id, cancellationToken);
    }

    public string Export(Profile profile)
    {
        var envelope = new ProfileExportEnvelope(1, profile);
        return JsonSerializer.Serialize(envelope, ExportOptions);
    }

    public Profile Import(string json)
    {
        var envelope = JsonSerializer.Deserialize<ProfileExportEnvelope>(json, ExportOptions)
            ?? throw new InvalidDataException("配置方案文件为空或格式无效。");
        if (envelope.Version != 1)
        {
            throw new InvalidDataException($"不支持配置方案版本 {envelope.Version}。");
        }

        var imported = envelope.Profile with
        {
            Id = Guid.NewGuid(),
            IsDefault = false,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            LastUsedAt = null,
        };
        var validation = validator.Validate(imported);
        if (!validation.IsValid)
        {
            throw new ProfileValidationException(validation.Issues);
        }

        return imported;
    }

    private sealed record ProfileExportEnvelope(int Version, Profile Profile);
}
