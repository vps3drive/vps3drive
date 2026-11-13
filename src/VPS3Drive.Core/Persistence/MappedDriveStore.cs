using System.Text.Json;

namespace VPS3Drive.Core;

public sealed class MappedDriveStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public MappedDriveStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPS3Drive",
            "drives.json");
    }

    public async Task<IReadOnlyList<MappedDrive>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var items = await JsonSerializer.DeserializeAsync<List<MappedDrive>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return items ?? [];
    }

    public async Task SaveAllAsync(IEnumerable<MappedDrive> drives, CancellationToken cancellationToken = default)
    {
        var list = drives.ToList();
        foreach (var drive in list)
            drive.Validate();

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
}
