using System.Text.Json;
using System.Windows.Controls;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Views;

public partial class DashboardView : UserControl
{
    private bool _webViewReady;

    public DashboardView(DashboardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.FetchHtml = FetchHtmlAsync;
        Loaded += async (_, _) =>
        {
            if (vm.Sections.Count == 0 && !vm.IsLoading)
                await vm.LoadAsync();
        };
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;
        // Same user-data folder as the browser tabs → same forum login.
        var env = await WebViewEnvironmentService.GetAsync();
        await Fetcher.EnsureCoreWebView2Async(env);
        _webViewReady = true;
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        await EnsureWebViewAsync();

        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            Fetcher.CoreWebView2.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }
        Fetcher.CoreWebView2.NavigationCompleted += Handler;
        Fetcher.CoreWebView2.Navigate(url);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(20000));
        if (done != tcs.Task || !tcs.Task.Result)
            throw new InvalidOperationException($"Couldn't load {url} (timeout or navigation error).");

        var json = await Fetcher.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }
}
