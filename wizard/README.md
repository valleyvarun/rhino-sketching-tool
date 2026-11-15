# Crayon Setup

`dist/CrayonSetup.exe` is the single Windows download linked from the website.
The wizard has two actions: **Install Crayon**, then **Finish**. It has no account,
registration, or sign-in screens. The original Crayon SVGs are copied into `Assets`;
the PNG and Windows icon are rendered from that same artwork.

## What setup does

- Finds Rhino 8 using its installation registry entry. A file picker handles custom installations.
- Requires Rhino to be closed, without terminating it or touching open models.
- Installs WebView2 if missing, using Microsoft's signed Evergreen bootstrapper.
- Installs or upgrades Rhino-MCP-Platform to at least 0.4.0 through Rhino's own Yak tool.
- Reuses Claude Code and Codex when available; otherwise runs their official native installers.
- Installs the bundled Crayon Yak package for the current Windows user, then checks its SHA-256 hash.
- Offers to open Rhino. Crayon shows its sidebar automatically on first load and connects RhinoMCP when an agent opens.

Rhino 8 must already be installed and licensed. Internet access is needed for missing
dependencies. Agent authentication stays in Crayon, outside the setup wizard. No keys,
credentials, global MCP configuration, or accounts are created or changed by setup.

Retries reuse completed components. A dependency or verification failure never shows
the success page. Logs live under `%LOCALAPPDATA%\Crayon\Setup\Logs`. Remove Crayon
through Rhino's Package Manager; shared runtimes and agents are left intact.

## Build

Requires Windows, Rhino 8 (including Yak), a .NET SDK and .NET Framework 4.8 targeting pack.

```powershell
.\wizard\scripts\Build.ps1
# For a versioned release; always use a new version for changed plugin contents:
.\wizard\scripts\Build.ps1 -Version 1.0.0
```

The build stages the latest plugin, builds a local Yak package, embeds it in the
installer, and writes `dist/CrayonSetup.exe` plus `dist/CrayonSetup.sha256`.
Publish the executable at `wizard/dist/CrayonSetup.exe` with the website. Nothing
is published to Yak or a website by the build. Development builds use a unique
prerelease version so Yak never silently reuses different contents under one version.

The generated executable is **unsigned**. Before public distribution, sign it using
your publisher certificate and timestamp service, then regenerate the SHA-256 file.
Signing credentials are not included. Without signing, Windows can show an unknown
publisher/SmartScreen warning. Test the signed release on a clean Windows/Rhino 8 VM
before distributing it; local checks cannot substitute for that environment.

`scripts/Render-Logo.mjs` regenerates the PNG/ICO from the copied original SVG (Node + Chrome).

## Validation

```powershell
dotnet build wizard/tests/SetupTests.csproj -c Release
.\wizard\tests\bin\Release\net48\SetupTests.exe
# Additional read-only checks against installed Rhino, MCP, agents and payload:
.\wizard\tests\bin\Release\net48\SetupTests.exe --local-check
# Render the actual wizard without running the installation:
.\wizard\dist\CrayonSetup.exe --preview C:\absolute\preview.png
```

Tests cover dependency ordering, repeated installs, retries, Rhino-open guards,
verification failures, exact package recognition and embedded payload integrity.
The local check runs existing tools with `--version`; it does not install anything.

## Official dependency sources

- Rhino packaging: https://developer.rhino3d.com/en/guides/yak/installing-and-managing-packages/
- Claude Code: https://code.claude.com/docs/en/setup (`https://claude.ai/install.ps1`)
- Codex: https://github.com/openai/codex (`https://chatgpt.com/codex/install.ps1`)
- WebView2: https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution

Third-party packages are downloaded from their publishers during setup, not redistributed
in this repository. `Setup/Installer.cs` owns the workflow, `SystemHost.cs` owns installation
and detection, and `Window.xaml`/`Program.cs` own the native wizard.
