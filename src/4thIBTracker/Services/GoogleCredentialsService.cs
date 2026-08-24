using System.IO;
using Google.Apis.Auth.OAuth2;

namespace FourthIBTracker.Services;

public record CredentialImportResult(string? BackupPath, int ClearedTokenFiles);

/// <summary>
/// Owns the per-user Google OAuth client file. Older versions kept it beside
/// the executable; that file is copied once as a migration source and retained.
/// </summary>
public static class GoogleCredentialsService
{
    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker");

    public static string CredentialsPath =>
        Path.Combine(AppDataDirectory, "credentials.json");

    public static string LegacyCredentialsPath =>
        Path.Combine(AppContext.BaseDirectory, "credentials.json");

    public static bool Exists => File.Exists(CredentialsPath);

    public static void EnsureMigrated()
    {
        if (Exists || !File.Exists(LegacyCredentialsPath)) return;

        Validate(LegacyCredentialsPath);
        Directory.CreateDirectory(AppDataDirectory);
        File.Copy(LegacyCredentialsPath, CredentialsPath, overwrite: false);
    }

    public static void Validate(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected credentials file does not exist.", path);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            if (string.IsNullOrWhiteSpace(secrets.ClientId) ||
                string.IsNullOrWhiteSpace(secrets.ClientSecret))
                throw new InvalidDataException(
                    "The file does not contain a Google OAuth client ID and client secret.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new InvalidDataException(
                "The selected JSON is not a valid Google OAuth client credentials file.", ex);
        }
    }

    public static CredentialImportResult Import(string sourcePath)
    {
        Validate(sourcePath);
        Directory.CreateDirectory(AppDataDirectory);

        string? backupPath = null;
        if (File.Exists(CredentialsPath))
        {
            backupPath = Path.Combine(AppDataDirectory, "credentials.backup.json");
            File.Copy(CredentialsPath, backupPath, overwrite: true);
        }

        var tempPath = CredentialsPath + ".import";
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, CredentialsPath, overwrite: true);
        }
        finally
        {
            // Clean up a partially copied import without masking the real error.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }

        // Refresh tokens belong to the OAuth client that issued them. Replacing
        // the client must force a fresh browser authorization on the next load.
        int cleared = 0;
        foreach (var tokenPath in Directory.EnumerateFiles(
                     AppDataDirectory,
                     "Google.Apis.Auth.OAuth2.Responses.TokenResponse-*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(tokenPath);
            cleared++;
        }

        return new CredentialImportResult(backupPath, cleared);
    }
}
