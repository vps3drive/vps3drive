using System.Security.Cryptography;
using System.Text;

namespace VPS3Drive.Core;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly byte[] _entropy;

    public DpapiSecretStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPS3Drive", "secrets");
        _entropy = Encoding.UTF8.GetBytes("VPS3Drive.Secret.v1");
    }

    public async Task SaveAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);
        Directory.CreateDirectory(_directory);
        var path = GetPath(reference);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), _entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(reference);
        if (!File.Exists(path))
            return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var clearBytes = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(reference);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(string reference)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
        return Path.Combine(_directory, name + ".dpapi");
    }
}
