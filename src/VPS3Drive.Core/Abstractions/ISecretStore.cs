namespace VPS3Drive.Core;

public interface ISecretStore
{
    Task SaveAsync(string reference, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
