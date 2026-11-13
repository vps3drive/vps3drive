using System.Text.Json;

namespace VPS3Drive.Core;

public sealed class ProfileStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ProfileStore(string? path = null)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = path ?? Path.Combine(root, "VPS3Drive", "profiles.json");
    }

    public async Task<IReadOnlyList<StorageProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return Array.Empty<StorageProfile>();

        await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<List<StorageProfile>>(input, _options, cancellationToken).ConfigureAwait(false)
            ?? new List<StorageProfile>();
    }

    public async Task SaveAsync(IEnumerable<StorageProfile> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        foreach (var profile in profiles)
            profile.Validate();

        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Profile path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, profiles, _options, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
