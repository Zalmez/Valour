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
            // External URLs must be opened in the system browser.
            // Without this, all link clicks are silently swallowed by the WebView.
            if (e.Url.Host != "0.0.0.0" && e.Url.Host != "0.0.0.1")
            {
                e.UrlLoadingStrategy = Microsoft.AspNetCore.Components.WebView.UrlLoadingStrategy.OpenExternally;
            }
        };
    }
}
