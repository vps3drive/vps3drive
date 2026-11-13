using VPS3Drive.Core;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class StorageProfileTests
{
    [Fact]
    public void Validate_AcceptsS3CompatibleEndpoint()
    {
        var profile = new StorageProfile
        {
            Name = "dev",
            Bucket = "sample-bucket",
            Region = "us-east-1",
            ServiceUrl = "https://storage.example.test",
            ForcePathStyle = true
        };

        profile.Validate();
    }

    [Theory]
    [InlineData("file:///tmp/storage")]
    [InlineData("not a url")]
    public void Validate_RejectsInvalidServiceUrl(string serviceUrl)
    {
        var profile = new StorageProfile
        {
            Name = "dev",
            Bucket = "sample-bucket",
            Region = "us-east-1",
            ServiceUrl = serviceUrl
        };

        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_RejectsConflictingCredentialSources()
    {
        var profile = new StorageProfile
        {
            Name = "dev",
            Bucket = "sample-bucket",
            Region = "us-east-1",
            CredentialReference = "saved-key",
            CredentialProfile = "work"
        };

        Assert.Throws<ArgumentException>(profile.Validate);
    }
}
