using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Views;

public partial class TrainingReportsView : UserControl
{
    private bool _webViewReady;
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private HttpClient? _forumClient;

    public TrainingReportsView(TrainingReportsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.FetchHtml = FetchHtmlAsync;
        vm.FetchHtmlBatch = FetchHtmlBatchAsync;
        Loaded += async (_, _) =>
        {
            if (!vm.HasScanned && !vm.IsLoading)
                await vm.ScanAsync();
        };
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;
        var env = await WebViewEnvironmentService.GetAsync();
        await Fetcher.EnsureCoreWebView2Async(env);
        _webViewReady = true;
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        var pages = await FetchHtmlBatchAsync([url]);
        return pages[0];
    }

    private async Task<IReadOnlyList<string>> FetchHtmlBatchAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return Array.Empty<string>();
        await EnsureWebViewAsync();

        try
        {
            var client = await GetForumClientAsync(urls[0]);
            var results = new string[urls.Count];
            using var gate = new SemaphoreSlim(6, 6);
            await Task.WhenAll(urls.Select(async (url, index) =>
            {
                await gate.WaitAsync();
                try { results[index] = await client.GetStringAsync(url); }
                finally { gate.Release(); }
            }));

            if (results.Any(ForumCoursesService.LooksLoggedOut))
                throw new HttpRequestException(
                    "The direct forum request did not receive the browser login session.");
            return results;
        }
        catch (Exception) when (urls.Count > 1)
        {
            ResetForumClient();
            var fallback = new List<string>(urls.Count);
            foreach (var url in urls) fallback.Add(await NavigateHtmlAsync(url));
            return fallback;
        }
        catch
        {
            ResetForumClient();
            return [await NavigateHtmlAsync(urls[0])];
        }
    }

    private async Task<HttpClient> GetForumClientAsync(string url)
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

    private void ResetForumClient()
    {
        _forumClient?.Dispose();
        _forumClient = null;
    }

    private async Task<string> NavigateHtmlAsync(string url)
    {
        await EnsureWebViewAsync();
        await _navigationLock.WaitAsync();
        try
        {
            var completion = new TaskCompletionSource<bool>();
            void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                completion.TrySetResult(args.IsSuccess);

            Fetcher.CoreWebView2.NavigationCompleted += Handler;
            try
            {
                Fetcher.CoreWebView2.Navigate(url);
                var done = await Task.WhenAny(completion.Task, Task.Delay(20000));
                if (done != completion.Task || !await completion.Task)
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
