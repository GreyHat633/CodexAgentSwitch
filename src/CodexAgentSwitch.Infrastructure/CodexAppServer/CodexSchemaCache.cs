using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record ProtocolSchemaSnapshot(string Version, string Directory, string Sha256, DateTimeOffset GeneratedAt);

public sealed class CodexSchemaCache(string cacheRoot)
{
    public async Task<ProtocolSchemaSnapshot> GenerateAsync(
        CodexCommand command,
        string version,
        CancellationToken cancellationToken = default)
    {
        var safeVersion = string.Concat(version.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var outputDirectory = Path.Combine(cacheRoot, safeVersion);
        Directory.CreateDirectory(outputDirectory);
        var marker = Path.Combine(outputDirectory, "ClientRequest.json");
        if (!File.Exists(marker))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var prefix in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefix);
            }

            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("generate-json-schema");
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add("--experimental");
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Codex schema generator.");
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Codex schema generation failed with exit {process.ExitCode}: {Limit(stderr)}");
            }
        }

        var files = Directory.GetFiles(outputDirectory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidDataException("Codex schema generator produced no JSON files.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(outputDirectory, file).Replace('\\', '/')));
            await using var stream = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return new ProtocolSchemaSnapshot(version, outputDirectory, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), DateTimeOffset.UtcNow);
    }

    private static string Limit(string value) => value.Length <= 500 ? value.Trim() : value[..500].Trim();
}
