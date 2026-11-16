namespace VPS3Drive.Core;

public sealed record StorageProfile
{
    public required string Name { get; init; }
    public required string Bucket { get; init; }
    public required string Region { get; init; }
    public string? ServiceUrl { get; init; }
    public string? CredentialProfile { get; init; }
    public string? CredentialReference { get; init; }
    public bool ForcePathStyle { get; init; }
    public char DriveLetter { get; init; } = 'V';

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Profile name is required.", nameof(Name));
        if (string.IsNullOrWhiteSpace(Bucket))
            throw new ArgumentException("Bucket name is required.", nameof(Bucket));
        if (string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("AWS region is required.", nameof(Region));
        if (!char.IsAsciiLetter(DriveLetter))
            throw new ArgumentException("Drive letter must be an ASCII letter.", nameof(DriveLetter));
        if (ServiceUrl is not null &&
            (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
            throw new ArgumentException("Service URL must be an absolute HTTP or HTTPS URL.", nameof(ServiceUrl));
        if (!string.IsNullOrWhiteSpace(CredentialReference) && !string.IsNullOrWhiteSpace(CredentialProfile))
            throw new ArgumentException("Choose either a Windows credential reference or an AWS profile.");
    }
}
