using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;

/// <summary>
/// Entry point. The Service Control Manager launches this with --service; every other
/// invocation is a human at a console, so those paths may prompt and print freely.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new SleepNotifyService());
            return 0;
        }

        // Double-clicking passes no arguments; treat that as the guided install.
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "setup";

        try
        {
            return verb switch
            {
                "setup"                    => Setup(args),
                "uninstall"                => Uninstall(args),
                "status"                   => Status(),
                "help" or "--help" or "-h" => Help(0),
                _                          => Unknown(verb)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            PauseIfOwnConsole();
        }
    }

    // --- Verbs ---

    private static int Setup(string[] args)
    {
        var options = Options.Parse(args);

        if (!IsElevated())
        {
            // A double-clicked installer should offer UAC rather than just failing.
            if (!options.Quiet && RelaunchElevated(args)) return 0;

            Console.Error.WriteLine("Setup must run as an administrator.");
            return 1;
        }

        var url   = options.Url   ?? Ask("Apprise URL", "--url",   required: true,  shown: null, options.Quiet);
        var tag   = options.Tag   ?? Ask("Apprise tag", "--tag",   required: false, shown: null, options.Quiet);
        var label = options.Label ?? Ask("Label",       "--label", required: false, shown: SleepNotifyService.DefaultLabel, options.Quiet);

        ValidateUrl(url);

        Console.WriteLine();

        Installer.StopIfRunning();

        var exePath = Installer.InstallBinary();
        Report($"Installed  {exePath}");

        new AppConfig { Url = url, Tag = tag, Label = label }.Save();
        Report($"Wrote      {AppConfig.FilePath}");

        if (Installer.Exists())
        {
            Installer.Delete();
            Report($"Replaced   existing '{Installer.ServiceName}' registration");
        }

        Installer.Create(exePath);
        Report($"Registered {Installer.ServiceName} (start=auto)");

        Installer.Start();
        Report("Started    service");

        Console.WriteLine();
        Console.WriteLine($"Notifications will be labelled '{(string.IsNullOrWhiteSpace(label) ? SleepNotifyService.DefaultLabel : label)}'.");
        return 0;
    }

    private static int Uninstall(string[] args)
    {
        var options = Options.Parse(args);

        if (!IsElevated())
        {
            if (!options.Quiet && RelaunchElevated(args)) return 0;

            Console.Error.WriteLine("Uninstall must run as an administrator.");
            return 1;
        }

        if (Installer.Exists())
        {
            Installer.StopIfRunning();
            Installer.Delete();
            Report($"Removed    {Installer.ServiceName}");
        }
        else
        {
            Report($"Not found  {Installer.ServiceName} (nothing to remove)");
        }

        if (options.Purge)
        {
            AppConfig.Delete();
            Installer.RemoveBinary();
            Report("Purged     configuration and installed files");
        }
        else if (AppConfig.Exists())
        {
            Console.WriteLine();
            Console.WriteLine($"Configuration kept at {AppConfig.FilePath}. Use 'uninstall --purge' to remove it.");
        }

        return 0;
    }

    private static int Status()
    {
        var status = Installer.Status();

        Console.WriteLine($"Service        {(status is null ? "not installed" : status.ToString()!.ToLowerInvariant())}");
        Console.WriteLine($"Configuration  {(AppConfig.Exists() ? AppConfig.FilePath : "not configured")}");

        if (AppConfig.Exists() && !AppConfig.TryLoad(out _, out var error))
            Console.WriteLine($"Warning        {error}");

        return 0;
    }

    private static int Help(int exitCode)
    {
        Console.WriteLine($"""
            SleepNotifyService - Apprise notifications for Windows sleep, resume, logon, and shutdown.

            Usage:
              SleepNotifyService.exe setup [options]     Install, configure, and start the service
              SleepNotifyService.exe uninstall [--purge] Stop and remove the service
              SleepNotifyService.exe status              Show service and configuration state
              SleepNotifyService.exe help                Show this message

            Setup options:
              --url <url>      Apprise endpoint, e.g. https://example.com/notify/apprise
              --tag <tag>      Apprise tag to route notifications to
              --label <name>   Name shown in notifications (default: {SleepNotifyService.DefaultLabel})
              --quiet          Never prompt; requires --url

            Run with no arguments to be prompted for everything.
            """);
        return exitCode;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown command '{verb}'.");
        Console.Error.WriteLine();
        return Help(1);
    }

    // --- Console helpers ---

    private static void Report(string line) => Console.WriteLine("  " + line);

    /// <summary>
    /// Prompts for a value. <paramref name="shown"/> is display only: accepting it returns an
    /// empty string so the value stays unset and the runtime fallback chain still applies.
    /// </summary>
    private static string Ask(string label, string flag, bool required, string? shown, bool quiet)
    {
        while (true)
        {
            if (quiet)
            {
                if (required) throw new ArgumentException($"--quiet requires {flag} to be supplied.");
                return "";
            }

            Console.Write(shown is null ? $"{label}: " : $"{label} [{shown}]: ");
            var value = (Console.ReadLine() ?? "").Trim();

            if (value.Length > 0) return value;
            if (!required) return "";

            Console.WriteLine($"{label} is required.");
        }
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{url}' is not a valid http or https URL.");
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        var startInfo = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// True when this process owns its console, i.e. it was double-clicked rather than run from
    /// an existing shell. Without a pause the window would vanish before anything can be read.
    /// </summary>
    private static void PauseIfOwnConsole()
    {
        try
        {
            var buffer = new uint[4];
            if (GetConsoleProcessList(buffer, (uint)buffer.Length) > 1) return;

            Console.WriteLine();
            Console.Write("Press Enter to close...");
            Console.ReadLine();
        }
        catch (Exception)
        {
            // No console attached; nothing to keep open.
        }
    }

    // --- Argument parsing ---

    private sealed class Options
    {
        public string? Url   { get; private set; }
        public string? Tag   { get; private set; }
        public string? Label { get; private set; }
        public bool    Quiet { get; private set; }
        public bool    Purge { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--url":   options.Url   = Value(args, ref i); break;
                    case "--tag":   options.Tag   = Value(args, ref i); break;
                    case "--label": options.Label = Value(args, ref i); break;
                    case "--quiet": options.Quiet = true;               break;
                    case "--purge": options.Purge = true;               break;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            return options;
        }

        private static string Value(string[] args, ref int index)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Option '{args[index]}' needs a value.");
            return args[++index];
        }
    }
}
