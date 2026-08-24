using System.IO;
using Microsoft.Web.WebView2.Core;

namespace FourthIBTracker.Services;

/// <summary>
/// Shares one WebView2 environment across every embedded browser and hidden
/// forum fetcher that uses the application's persistent browser profile.
/// </summary>
public static class WebViewEnvironmentService
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _environmentTask;

    public static async Task<CoreWebView2Environment> GetAsync()
    {
        Task<CoreWebView2Environment> task;
        lock (Gate)
            task = _environmentTask ??= CreateAsync();

        try
        {
            return await task;
        }
        catch
        {
            // A transient runtime/profile failure must remain retryable.
            lock (Gate)
                if (ReferenceEquals(_environmentTask, task))
                    _environmentTask = null;
            throw;
        }
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "4thIBTracker", "WebView2");
        return CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
    }
}
