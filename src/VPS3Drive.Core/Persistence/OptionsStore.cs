using System.Text.Json;

namespace VPS3Drive.Core;

public sealed class OptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public OptionsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPS3Drive",
            "options.json");
    }

    public async Task<AppOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return new AppOptions();

        await using var stream = File.OpenRead(_filePath);
        var loaded = await JsonSerializer.DeserializeAsync<AppOptions>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return loaded ?? new AppOptions();
    }

    public async Task SaveAsync(AppOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, options, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
