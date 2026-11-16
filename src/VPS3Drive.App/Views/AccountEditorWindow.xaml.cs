using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VPS3Drive.App.Services;
using VPS3Drive.Core;

namespace VPS3Drive.App;

public partial class AccountEditorWindow : Window
{
    private readonly AppStateService _state;
    private readonly StorageAccount? _existing;

    public StorageAccount? ResultAccount { get; private set; }

    public AccountEditorWindow(AppStateService state, StorageAccount? existing = null)
    {
        _state = state;
        _existing = existing;
        InitializeComponent();

        if (existing is not null)
        {
            Title = "Edit Storage Account";
            AccountNameBox.Text = existing.Name;
            SelectAccountType(existing.AccountType);
            RegionBox.Text = existing.Region;
            RestEndpointBox.Text = existing.RestEndpoint ?? string.Empty;
            RegionEndpointBox.Text = existing.RegionSpecificEndpoint ?? string.Empty;
            UseSslBox.IsChecked = existing.UseSsl;
            PathStyleBox.IsChecked = existing.ForcePathStyle;
            SignatureBox.SelectedIndex = existing.SignatureVersion == SignatureVersion.V2 ? 1 : 0;
            ConfigFileBox.Text = existing.ConfigFilePath ?? string.Empty;
            ProfileBox.Text = existing.CredentialProfile ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(existing.CredentialReference))
                StatusText.Text = "Existing encrypted keys are stored with Windows DPAPI. Leave key fields blank to keep them.";
        }
        else
        {
            Title = "Add New Storage Account";
        }
    }

    private void SelectAccountType(AccountType type)
    {
        foreach (ComboBoxItem item in AccountTypeBox.Items)
        {
            if (item.Tag?.ToString() == type.ToString())
            {
                AccountTypeBox.SelectedItem = item;
                break;
            }
        }
    }

    private AccountType GetSelectedAccountType()
    {
        if (AccountTypeBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<AccountType>(item.Tag?.ToString(), out var parsed))
        {
            return parsed;
        }
        return AccountType.AmazonS3;
    }

    private void AccountTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeysGroup is null || ConfigFileGroup is null)
            return;

        var type = GetSelectedAccountType();
        KeysGroup.Visibility = type == AccountType.AwsConfigFile ? Visibility.Collapsed : Visibility.Visible;
        ConfigFileGroup.Visibility = type == AccountType.AwsConfigFile ? Visibility.Visible : Visibility.Collapsed;
        if (type == AccountType.S3Compatible && PathStyleBox is not null)
            PathStyleBox.IsChecked = true;
    }

    private void BrowseConfigFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select AWS Config or Credentials File",
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            ConfigFileBox.Text = dialog.FileName;
    }

    private StorageAccount BuildDraftAccount(string? credentialRef)
    {
        var type = GetSelectedAccountType();
        var sig = SignatureBox.SelectedIndex == 1 ? SignatureVersion.V2 : SignatureVersion.V4;

        return new StorageAccount
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = AccountNameBox.Text.Trim(),
            AccountType = type,
            Region = string.IsNullOrWhiteSpace(RegionBox.Text) ? "us-east-1" : RegionBox.Text.Trim(),
            RestEndpoint = string.IsNullOrWhiteSpace(RestEndpointBox.Text) ? null : RestEndpointBox.Text.Trim(),
            RegionSpecificEndpoint = string.IsNullOrWhiteSpace(RegionEndpointBox.Text) ? null : RegionEndpointBox.Text.Trim(),
            UseSsl = UseSslBox.IsChecked == true,
            ForcePathStyle = PathStyleBox.IsChecked == true,
            SignatureVersion = sig,
            ConfigFilePath = type == AccountType.AwsConfigFile && !string.IsNullOrWhiteSpace(ConfigFileBox.Text) ? ConfigFileBox.Text.Trim() : null,
            CredentialProfile = type == AccountType.AwsConfigFile && !string.IsNullOrWhiteSpace(ProfileBox.Text) ? ProfileBox.Text.Trim() : null,
            CredentialReference = type == AccountType.AwsConfigFile ? null : credentialRef
        };
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        StatusText.Text = "Testing connection...";
        try
        {
            var draft = BuildDraftAccount(_existing?.CredentialReference);
            draft.Validate();
            var (ok, msg) = await _state.TestAccountAsync(draft, AccessKeyBox.Text.Trim(), SecretKeyBox.Password);
            StatusText.Text = msg;
            if (!ok)
                MessageBox.Show(this, msg, "Connection Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var type = GetSelectedAccountType();
            var credRef = _existing?.CredentialReference;

            if (type != AccountType.AwsConfigFile)
            {
                var accessKey = AccessKeyBox.Text.Trim();
                var secretKey = SecretKeyBox.Password;
                var hasNewKeys = !string.IsNullOrWhiteSpace(accessKey) || !string.IsNullOrWhiteSpace(secretKey);

                if (hasNewKeys)
                {
                    if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                        throw new ArgumentException("Both Access Key ID and Secret Access Key are required.");

                    credRef ??= $"acct-{Guid.NewGuid():N}";
                    await _state.SaveAccountSecretAsync(credRef, accessKey, secretKey);
                }
                else if (string.IsNullOrWhiteSpace(credRef))
                {
                    throw new ArgumentException("Access Key ID and Secret Access Key are required.");
                }
            }

            var account = BuildDraftAccount(credRef);
            account.Validate();
            ResultAccount = account;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Invalid Account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
