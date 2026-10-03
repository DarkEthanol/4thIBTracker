using System.IO;
using System.Reflection;
using Google.Apis.Auth.OAuth2;

namespace FourthIBTracker.Services;

/// <summary>
/// Reads the shared desktop OAuth identity embedded by the release publisher.
/// Desktop OAuth clients cannot keep this metadata secret; user refresh tokens
/// remain private and are stored only in the Windows profile.
/// </summary>
public static class GoogleOAuthConfiguration
{
    private const string ClientIdKey = "GoogleOAuthClientId";
    private const string ClientSecretKey = "GoogleOAuthClientSecret";

    public static string TokenDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker");

    public static string TokenPath => Path.Combine(
        TokenDirectory,
        "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");

    public static bool HasStoredAuthorization => File.Exists(TokenPath);

    public static bool IsConfigured => TryGetClientSecrets(out _);

    public static ClientSecrets GetClientSecrets() =>
        TryGetClientSecrets(out var secrets)
            ? secrets
            : throw new InvalidOperationException(
                "Google OAuth is not configured in this development build. " +
                "Install an official release or configure GOOGLE_OAUTH_CLIENT_ID and " +
                "GOOGLE_OAUTH_CLIENT_SECRET for your Windows user.");

    private static bool TryGetClientSecrets(out ClientSecrets secrets)
    {
        var metadata = typeof(GoogleOAuthConfiguration).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? "",
                StringComparer.OrdinalIgnoreCase);

        // Environment values keep local development possible without restoring
        // the old credentials.json workflow. Official releases use metadata.
        var clientId = metadata.GetValueOrDefault(ClientIdKey);
        var clientSecret = metadata.GetValueOrDefault(ClientSecretKey);
        if (string.IsNullOrWhiteSpace(clientId))
            clientId = GetDevelopmentValue(ClientIdKey, "GOOGLE_OAUTH_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(clientSecret))
            clientSecret = GetDevelopmentValue(ClientSecretKey, "GOOGLE_OAUTH_CLIENT_SECRET");

        secrets = new ClientSecrets
        {
            ClientId = clientId.Trim(),
            ClientSecret = clientSecret.Trim(),
        };
        return secrets.ClientId.Length > 0 && secrets.ClientSecret.Length > 0;
    }

    private static string GetDevelopmentValue(string name, string alternateName)
    {
        var value = Environment.GetEnvironmentVariable(name) ??
                    Environment.GetEnvironmentVariable(alternateName);
        if (!string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
            return value ?? "";

        // Visual Studio and already-running terminals do not always inherit a
        // newly configured user environment. Reading that scope directly makes
        // the one-time development setup available immediately.
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
               Environment.GetEnvironmentVariable(alternateName, EnvironmentVariableTarget.User) ??
               "";
    }
}
