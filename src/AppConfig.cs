using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>
/// Machine-scoped configuration stored under %ProgramData%. The Apprise endpoint and tag are
/// encrypted with DPAPI at LocalMachine scope, so a copied config file is useless elsewhere.
/// </summary>
internal sealed class AppConfig
{
    private const string FolderName = "SleepNotifyService";
    private const string FileName   = "config.json";

    // Extra entropy so another DPAPI consumer on the same machine cannot trivially unprotect.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SleepNotifyService/v1");

    public string  Url   { get; init; } = "";
    public string  Tag   { get; init; } = "";
    public string? Label { get; init; }

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    public static string FilePath => Path.Combine(DirectoryPath, FileName);

    public static bool Exists() => File.Exists(FilePath);

    private sealed class Model
    {
        public int     Version      { get; set; } = 1;
        public string? ProtectedUrl { get; set; }
        public string? ProtectedTag { get; set; }
        public string? Label        { get; set; }
    }

    public static bool TryLoad(out AppConfig config, out string error)
    {
        config = new AppConfig();

        if (!File.Exists(FilePath))
        {
            error = $"No configuration at {FilePath}. Run 'SleepNotifyService.exe setup' as an administrator.";
            return false;
        }

        try
        {
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
            if (model is null || string.IsNullOrWhiteSpace(model.ProtectedUrl))
            {
                error = $"Configuration at {FilePath} has no Apprise URL. Re-run 'setup'.";
                return false;
            }

            config = new AppConfig
            {
                Url   = Unprotect(model.ProtectedUrl),
                Tag   = string.IsNullOrEmpty(model.ProtectedTag) ? "" : Unprotect(model.ProtectedTag),
                Label = model.Label
            };
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            // Most often a config copied from a different machine, which DPAPI cannot unprotect.
            error = $"Configuration at {FilePath} could not be read: {ex.Message}";
            return false;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);

        var model = new Model
        {
            ProtectedUrl = Protect(Url),
            ProtectedTag = string.IsNullOrEmpty(Tag) ? null : Protect(Tag),
            Label        = string.IsNullOrWhiteSpace(Label) ? null : Label
        };

        File.WriteAllText(FilePath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        Restrict(FilePath);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        if (Directory.Exists(DirectoryPath) && Directory.GetFileSystemEntries(DirectoryPath).Length == 0)
            Directory.Delete(DirectoryPath);
    }

    /// <summary>Drops inherited access and grants only SYSTEM and the local Administrators group.</summary>
    private static void Restrict(string path)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }

    private static string Protect(string value)
        => Convert.ToBase64String(ProtectedData.Protect(
               Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    private static string Unprotect(string value)
        => Encoding.UTF8.GetString(ProtectedData.Unprotect(
               Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));
}
