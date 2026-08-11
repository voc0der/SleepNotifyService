SleepNotifyService
==================

A lightweight Windows service that sends [Apprise](https://github.com/caronc/apprise)
notifications when a machine changes power or session state.

It reports four events:

| Event | Trigger |
| --- | --- |
| 🔓 PC Online | Service start, session logon, remote connect, or unlock |
| 🌙 Going To Sleep | `Suspend` power broadcast, sent before the machine suspends |
| ✅ System Resumed | Resume from suspend, once the network is reachable |
| 🛑 Shutdown/Restart Initiated | Planned shutdown or restart |

Notifications are best-effort by design. The service never throws back to the Service Control
Manager, and pre-sleep delivery uses short timeouts so it cannot delay a suspend.

Requirements
------------

* Windows with .NET 8 support
* No runtime install needed — release builds are self-contained
* Administrator rights to install (registering a service requires them)

Installation
------------

Download the latest `SleepNotifyService-v*-win-x64.zip` from the
[Releases](https://github.com/voc0der/SleepNotifyService/releases) page and verify it:

```powershell
Get-FileHash .\SleepNotifyService-v1.0.0-win-x64.zip -Algorithm SHA256
```

Compare against the published `.sha256` file, extract, then run setup. Double-clicking the exe
does the same thing and prompts for elevation:

```
> SleepNotifyService.exe setup
Apprise URL: https://apprise.example.com/notify/apprise
Apprise tag: my_alerts
Label [AdamSmasher]:

  Installed  C:\Program Files\SleepNotifyService\SleepNotifyService.exe
  Wrote      C:\ProgramData\SleepNotifyService\config.json
  Registered SleepNotifyService (start=auto)
  Started    service
```

Setup copies the exe into Program Files, writes the configuration, registers the service to
start automatically, and starts it. Re-running `setup` reconfigures an existing install.

For scripted or fleet deployment, supply everything up front:

```powershell
SleepNotifyService.exe setup --url https://apprise.example.com/notify/apprise --tag my_alerts --quiet
```

Other commands:

```powershell
SleepNotifyService.exe status              # service state and config location
SleepNotifyService.exe uninstall           # stop and remove the service, keep config
SleepNotifyService.exe uninstall --purge    # also delete config and installed files
SleepNotifyService.exe help
```

Configuration
-------------

Settings live in `C:\ProgramData\SleepNotifyService\config.json`. The Apprise URL and tag are
encrypted with DPAPI at machine scope, and the file's ACL is reset to grant only `SYSTEM` and
the local `Administrators` group. Because the encryption is machine-bound, copying the file to
another machine will not work — run `setup` there instead.

The label shown in notifications resolves in this order:

1. The `--label` value stored during setup
2. The `NOTIFY_LABEL` environment variable
3. The machine name, in title case

To override the label without re-running setup:

```powershell
[Environment]::SetEnvironmentVariable('NOTIFY_LABEL', 'Living Room PC', 'Machine')
Restart-Service SleepNotifyService
```

Note that services read their environment from the Service Control Manager, which caches it at
boot — a machine-level variable may not take effect until a reboot. Using `setup --label` is
more reliable.

If the service starts without usable configuration, it writes the reason to the Windows
Application event log and stops rather than running and silently dropping every notification.

Building from source
--------------------

```bash
dotnet restore src/SleepNotifyService.csproj
dotnet build src/SleepNotifyService.csproj -c Release
```

To produce the same artifact CI does:

```bash
dotnet publish src/SleepNotifyService.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -o ./artifacts/publish
```

The build is cross-platform; only running the service requires Windows.

Releasing
---------

Releases follow [Semantic Versioning](https://semver.org). Pushing a `vMAJOR.MINOR.PATCH` tag
builds the binary and publishes a GitHub Release with the zip and its SHA-256 checksum:

```bash
git tag v1.0.0
git push origin v1.0.0
```

A tag with a prerelease suffix (`v1.1.0-rc.1`) is marked as a prerelease. The workflow can also
be run manually from the Actions tab with an explicit version. No repository secrets are
required — the released binary is identical for everyone and contains no endpoint of its own.

Security
--------

Credentials are never compiled into the binary. The Apprise URL is supplied at install time and
stored DPAPI-encrypted under `%ProgramData%`, readable only by `SYSTEM` and local
administrators, and useless if copied to another machine. Published release artifacts contain
no secrets, so they are safe to distribute publicly.

Contributing
------------

See [CONTRIBUTING.md](CONTRIBUTING.md).

License
-------

[MIT](LICENSE) © voc0der
