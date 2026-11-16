using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VPS3Drive.App.Services;
using VPS3Drive.Core;

namespace VPS3Drive.App;

public partial class AccountsWindow : Window
{
    private readonly AppStateService _state;
    private readonly AccountStore _store = new();

    public AccountsWindow(AppStateService state)
    {
        _state = state;
        InitializeComponent();
        RefreshGrid();
    }

    private void RefreshGrid(int selectIndex = -1)
    {
        AccountsGrid.ItemsSource = null;
        AccountsGrid.ItemsSource = _state.Accounts;
        if (selectIndex >= 0 && selectIndex < _state.Accounts.Count)
            AccountsGrid.SelectedIndex = selectIndex;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var idx = AccountsGrid.SelectedIndex;
        var hasSelection = idx >= 0;
        EditButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        ExportButton.IsEnabled = hasSelection;
        MoveUpButton.IsEnabled = idx > 0;
        MoveDownButton.IsEnabled = hasSelection && idx < _state.Accounts.Count - 1;
    }

    private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AccountEditorWindow(_state) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultAccount is not null)
        {
            _state.Accounts.Add(editor.ResultAccount);
            await _state.SaveAccountsAsync();
            _state.Log("Info", "Accounts", $"Added account '{editor.ResultAccount.Name}'.");
            RefreshGrid(_state.Accounts.Count - 1);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not StorageAccount selected)
            return;

        var idx = AccountsGrid.SelectedIndex;
        var editor = new AccountEditorWindow(_state, selected) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultAccount is not null)
        {
            _state.Accounts[idx] = editor.ResultAccount;
            await _state.SaveAccountsAsync();
            _state.Log("Info", "Accounts", $"Updated account '{editor.ResultAccount.Name}'.");
            RefreshGrid(idx);
        }
    }

    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not StorageAccount selected)
            return;

        if (MessageBox.Show(this, $"Delete storage account '{selected.Name}'?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _state.Accounts.Remove(selected);
        await _state.DeleteAccountSecretAsync(selected.CredentialReference);
        await _state.SaveAccountsAsync();
        _state.Log("Info", "Accounts", $"Deleted account '{selected.Name}'.");
        RefreshGrid();
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = AccountsGrid.SelectedIndex;
        if (idx <= 0)
            return;

        (_state.Accounts[idx - 1], _state.Accounts[idx]) = (_state.Accounts[idx], _state.Accounts[idx - 1]);
        await _state.SaveAccountsAsync();
        RefreshGrid(idx - 1);
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = AccountsGrid.SelectedIndex;
        if (idx < 0 || idx >= _state.Accounts.Count - 1)
            return;

        (_state.Accounts[idx + 1], _state.Accounts[idx]) = (_state.Accounts[idx], _state.Accounts[idx + 1]);
        await _state.SaveAccountsAsync();
        RefreshGrid(idx + 1);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Storage Accounts",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var imported = await _store.ImportAsync(dialog.FileName);
            _state.Accounts.AddRange(imported);
            await _state.SaveAccountsAsync();
            _state.Log("Info", "Accounts", $"Imported {imported.Count} account(s).");
            RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not StorageAccount selected)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Storage Account",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"{selected.Name}.json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await _store.ExportAsync(dialog.FileName, [selected]);
            _state.Log("Info", "Accounts", $"Exported account '{selected.Name}' (secrets omitted).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
