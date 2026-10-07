using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Views;

public partial class CoursesView : UserControl
{
    private readonly CoursesViewModel _vm;
    private DataGridColumnHeader? _hoveredHeader;
    private bool _webViewReady;
    private readonly SemaphoreSlim _webViewLock = new(1, 1);
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private HttpClient? _forumClient;

    public CoursesView(CoursesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.FetchProfileHtml = FetchProfileHtmlAsync;
        vm.DataLoaded += BuildColumns;
        Grid.MouseMove += CoursesGrid_MouseMove;
        Grid.MouseLeave += (_, _) => SetHoveredColumn(null);

        // Group rows by section so each section reads as its own block.
        var view = CollectionViewSource.GetDefaultView(vm.Records);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
        Grid.ItemsSource = view;

        Loaded += async (_, _) =>
        {
            if (_vm.Records.Count == 0 && !_vm.IsLoading)
                await _vm.LoadAsync();
        };
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;
        await _webViewLock.WaitAsync();
        try
        {
            if (_webViewReady) return;
            var environment = await WebViewEnvironmentService.GetAsync();
            await Fetcher.EnsureCoreWebView2Async(environment);
            _webViewReady = true;
        }
        finally
        {
            _webViewLock.Release();
        }
    }

    private async Task<string> FetchProfileHtmlAsync(
        string url, CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync();

        HttpClient? client = null;
        try
        {
            client = await GetForumClientAsync(url);
            var html = await client.GetStringAsync(url, cancellationToken);
            if (ForumCoursesService.LooksLoggedOut(html))
                throw new HttpRequestException(
                    "The profile request did not receive the browser login session.");
            return html;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            await ResetForumClientAsync(client);
            return await NavigateHtmlAsync(url, cancellationToken);
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
                    // Keep all cookies that are valid for a normal HTTP request.
                }
            }

            _forumClient = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
            }) { Timeout = TimeSpan.FromSeconds(20) };
            var userAgent = Fetcher.CoreWebView2.Settings.UserAgent;
            if (!string.IsNullOrWhiteSpace(userAgent))
                _forumClient.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", userAgent);
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
            if (!ReferenceEquals(_forumClient, failedClient)) return;
            _forumClient = null;
            failedClient.Dispose();
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<string> NavigateHtmlAsync(
        string url, CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync();
        await _navigationLock.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                completion.TrySetResult(args.IsSuccess);

            Fetcher.CoreWebView2.NavigationCompleted += Handler;
            try
            {
                Fetcher.CoreWebView2.Navigate(url);
                var timeout = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                var done = await Task.WhenAny(completion.Task, timeout);
                if (done != completion.Task || !await completion.Task)
                    throw new HttpRequestException(
                        $"Couldn't load {url} through the browser session.");

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

    private void CoursesGrid_MouseMove(object sender, MouseEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        SetHoveredColumn(cell?.Column);
    }

    private void SetHoveredColumn(DataGridColumn? column)
    {
        var header = column is null
            ? null
            : FindVisualChildren<DataGridColumnHeader>(Grid)
                .FirstOrDefault(candidate => candidate.Column == column);
        if (ReferenceEquals(header, _hoveredHeader)) return;

        if (_hoveredHeader is not null)
        {
            _hoveredHeader.ClearValue(Control.BackgroundProperty);
            _hoveredHeader.ClearValue(Control.BorderBrushProperty);
        }

        _hoveredHeader = header;
        if (_hoveredHeader is null) return;
        _hoveredHeader.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x5F, 0x3A));
        _hoveredHeader.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x9E, 0x5F));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is ContentElement content
                ? ContentOperations.GetParent(content) ??
                  (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static Style DarkHeader(bool wrapped)
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A))));
        s.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE3))));
        s.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 6, 6, 6)));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        s.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))));
        if (wrapped)
        {
            // Vertical course names, reading bottom-up. The DataGrid's
            // ColumnHeaderHeight (set in XAML) leaves room for the longest name.
            var template = new DataTemplate();
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding());
            tb.SetValue(FrameworkElement.LayoutTransformProperty, new RotateTransform(-90));
            template.VisualTree = tb;
            s.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, template));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Bottom));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        }
        else
        {
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Bottom));
        }
        return s;
    }

    /// <summary>Course columns are dynamic (they come from the sheet header), so build them in code.</summary>
    private void BuildColumns()
    {
        Grid.Columns.Clear();
        var flatHeader = DarkHeader(wrapped: false);
        var wrapHeader = DarkHeader(wrapped: true);

        Grid.Columns.Add(new DataGridTextColumn
        { Header = "Name", Binding = new Binding("Name"), Width = 260, MinWidth = 220, HeaderStyle = flatHeader });
        Grid.Columns.Add(new DataGridTextColumn
        { Header = "ACMT", Binding = new Binding("Acmt"), Width = 110, MinWidth = 90, HeaderStyle = flatHeader });
        Grid.Columns.Add(new DataGridTextColumn
        { Header = "Done", Binding = new Binding("CompletedCount"), Width = 90, MinWidth = 75, HeaderStyle = flatHeader });

        var chipBrush = new CourseChipBrushConverter();
        var chipSymbol = new CourseChipSymbolConverter();

        foreach (var course in _vm.CourseNames)
        {
            // A small centred status chip; blank cell = not done.
            var chipText = new FrameworkElementFactory(typeof(TextBlock));
            chipText.SetBinding(TextBlock.TextProperty,
                new Binding($"Courses[{course}]") { Converter = chipSymbol });
            chipText.SetValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x24)));
            chipText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            chipText.SetValue(TextBlock.FontSizeProperty, 13.5);
            chipText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chipText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetBinding(Border.BackgroundProperty,
                new Binding($"Courses[{course}]") { Converter = chipBrush });
            chip.SetValue(FrameworkElement.WidthProperty, 26.0);
            chip.SetValue(FrameworkElement.HeightProperty, 22.0);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            chip.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chip.AppendChild(chipText);

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = course,
                CellTemplate = new DataTemplate { VisualTree = chip },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 64,
                HeaderStyle = wrapHeader,
                CellStyle = cellStyle,
                CanUserSort = false,
            });
        }
    }
}

public class CourseChipBrushConverter : IValueConverter
{
    private static readonly Brush Complete = new SolidColorBrush(Color.FromRgb(0x8F, 0xBC, 0x72));
    private static readonly Brush Advanced = new SolidColorBrush(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush NotDone  = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x5C));

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "complete" => Complete,
            "advanced" => Advanced,
            "not done" or "" => NotDone,
            _ => Advanced, // any other status shows amber
        };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

public class CourseChipSymbolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "complete" => "✓",
            "advanced" => "★",
            "not done" or "" => "✗",
            _ => "•",
        };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
