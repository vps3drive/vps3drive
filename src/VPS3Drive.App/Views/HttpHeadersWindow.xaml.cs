using System.Collections.ObjectModel;
using System.Windows;
using VPS3Drive.Core;

namespace VPS3Drive.App;

public partial class HttpHeadersWindow : Window
{
    public ObservableCollection<HttpHeaderRule> Rules { get; }

    public HttpHeadersWindow(IEnumerable<HttpHeaderRule> existing)
    {
        Rules = new ObservableCollection<HttpHeaderRule>(existing);
        InitializeComponent();
        HeadersGrid.ItemsSource = Rules;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Rules.Add(new HttpHeaderRule
        {
            BucketPattern = "*",
            FileMask = "*",
            HeaderName = "Cache-Control",
            HeaderValue = "max-age=3600"
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HeadersGrid.SelectedItem is HttpHeaderRule rule)
            Rules.Remove(rule);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
