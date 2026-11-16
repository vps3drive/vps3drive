using System.Text.Json;

namespace VPS3Drive.Core;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public AccountStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPS3Drive",
            "accounts.json");
    }

    public async Task<IReadOnlyList<StorageAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var items = await JsonSerializer.DeserializeAsync<List<StorageAccount>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return items ?? [];
    }

    public async Task SaveAllAsync(IEnumerable<StorageAccount> accounts, CancellationToken cancellationToken = default)
    {
        var list = accounts.ToList();
        foreach (var account in list)
            account.Validate();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, list, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public async Task ExportAsync(string destinationPath, IEnumerable<StorageAccount> accounts, CancellationToken cancellationToken = default)
    {
        var list = accounts.Select(a => a with { CredentialReference = null }).ToList();
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, list, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StorageAccount>> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(sourcePath);
        var imported = await JsonSerializer.DeserializeAsync<List<StorageAccount>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (imported is null)
            return [];

        var result = new List<StorageAccount>(imported.Count);
        foreach (var item in imported)
        {
            var normalized = item with { Id = Guid.NewGuid().ToString("N") };
            normalized.Validate();
            result.Add(normalized);
        }
        return result;
    }
}
