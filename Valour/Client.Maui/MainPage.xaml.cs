namespace Valour.Client.Maui;

public partial class MainPage : ContentPage
{
    public MainPage(string? startPath = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(startPath))
        {
            blazorWebView.StartPath = startPath;
        }

        blazorWebView.BlazorWebViewInitialized += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine("BlazorWebView initialized");
        };
        blazorWebView.UrlLoading += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"URL loading: {e.Url}");
        };
    }
}
