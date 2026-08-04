using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.CredentialBroker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4
            || !string.Equals(args[0], "--data-root", StringComparison.Ordinal)
            || !string.Equals(args[2], "--provider-id", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("Usage: CodexAgentSwitch.CredentialBroker --data-root <path> --provider-id <id>");
            return 64;
        }

        try
        {
            var dataRoot = Path.GetFullPath(args[1]);
            var databasePath = Path.Combine(dataRoot, "codex-agent-switch.db");
            if (!File.Exists(databasePath))
            {
                await Console.Error.WriteLineAsync("The provider configuration is unavailable.");
                return 3;
            }

            var provider = await new SqliteProviderRepository(new SqliteDatabase(databasePath)).GetAsync(args[3]);
            if (provider is null || !provider.IsEnabled || string.IsNullOrWhiteSpace(provider.CredentialReference))
            {
                await Console.Error.WriteLineAsync("The provider credential is unavailable.");
                return 3;
            }

            var secret = await new WindowsCredentialStore().ReadAsync(provider.CredentialReference);
            if (string.IsNullOrWhiteSpace(secret))
            {
                await Console.Error.WriteLineAsync("The requested credential is unavailable.");
                return 3;
            }

            await Console.Out.WriteAsync(secret.Trim());
            return 0;
        }
        catch
        {
            // Never echo the credential, its Windows target, or an exception
            // that could contain process/environment details to Codex stdout.
            await Console.Error.WriteLineAsync("The credential helper failed.");
            return 1;
        }
    }
}
