Contributing
============

Rules
-----

* Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
* Use semantic names by default.
* Never commit build output (`bin/`, `obj/`, `publish/`) or a real Apprise endpoint. Endpoints
  are supplied at install time and stored under `%ProgramData%`; nothing sensitive belongs in
  the source tree, and CI fails any commit that adds one.

Naming
------

* Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
* Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
* PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

Versioning
----------

* Releases follow [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.
* A release is cut by pushing a `vMAJOR.MINOR.PATCH` tag; the workflow derives the assembly
  version from the tag, so no version number is stored in the source tree.
* `fix(...)` implies a patch bump, `feat(...)` a minor bump, and a `!` suffix or a
  `BREAKING CHANGE:` trailer a major bump.

Layout
------

| File | Responsibility |
| --- | --- |
| `src/Program.cs` | Entry point, CLI verbs, elevation, argument parsing |
| `src/SleepNotifyService.cs` | The service itself: power, session, and shutdown handling |
| `src/AppConfig.cs` | Config under `%ProgramData%`, DPAPI encryption, file ACL |
| `src/Installer.cs` | Service registration and control, installed binary |

The Service Control Manager launches the exe with `--service`; every other invocation is a
human at a console. Service code paths must never prompt or write to stdout, because a service
runs in Session 0 with no interactive desktop.

Before Opening a PR
-------------------

* Run `dotnet restore src/SleepNotifyService.csproj`
* Run `dotnet build src/SleepNotifyService.csproj -c Release --no-restore` and confirm it
  is warning-free
* Confirm `git status` is clean apart from your intended changes, and that no `bin/` or `obj/`
  output is staged

Notes
-----

* The project targets `net8.0-windows`, but the build itself is cross-platform: a Linux or
  macOS checkout can `restore`, `build`, and `publish -r win-x64` without a Windows machine.
  Only running the service requires Windows.
* Do not run `dotnet format` across `src/`. The constant blocks and field declarations use
  deliberate column alignment that the default whitespace rules would collapse.
* Release artifacts are self-contained, single-file `win-x64` builds, so no .NET runtime is
  required on the target machine.
