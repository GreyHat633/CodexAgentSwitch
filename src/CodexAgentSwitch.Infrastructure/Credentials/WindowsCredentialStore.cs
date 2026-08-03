using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CodexAgentSwitch.Application.Credentials;

namespace CodexAgentSwitch.Infrastructure.Credentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string DefaultPrefix = "CodexAgentSwitch/";

    public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = CredRead(Target(referenceId), CredentialTypeGeneric, 0, out var pointer);
        if (exists)
        {
            CredFree(pointer);
            return Task.FromResult(true);
        }

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound
            ? Task.FromResult(false)
            : Task.FromException<bool>(new Win32Exception(error, "Unable to query Windows Credential Manager."));
    }

    public Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var secretBytes = checked(secret.Length * sizeof(char));
        if (secretBytes > 2560)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Credential exceeds the Windows generic credential limit.");
        }

        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(referenceId),
                CredentialBlobSize = (uint)secretBytes,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save credential in Windows Credential Manager.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(Target(referenceId), CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? Task.FromResult<string?>(null)
                : Task.FromException<string?>(new Win32Exception(error, "Unable to read Windows credential."));
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var value = credential.CredentialBlob == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char)));
            return Task.FromResult<string?>(value);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CredDelete(Target(referenceId), CredentialTypeGeneric, 0))
        {
            return Task.CompletedTask;
        }

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound
            ? Task.CompletedTask
            : Task.FromException(new Win32Exception(error, "Unable to delete Windows credential."));
    }

    private static string Target(string referenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        if (referenceId.Length > 180
            || referenceId.StartsWith('/')
            || referenceId.EndsWith('/')
            || referenceId.Contains("//", StringComparison.Ordinal)
            || referenceId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/')))
        {
            throw new ArgumentException("Credential reference contains unsupported characters.", nameof(referenceId));
        }

        var prefix = Environment.GetEnvironmentVariable("CAS_CREDENTIAL_PREFIX") ?? DefaultPrefix;
        if (prefix.Length is < 1 or > 60
            || !prefix.EndsWith('/')
            || prefix.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/')))
        {
            throw new InvalidOperationException("CAS_CREDENTIAL_PREFIX is invalid.");
        }

        return prefix + referenceId;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
