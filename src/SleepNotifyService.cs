using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class SleepNotifyService : ServiceBase
{
    // Loaded from %ProgramData% at start; see AppConfig.
    private static AppConfig _config = new AppConfig();
    private static string    _label  = DefaultLabel;

    // Emoji icons via Unicode escapes (safe to keep source ASCII)
    private const string ICON_ONLINE   = "\U0001F513"; // 🔓
    private const string ICON_SLEEP    = "\U0001F319"; // 🌙
    private const string ICON_RESUME   = "\u2705";     // ✅
    private const string ICON_SHUTDOWN = "\U0001F6D1"; // 🛑

    // Timeouts
    private const int PreSleepConnectTimeoutMs = 1000;  // pre-sleep: be aggressive
    private const int PreSleepTotalTimeoutMs   = 2000;
    private const int ResumeWaitNetworkSec     = 45;    // wait for NIC after resume
    private const int OnlineWaitNetworkSec     = 45;    // wait for NIC on start/logon

    // Dedup windows
    private static readonly TimeSpan SessionOnlineDedup = TimeSpan.FromSeconds(10);

    // Reused HTTP client
    private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

    // Power hold handle
    private IntPtr _powerReq = IntPtr.Zero;

    // Dedup state
    private DateTime _lastOnline = DateTime.MinValue;
    private int _resumeGate = 0; // interlocked dedupe for dual resume triggers

    /// <summary>Label used when neither the config nor NOTIFY_LABEL supplies one.</summary>
    public static string DefaultLabel => GetNiceMachineName();

    public SleepNotifyService()
    {
        ServiceName = "SleepNotifyService";
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        if (!AppConfig.TryLoad(out var config, out var error))
        {
            // Nothing can be delivered without an endpoint, so fail visibly in the Event Log
            // rather than sitting in a running state that silently drops every notification.
            EventLog.WriteEntry(error, EventLogEntryType.Error);
            ExitCode = 1066; // ERROR_SERVICE_SPECIFIC_ERROR
            Stop();
            return;
        }

        _config = config;
        _label  = string.IsNullOrWhiteSpace(config.Label)
                      ? Environment.GetEnvironmentVariable("NOTIFY_LABEL") ?? DefaultLabel
                      : config.Label!;

        _ = Task.Run(async () =>
        {
            await WaitForNetworkAsync(OnlineWaitNetworkSec);
            var body = Compose(ICON_ONLINE, "PC Online", _label, "has come **online**.");
            await SendAsync(body);
        });
        base.OnStart(args);
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        if (changeDescription.Reason == SessionChangeReason.SessionLogon ||
            changeDescription.Reason == SessionChangeReason.RemoteConnect ||
            changeDescription.Reason == SessionChangeReason.SessionUnlock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastOnline > SessionOnlineDedup)
            {
                _lastOnline = now;
                _ = Task.Run(async () =>
                {
                    await WaitForNetworkAsync(OnlineWaitNetworkSec);
                    var body = Compose(ICON_ONLINE, "PC Online", _label, "has come **online**.");
                    await SendAsync(body);
                });
            }
        }
        base.OnSessionChange(changeDescription);
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            if (powerStatus == PowerBroadcastStatus.Suspend)
            {
                CreateAndSetPowerHold("SleepNotify pre-sleep send");
                TrySendPreSleep();
            }
            else if (powerStatus == PowerBroadcastStatus.ResumeSuspend ||
                     powerStatus == PowerBroadcastStatus.ResumeAutomatic ||
                     powerStatus == PowerBroadcastStatus.ResumeCritical)
            {
                if (Interlocked.Exchange(ref _resumeGate, 1) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WaitForNetworkAsync(ResumeWaitNetworkSec);
                            var body = Compose(ICON_RESUME, "System Resumed", _label, "has **resumed** from **sleep**.");
                            await SendAsync(body);
                        }
                        finally
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10));
                            Interlocked.Exchange(ref _resumeGate, 0);
                        }
                    });
                }
            }
        }
        catch { /* never throw to SCM */ }
        finally
        {
            ClearPowerHold();
        }
        return true;
    }

    protected override void OnShutdown()
    {
        try
        {
            var body = Compose(ICON_SHUTDOWN, "Shutdown/Restart Initiated", _label, "is performing a planned **shutdown/restart**.");
            _ = SendAsync(body, connectMs: 1000, totalMs: 3000);
        }
        catch { }
        base.OnShutdown();
    }

    // --- Formatter: "🔓 PC Online:\n`AdamSmasher` has come **online**." ---
    private static string Compose(string icon, string title, string subject, string action)
        => $"{icon} {title}:\n`{subject}` {action}";
    // If your client prefers HTML breaks, use:
    // => $"{icon} {title}:<br>`{subject}` {action}";

    // --- Helpers ---
    private static string GetNiceMachineName()
    {
        var m = Environment.MachineName ?? "This PC";
        return (m.Length <= 1) ? m : char.ToUpperInvariant(m[0]) + m.Substring(1).ToLowerInvariant();
    }

    private void TrySendPreSleep()
    {
        var body = Compose(ICON_SLEEP, "Going To Sleep", _label, "is going to **sleep**.");
        _ = SendAsync(body, connectMs: PreSleepConnectTimeoutMs, totalMs: PreSleepTotalTimeoutMs);
    }

    private async Task WaitForNetworkAsync(int maxSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(maxSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReach443Async("1.1.1.1", 443, 600)) return;
            await Task.Delay(1200);
        }
    }

    private static async Task<bool> CanReach443Async(string host, int port, int timeoutMs)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private async Task SendAsync(string body, int? connectMs = null, int? totalMs = null)
    {
        using var cts = new System.Threading.CancellationTokenSource();
        if (totalMs.HasValue) cts.CancelAfter(totalMs.Value);

        var payload = new Dictionary<string, string>
        {
            ["body"]   = body,
            ["format"] = "markdown"
        };
        if (!string.IsNullOrEmpty(_config.Tag)) payload["tag"] = _config.Tag;

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        // Bare "application/json": StringContent would otherwise append "; charset=utf-8".
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var sendTask = Http.PostAsync(_config.Url, content, cts.Token);

        if (connectMs.HasValue)
        {
            // crude connect-timeout: abandon if not connected fast
            var raced = await Task.WhenAny(sendTask, Task.Delay(connectMs.Value, cts.Token));
            if (raced != sendTask) return;
        }

        try { using var resp = await sendTask; } catch { /* best-effort */ }
    }

    // --- Power hold P/Invoke ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    private enum PowerRequestType
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired  = 1,
        PowerRequestAwayModeRequired = 2
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT Context);

    [DllImport("kernel32.dll")]
    private static extern bool PowerSetRequest(IntPtr hPowerRequest, PowerRequestType RequestType);

    [DllImport("kernel32.dll")]
    private static extern bool PowerClearRequest(IntPtr hPowerRequest, PowerRequestType RequestType);

    private void CreateAndSetPowerHold(string reason)
    {
        try
        {
            var ctx = new REASON_CONTEXT { Version = 0, Flags = 0, SimpleReasonString = reason };
            _powerReq = PowerCreateRequest(ref ctx);
            if (_powerReq != IntPtr.Zero) PowerSetRequest(_powerReq, PowerRequestType.PowerRequestSystemRequired);
        }
        catch { }
    }

    private void ClearPowerHold()
    {
        try
        {
            if (_powerReq != IntPtr.Zero) PowerClearRequest(_powerReq, PowerRequestType.PowerRequestSystemRequired);
            _powerReq = IntPtr.Zero;
        }
        catch { }
    }
}
