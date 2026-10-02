using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Views;

public partial class DashboardView : UserControl
{
    private bool _webViewReady;
    private readonly SemaphoreSlim _webViewLock = new(1, 1);
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private HttpClient? _forumClient;

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
        await _webViewLock.WaitAsync();
        try
        {
            if (_webViewReady) return;
            // Same user-data folder as the browser tabs → same forum login.
            var env = await WebViewEnvironmentService.GetAsync();
            await Fetcher.EnsureCoreWebView2Async(env);
            _webViewReady = true;
        }
        finally
        {
            _webViewLock.Release();
        }
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        await EnsureWebViewAsync();

        HttpClient? client = null;
        try
        {
            client = await GetForumClientAsync(url);
            var html = await client.GetStringAsync(url);
            if (ForumCoursesService.LooksLoggedOut(html))
                throw new HttpRequestException(
                    "The direct request did not receive the browser login session.");
            return html;
        }
        catch
        {
            await ResetForumClientAsync(client);
            return await NavigateHtmlAsync(url);
        }
    }

    private async Task<HttpClient> GetForumClientAsync(string url)
    {
        if (_forumClient is not null) return _forumClient;

        await _clientLock.WaitAsync();
        try
        {
            if (_forumClient is not null) return _forumClient;

            var cookies = await Fetcher.CoreWebView2.CookieManager.GetCookiesAsync(url);
            var cookieContainer = new CookieContainer();
            foreach (var cookie in cookies)
            {
                try
                {
                    cookieContainer.Add(new Cookie(
                        cookie.Name,
                        cookie.Value,
                        string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                        cookie.Domain)
                    {
                        HttpOnly = cookie.IsHttpOnly,
                        Secure = cookie.IsSecure,
                    });
                }
                catch (CookieException)
                {
                    // Browser-only cookies are nonessential to the forum request.
                }
            }

            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            _forumClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var userAgent = Fetcher.CoreWebView2.Settings.UserAgent;
            if (!string.IsNullOrWhiteSpace(userAgent))
                _forumClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            return _forumClient;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task ResetForumClientAsync(HttpClient? failedClient)
    {
        if (failedClient is null) return;
        await _clientLock.WaitAsync();
        try
        {
            // Another request may already have replaced the failed client.
            if (!ReferenceEquals(_forumClient, failedClient)) return;
            _forumClient = null;
            failedClient.Dispose();
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<string> NavigateHtmlAsync(string url)
    {
        await EnsureWebViewAsync();
        await _navigationLock.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                tcs.TrySetResult(args.IsSuccess);

            Fetcher.CoreWebView2.NavigationCompleted += Handler;
            try
            {
                Fetcher.CoreWebView2.Navigate(url);

                var done = await Task.WhenAny(tcs.Task, Task.Delay(20000));
                if (done != tcs.Task || !await tcs.Task)
                    throw new InvalidOperationException(
                        $"Couldn't load {url} (timeout or navigation error).");

                var json = await Fetcher.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement.outerHTML");
                return JsonSerializer.Deserialize<string>(json) ?? "";
            }
            finally
            {
                Fetcher.CoreWebView2.NavigationCompleted -= Handler;
            }
        }
        finally
        {
            _navigationLock.Release();
        }
    }
}
