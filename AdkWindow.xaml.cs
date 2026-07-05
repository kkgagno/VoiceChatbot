using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace VoiceChatbot;

public partial class AdkWindow : Window
{
    private string _currentUrl;

    public AdkWindow(string adkUrl)
    {
        InitializeComponent();
        _currentUrl = NormalizeUrl(adkUrl);
        UrlBox.Text = _currentUrl;
        Loaded += AdkWindow_Loaded;
    }

    private async void AdkWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            await AdkWebView.EnsureCoreWebView2Async();
            AdkWebView.CoreWebView2.NavigationStarting += (_, _) => LoadingOverlay.Visibility = Visibility.Visible;
            AdkWebView.CoreWebView2.NavigationCompleted += (_, _) => LoadingOverlay.Visibility = Visibility.Collapsed;
            Navigate(_currentUrl);
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                $"Could not start the embedded ADK browser.\n\n{ex.Message}",
                "Google ADK",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public void Navigate(string adkUrl)
    {
        _currentUrl = NormalizeUrl(adkUrl);
        UrlBox.Text = _currentUrl;

        if (AdkWebView.CoreWebView2 is null)
            return;

        LoadingOverlay.Visibility = Visibility.Visible;
        AdkWebView.CoreWebView2.Navigate(_currentUrl);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (AdkWebView.CoreWebView2 is null)
            Navigate(UrlBox.Text);
        else
            AdkWebView.CoreWebView2.Reload();
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = NormalizeUrl(UrlBox.Text);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        Navigate(UrlBox.Text);
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return AppSettings.DefaultAdkUrl;

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }
}
