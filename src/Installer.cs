using System.Diagnostics;
using System.ServiceProcess;

/// <summary>
/// Registers, removes, and controls the Windows service, and owns the installed copy of the exe.
/// </summary>
internal static class Installer
{
    public const  string ServiceName = "SleepNotifyService";
    private const string DisplayName = "Sleep Notify Service";
    private const string Description = "Sends Apprise notifications on sleep, resume, logon, and shutdown.";

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ServiceName + ".exe");

    public static bool Exists() =>
        ServiceController.GetServices().Any(s =>
            string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    public static ServiceControllerStatus? Status()
    {
        if (!Exists()) return null;
        using var controller = new ServiceController(ServiceName);
        return controller.Status;
    }

    /// <summary>Copies the running exe into Program Files unless it is already the installed copy.</summary>
    public static string InstallBinary()
    {
        var source = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable path.");

        if (string.Equals(source, InstalledExePath, StringComparison.OrdinalIgnoreCase))
            return InstalledExePath;

        Directory.CreateDirectory(InstallDirectory);
        File.Copy(source, InstalledExePath, overwrite: true);
        return InstalledExePath;
    }

    public static void RemoveBinary()
    {
        if (!Directory.Exists(InstallDirectory)) return;

        // Never delete the exe we are currently executing from.
        var running = Environment.ProcessPath;
        foreach (var file in Directory.GetFiles(InstallDirectory))
        {
            if (string.Equals(file, running, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (IOException) { /* in use; leave it */ }
        }

        if (Directory.GetFileSystemEntries(InstallDirectory).Length == 0)
            Directory.Delete(InstallDirectory);
    }

    public static void Create(string exePath)
    {
        Sc("create", ServiceName,
           "binPath=", $"\"{exePath}\" --service",
           "DisplayName=", DisplayName,
           "start=", "auto");

        Sc("description", ServiceName, Description);

        // Restart twice on unexpected failure, one minute apart, then leave it stopped.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000//0");
    }

    public static void Delete() => Sc("delete", ServiceName);

    public static void Start()
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Running) return;

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
    }

    public static void StopIfRunning()
    {
        if (!Exists()) return;

        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;

        if (controller.CanStop)
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
        }
    }

    private static void Sc(params string[] args)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sc.exe.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"sc.exe {args[0]} failed with exit code {process.ExitCode}. {stdout.Trim()} {stderr.Trim()}".Trim());
    }
}
