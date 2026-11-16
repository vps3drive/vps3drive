using VPS3Drive.Core;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class VPS3DriveModelTests
{
    [Fact]
    public void StorageAccount_ComputesRegionSpecificEndpoint()
    {
        var account = new StorageAccount
        {
            Name = "AWS Prod",
            AccountType = AccountType.AmazonS3,
            Region = "ap-southeast-1",
            RegionSpecificEndpoint = "s3.{region-code}.amazonaws.com",
            UseSsl = true
        };

        account.Validate();
        Assert.Equal("https://s3.ap-southeast-1.amazonaws.com", account.EffectiveServiceUrl);
    }

    [Fact]
    public void MappedDrive_ParsesBucketAndPrefixAndBridgesToStorageProfile()
    {
        var account = new StorageAccount
        {
            Id = "acc-1",
            Name = "Wasabi",
            AccountType = AccountType.S3Compatible,
            Region = "us-east-1",
            RestEndpoint = "s3.wasabisys.com",
            ForcePathStyle = true,
            CredentialReference = "cred-1"
        };

        var drive = new MappedDrive
        {
            AccountId = account.Id,
            BucketPath = "media-bucket/archive/2026/",
            DriveLetter = 'z',
            MountType = DriveMountType.NetworkDrive,
            ReadOnly = true
        };

        drive.Validate();
        Assert.Equal("media-bucket", drive.BucketName);
        Assert.Equal("archive/2026/", drive.Prefix);
        Assert.Equal("Z:\\", drive.DisplayDriveLetter);

        var profile = drive.ToStorageProfile(account);
        Assert.Equal("media-bucket", profile.Bucket);
        Assert.Equal('Z', profile.DriveLetter);
        Assert.Equal("https://s3.wasabisys.com", profile.ServiceUrl);
    }

    [Fact]
    public async Task AccountAndDriveStores_RoundTripJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpdrive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var accountStore = new AccountStore(Path.Combine(dir, "accounts.json"));
            var driveStore = new MappedDriveStore(Path.Combine(dir, "drives.json"));
            var optionsStore = new OptionsStore(Path.Combine(dir, "options.json"));

            var account = new StorageAccount { Name = "MinIO", AccountType = AccountType.S3Compatible, RestEndpoint = "http://localhost:9000", UseSsl = false };
            await accountStore.SaveAllAsync([account]);
            var loadedAccounts = await accountStore.LoadAsync();
            Assert.Single(loadedAccounts);
            Assert.Equal("MinIO", loadedAccounts[0].Name);

            var drive = new MappedDrive { AccountId = account.Id, BucketPath = "test-bucket", DriveLetter = 'Y' };
            await driveStore.SaveAllAsync([drive]);
            var loadedDrives = await driveStore.LoadAsync();
            Assert.Single(loadedDrives);
            Assert.Equal('Y', loadedDrives[0].DriveLetter);

            var opts = new AppOptions { MaxConcurrentUploads = 8, VerifyUploadIntegrity = false };
            await optionsStore.SaveAsync(opts);
            var loadedOpts = await optionsStore.LoadAsync();
            Assert.Equal(8, loadedOpts.MaxConcurrentUploads);
            Assert.False(loadedOpts.VerifyUploadIntegrity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
