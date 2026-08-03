using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAgentSwitch.Infrastructure.Common;

public static partial class DiagnosticBundleExporter
{
    public static string Export(AppDataPaths paths, DateTimeOffset? now = null)
    {
        paths.EnsureCreated();
        var exportDirectory = Path.Combine(paths.Root, "exports");
        Directory.CreateDirectory(exportDirectory);
        var destination = Path.Combine(exportDirectory, $"diagnostics-{(now ?? DateTimeOffset.Now):yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var environment = JsonSerializer.Serialize(new
        {
            os = Environment.OSVersion.VersionString,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            appVersion = typeof(DiagnosticBundleExporter).Assembly.GetName().Version?.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteText(archive, "environment.json", environment);

        foreach (var file in Directory.EnumerateFiles(paths.LogsDirectory, "*", SearchOption.AllDirectories).Take(100))
        {
            var info = new FileInfo(file);
            if (info.Length > 5 * 1024 * 1024) continue;
            var relative = Path.GetRelativePath(paths.LogsDirectory, file).Replace('\\', '/');
            try { WriteText(archive, "logs/" + relative, Redact(File.ReadAllText(file))); }
            catch (DecoderFallbackException) { }
            catch (IOException) { }
        }

        return destination;
    }

    public static string Redact(string value)
    {
        var redacted = BearerRegex().Replace(value, "$1[REDACTED]");
        redacted = ApiKeyRegex().Replace(redacted, "$1[REDACTED]");
        return SecretTokenRegex().Replace(redacted, "[REDACTED]");
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s\""']+")]
    private static partial Regex BearerRegex();
    [GeneratedRegex(@"(?i)((?:api[_-]?key|token)\s*[\""']?\s*[:=]\s*[\""']?)[^\s,\""']+")]
    private static partial Regex ApiKeyRegex();
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex SecretTokenRegex();
}
