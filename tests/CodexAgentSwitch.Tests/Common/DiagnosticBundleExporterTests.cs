using System.IO.Compression;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.Common;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public void Export_redacts_provider_secrets_and_includes_environment_summary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-diagnostics-{Guid.NewGuid():N}");
        var paths = new AppDataPaths(root);
        paths.EnsureCreated();
        File.WriteAllText(Path.Combine(paths.LogsDirectory, "startup-crash.txt"),
            "Authorization: Bearer secret-value api_key=sk-1234567890abcdef");
        try
        {
            var bundle = DiagnosticBundleExporter.Export(paths, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
            using var archive = ZipFile.OpenRead(bundle);
            Assert.NotNull(archive.GetEntry("environment.json"));
            using var reader = new StreamReader(archive.GetEntry("logs/startup-crash.txt")!.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("secret-value", text);
            Assert.DoesNotContain("sk-1234567890abcdef", text);
            Assert.Contains("[REDACTED]", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
