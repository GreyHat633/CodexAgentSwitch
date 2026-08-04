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

    public async Task<Profile?> EnsureDefaultAsync(CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            await repository.MarkInitializedAsync(cancellationToken);
            return existing;
        }

        var profiles = await repository.ListAsync(cancellationToken);
        var recoverable = profiles.FirstOrDefault(profile => !profile.RequiresRepair);
        if (recoverable is not null)
        {
            var normalized = recoverable with { IsDefault = true };
            await repository.UpsertAsync(normalized, cancellationToken);
            await repository.MarkInitializedAsync(cancellationToken);
            return normalized;
        }

        // A profile list that was previously initialized can legitimately be
        // empty because the user removed an obsolete preset. Do not resurrect
        // the old economic profile on every startup.
        if (await repository.HasBeenInitializedAsync(cancellationToken))
        {
            return null;
        }

        var profile = Profile.CreateDefault(clock.UtcNow);
        await repository.UpsertAsync(profile, cancellationToken);
        await repository.MarkInitializedAsync(cancellationToken);
        return profile;
    }

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        if (profile.RequiresRepair)
        {
            throw new ProfileValidationException([new("profile.repair.required", profile.RepairMessage ?? "该方案需要修复后才能保存。", "Profile")]);
        }

        var existingProfiles = await repository.ListAsync(cancellationToken);
        var validation = validator.ValidateUniqueName(profile, existingProfiles);
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
        await repository.MarkInitializedAsync(cancellationToken);
    }

    public async Task<Profile> CreateAsync(Profile template, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var created = template with
        {
            Id = Guid.NewGuid(),
            IsDefault = makeDefault,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
            LastUsedAt = null,
        };
        await SaveAsync(created, cancellationToken);
        return created;
    }

    public async Task<Profile> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Profile {id} does not exist.");
        var updated = profile with { IsDefault = true };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<Profile> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Profile {id} does not exist.");
        var now = clock.UtcNow;
        var updated = profile with { IsDefault = true, LastUsedAt = now, UpdatedAt = now };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<string> SuggestUniqueNameAsync(string baseName, CancellationToken cancellationToken = default)
    {
        var existingNames = (await repository.ListAsync(cancellationToken))
            .Select(profile => profile.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = string.IsNullOrWhiteSpace(baseName) ? "新建方案" : baseName.Trim();
        if (!existingNames.Contains(root))
        {
            return root;
        }

        var index = 2;
        var candidate = $"{root} - 副本";
        while (existingNames.Contains(candidate))
        {
            candidate = $"{root} - 副本 {index++}";
        }

        return candidate;
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
        // Profile contains only routing and budget data. Keep the export envelope
        // intentionally narrow so credentials cannot enter it as the model grows.
        var safeProfile = profile with
        {
            IsDefault = false,
            IsBuiltIn = false,
            LastUsedAt = null,
        };
        var envelope = new ProfileExportEnvelope(1, safeProfile);
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

        var imported = ProfileDataMigration.Migrate(envelope.Profile, clock.UtcNow).Profile with
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
