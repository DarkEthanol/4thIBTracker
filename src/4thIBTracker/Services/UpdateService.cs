using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public sealed record UpdateRelease(
    Version Version,
    string Tag,
    Uri DownloadUrl,
    string Sha256,
    Uri? ReleasePage,
    string ReleaseNotes);

/// <summary>
/// Checks a public GitHub repository for stable releases and installs the
/// single-file Windows executable published by this project.
/// </summary>
public sealed class UpdateService
{
    public const string ExecutableAssetName = "4thIBTracker.exe";
    public const string ChecksumAssetName = "4thIBTracker.exe.sha256";

    private const long MaximumDownloadBytes = 350L * 1024 * 1024;
    private static readonly Regex RepositoryPattern = new(
        @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        @"\b[0-9a-fA-F]{64}\b", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _downloadDirectory;

    public string Repository { get; }
    public Version CurrentVersion { get; }
    public string CurrentVersionText => FormatVersion(CurrentVersion);
    public bool IsConfigured => RepositoryPattern.IsMatch(Repository);

    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "4thIBTracker", "updates");

    public UpdateService(
        string? repository = null,
        Version? currentVersion = null,
        HttpClient? httpClient = null,
        string? downloadDirectory = null)
    {
        Repository = (repository ?? ReadRepositoryMetadata()).Trim();
        CurrentVersion = NormaliseVersion(currentVersion ??
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
        _downloadDirectory = downloadDirectory ?? DefaultDownloadDirectory;
        _http = httpClient ?? CreateHttpClient(CurrentVersionText);
    }

    public async Task<UpdateRelease?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Automatic updates are not configured in this build. Install a release built by the GitHub publisher.");

        var parts = Repository.Split('/', 2);
        var apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/" +
                     $"{Uri.EscapeDataString(parts[1])}/releases/latest";

        using var response = await _http.GetAsync(apiUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "The update repository or its first published release could not be found.");
        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            jsonStream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");

        if (release.Draft || release.Prerelease)
            return null;
        if (!TryParseReleaseVersion(release.TagName, out var version))
            throw new InvalidDataException(
                $"The latest release tag '{release.TagName}' is not in vMAJOR.MINOR.PATCH format.");
        if (version <= CurrentVersion)
            return null;

        var executable = release.Assets.SingleOrDefault(asset =>
            asset.Name.Equals(ExecutableAssetName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.SingleOrDefault(asset =>
            asset.Name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
        if (executable?.BrowserDownloadUrl is null || checksum?.BrowserDownloadUrl is null)
            throw new InvalidDataException(
                $"Release {release.TagName} must contain {ExecutableAssetName} and {ChecksumAssetName}.");

        if (!Uri.TryCreate(executable.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(checksum.BrowserDownloadUrl, UriKind.Absolute, out var checksumUri) ||
            checksumUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The release assets do not have secure download URLs.");

        var checksumText = await _http.GetStringAsync(checksumUri, cancellationToken);
        var expectedHash = ParseChecksum(checksumText);

        if (!string.IsNullOrWhiteSpace(executable.Digest))
        {
            var githubDigest = executable.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? executable.Digest["sha256:".Length..]
                : executable.Digest;
            if (Sha256Pattern.IsMatch(githubDigest) &&
                !githubDigest.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The publisher checksum does not match GitHub's release-asset digest.");
        }

        return new UpdateRelease(
            version,
            release.TagName,
            downloadUri,
            expectedHash,
            Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page) ? page : null,
            release.Body ?? "");
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_downloadDirectory);
        var destination = Path.Combine(
            _downloadDirectory, $"4thIBTracker-{FormatVersion(release.Version)}.exe");
        var temporary = destination + ".download";

        if (File.Exists(destination) &&
            (await ComputeSha256Async(destination, cancellationToken))
                .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(1);
            return destination;
        }

        try
        {
            using var response = await _http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var declaredLength = response.Content.Headers.ContentLength;
            if (!declaredLength.HasValue || declaredLength is <= 0 or > MaximumDownloadBytes)
                throw new InvalidDataException("The release executable has an invalid download size.");

            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumDownloadBytes)
                        throw new InvalidDataException("The release executable exceeded the maximum download size.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((double)total / declaredLength.Value);
                }
                await target.FlushAsync(cancellationToken);
            }

            if (total != declaredLength.Value)
                throw new InvalidDataException("The release download ended before the declared size was received.");

            var actualHash = await ComputeSha256Async(temporary, cancellationToken);
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded executable failed its SHA-256 verification.");

            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Starts the downloaded executable as a helper. The caller must then close
    /// this process so the helper can replace it.
    /// </summary>
    public void LaunchInstaller(UpdateRelease release, string downloadedExecutable)
    {
        var target = Environment.ProcessPath
            ?? throw new InvalidOperationException("The running executable path could not be determined.");
        if (!File.Exists(target))
            throw new FileNotFoundException("The running executable could not be found.", target);

        var actualHash = ComputeSha256(downloadedExecutable);
        if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged update no longer matches its verified checksum.");

        EnsureTargetDirectoryIsWritable(target);

        var start = new ProcessStartInfo(downloadedExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(downloadedExecutable)!,
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(release.Sha256);

        if (Process.Start(start) is null)
            throw new InvalidOperationException("Windows could not start the update helper.");
    }

    public static bool IsApplyUpdateCommand(IReadOnlyList<string> args) =>
        args.Count == 4 && args[0].Equals("--apply-update", StringComparison.Ordinal);

    /// <summary>Runs inside the downloaded new executable, before WPF creates a window.</summary>
    public static void ApplyUpdateAndRestart(IReadOnlyList<string> args)
    {
        if (!IsApplyUpdateCommand(args) ||
            !int.TryParse(args[1], out var oldProcessId) || oldProcessId <= 0)
            throw new ArgumentException("The update helper arguments are invalid.");

        var target = Path.GetFullPath(args[2]);
        var expectedHash = ParseChecksum(args[3]);
        var helper = Environment.ProcessPath
            ?? throw new InvalidOperationException("The update helper path could not be determined.");

        if (!ComputeSha256(helper).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update helper failed its SHA-256 verification.");

        WaitForProcessToExit(oldProcessId, TimeSpan.FromMinutes(2));

        var backup = target + ".previous";

        try
        {
            ReplaceExecutable(helper, target, expectedHash);

            var restart = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!,
            };
            restart.ArgumentList.Add("--update-cleanup");
            restart.ArgumentList.Add(Environment.ProcessId.ToString());
            restart.ArgumentList.Add(helper);
            restart.ArgumentList.Add(backup);

            if (Process.Start(restart) is null)
                throw new InvalidOperationException("Windows could not restart the updated application.");
        }
        catch
        {
            if (File.Exists(backup))
                File.Copy(backup, target, overwrite: true);
            throw;
        }
    }

    internal static void ReplaceExecutable(string source, string target, string expectedHash)
    {
        var staged = target + ".update";
        var backup = target + ".previous";
        TryDelete(staged);
        TryDelete(backup);
        File.Copy(source, staged, overwrite: true);
        if (!ComputeSha256(staged).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged);
            throw new InvalidDataException("The staged replacement failed its SHA-256 verification.");
        }

        if (!File.Exists(target))
        {
            File.Move(staged, target);
            return;
        }

        try
        {
            File.Replace(staged, target, backup, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            ReplaceWithMoveFallback(staged, target, backup);
        }
        catch (IOException)
        {
            ReplaceWithMoveFallback(staged, target, backup);
        }
    }

    private static void ReplaceWithMoveFallback(string staged, string target, string backup)
    {
        File.Move(target, backup, overwrite: true);
        try
        {
            File.Move(staged, target, overwrite: true);
        }
        catch
        {
            if (File.Exists(backup)) File.Move(backup, target, overwrite: true);
            throw;
        }
    }

    public static void TryRestartOriginal(IReadOnlyList<string> args)
    {
        if (!IsApplyUpdateCommand(args)) return;
        try
        {
            var target = Path.GetFullPath(args[2]);
            if (!File.Exists(target)) return;
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!,
            });
        }
        catch { }
    }

    /// <summary>
    /// Removes the downloaded helper and rollback copy after the helper exits.
    /// Strict path validation prevents command-line arguments deleting arbitrary files.
    /// </summary>
    public static void SchedulePostUpdateCleanup(IReadOnlyList<string> args)
    {
        if (args.Count != 4 ||
            !args[0].Equals("--update-cleanup", StringComparison.Ordinal) ||
            !int.TryParse(args[1], out var helperProcessId) || helperProcessId <= 0)
            return;

        var helper = Path.GetFullPath(args[2]);
        var backup = Path.GetFullPath(args[3]);
        var current = Environment.ProcessPath;
        if (current is null ||
            !IsPathWithin(helper, DefaultDownloadDirectory) ||
            !backup.Equals(current + ".previous", StringComparison.OrdinalIgnoreCase))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                WaitForProcessToExit(helperProcessId, TimeSpan.FromMinutes(1));
                TryDelete(helper);
                TryDelete(backup);
            }
            catch
            {
                // Stale update files are harmless and can be reused/cleaned next time.
            }
        });
    }

    public static bool TryParseReleaseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed))
            return false;
        version = NormaliseVersion(parsed);
        return true;
    }

    public static string ParseChecksum(string text)
    {
        var match = Sha256Pattern.Match(text);
        if (!match.Success)
            throw new InvalidDataException("The release checksum is not a valid SHA-256 value.");
        return match.Value.ToLowerInvariant();
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(
        string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient(string version)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("4thIBTracker", version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string ReadRepositoryMetadata() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                attribute.Key.Equals("UpdateRepository", StringComparison.OrdinalIgnoreCase))?
            .Value ?? "";

    private static Version NormaliseVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static void EnsureTargetDirectoryIsWritable(string target)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The application directory could not be determined.");
        var probe = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.write-test");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                "The application folder is not writable. Move the app to a folder you own before updating.", ex);
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void WaitForProcessToExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException("The previous application process did not close in time.");
        }
        catch (ArgumentException)
        {
            // It exited before Process.GetProcessById was called.
        }
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
