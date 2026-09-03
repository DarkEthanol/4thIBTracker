using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Views;

public partial class PlatoonAttendanceView : UserControl
{
    private readonly PlatoonAttendanceViewModel _vm;
    private bool _webViewReady;
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private HttpClient? _forumClient;

    public PlatoonAttendanceView(PlatoonAttendanceViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.FetchHtml = FetchHtmlAsync;
        vm.FetchHtmlBatch = FetchHtmlBatchAsync;
        vm.PropertyChanged += ViewModelPropertyChanged;
        BuildColumns();
        Loaded += async (_, _) =>
        {
            if (!vm.HasLoaded && !vm.IsLoading) await vm.RefreshAsync();
        };
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PlatoonAttendanceViewModel.SelectedMonth))
            BuildColumns();
    }

    private void BuildColumns()
    {
        AttendanceGrid.Columns.Clear();
        var headerStyle = DarkHeader();

        AttendanceGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Date",
            Binding = new Binding(nameof(WebsiteAttendanceEvent.DateLabel)),
            Width = 130,
            MinWidth = 115,
            HeaderStyle = headerStyle,
        });
        AttendanceGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Event",
            Binding = new Binding(nameof(WebsiteAttendanceEvent.Name)),
            Width = 230,
            MinWidth = 175,
            HeaderStyle = headerStyle,
        });

        if (_vm.SelectedMonth is null) return;
        for (var index = 0; index < _vm.SelectedMonth.Members.Count; index++)
        {
            var statusText = new FrameworkElementFactory(typeof(TextBlock));
            statusText.SetBinding(TextBlock.TextProperty,
                new Binding($"Marks[{index}].ShortLabel"));
            statusText.SetBinding(TextBlock.ForegroundProperty,
                new Binding($"Marks[{index}].Foreground"));
            statusText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            statusText.SetValue(TextBlock.FontSizeProperty, 13.0);
            statusText.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            statusText.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);

            var statusCell = new FrameworkElementFactory(typeof(Border));
            statusCell.SetBinding(Border.BackgroundProperty,
                new Binding($"Marks[{index}].Background"));
            statusCell.SetBinding(FrameworkElement.ToolTipProperty,
                new Binding($"Marks[{index}].Label"));
            statusCell.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            statusCell.SetValue(FrameworkElement.MarginProperty, new Thickness(5));
            statusCell.SetValue(FrameworkElement.MinWidthProperty, 46.0);
            statusCell.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Stretch);
            statusCell.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Stretch);
            statusCell.AppendChild(statusText);

            AttendanceGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _vm.SelectedMonth.Members[index],
                HeaderStyle = headerStyle,
                CellTemplate = new DataTemplate { VisualTree = statusCell },
                Width = 105,
                MinWidth = 86,
                CanUserSort = false,
            });
        }
    }

    private static Style DarkHeader()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A))));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE3))));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));

        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        template.VisualTree = text;
        style.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, template));
        return style;
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;
        var environment = await WebViewEnvironmentService.GetAsync();
        await Fetcher.EnsureCoreWebView2Async(environment);
        _webViewReady = true;
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        var pages = await FetchHtmlBatchAsync([url]);
        return pages[0];
    }

    private async Task<IReadOnlyList<string>> FetchHtmlBatchAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return [];
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
                // Keep all cookies that are valid for a normal HTTP request.
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
                    throw new InvalidOperationException($"Couldn't load {url} (timeout or navigation error).");

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
