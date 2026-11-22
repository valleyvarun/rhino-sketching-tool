# Crayon v0.2 for Rhino 8 for Mac

Crayon adds a drawing toolbar and an Agent, Sketch and Layers panel to Rhino. Crayon v0.2 targets Rhino **8.17 or later** on Intel and Apple silicon. For the optional RhinoMCP model connection in chat, use **Apple silicon and Rhino 8.20 or later**. McNeel's Mac package targets .NET 8, the default runtime from Rhino 8.20. [RhinoMCP package project](https://github.com/mcneel/RhinoAI/blob/plugin-v0.4.0/rhino/plugin/RhinoAI.csproj) · [Rhino runtime guide](https://developer.rhino3d.com/guides/rhinocommon/moving-to-dotnet-core/)

## Install

Quit Rhino, open [CrayonSetup.pkg](dist/CrayonSetup.pkg), and follow the installer. After setup, open Rhino and enter `Crayon` at the command line.

Setup installs and verifies the Crayon plug-in for the current user. It activates v0.2 even if a higher-numbered preview was previously installed. It reuses or installs Claude Code and Codex, and sets up RhinoMCP on supported Macs. Choose your assistant and sign in when you first use Agent in Rhino. Setup logs are in `~/Library/Logs/Crayon`. To uninstall Crayon, remove `crayon-rhino` in Rhino's Package Manager and restart Rhino.

## Drawing and chat

- **Brushes.** Rhino for Mac paints the ink with overlapping round points. Their spacing, diameter and opacity reproduce the brush width, hardness and pressure stored on the stroke. A very faint stroke can look beaded. The same curves and brush settings open on Windows. The toolbar and comment labels use viewport bitmap drawing introduced on Mac in Rhino 8.17.
- **Input.** A tablet or Sidecar pen supplies pressure through macOS mouse events. The mouse uses the same speed-based pressure as on Windows. Esc returns to Select when Rhino is idle. The Sketch tab provides the four view controls. Pinned comments open a text dialog in their document window.
- **Documents.** Each inspector belongs to its model. Switching models starts a fresh assistant session for the active model. Previous conversations are available in History. Local chat history and scratch files live under `~/Library/Application Support/Crayon`.
- **Assistant programs.** Crayon looks for Claude Code and Codex in the shell path, common installation folders and supported editor extensions. **Locate** lets you select an executable. Claude sign-in opens Terminal. Codex uses its own account sign-in flow.

## Build

The project in `mac/plugin` compiles the shared `rhino-plugin` sources for .NET 7, which Rhino 8 can load with its .NET 7 or .NET 8 runtime. Rhino supplies RhinoCommon, Eto and its Mac implementation of System.Drawing at runtime. See [McNeel's runtime guide](https://developer.rhino3d.com/guides/rhinocommon/moving-to-dotnet-core/).

From the repository root, build the assembly with a .NET SDK. Packaging also requires Rhino 8's Yak executable and PowerShell (PowerShell 7 on a Mac).

```powershell
dotnet build mac/plugin/RhinoSketchingTool.Mac.csproj -c Release
./mac/scripts/Build.ps1
```

Both commands read the default version from `release.version` in the repository root. The build writes `mac/dist/CrayonSetup.pkg` and its SHA-256 file. Use `-YakPath` for a nonstandard Yak location.

## Signing a public release without a Mac

The local v0.2 installer is unsigned. For the ordinary download-and-open experience, the publisher signs the installer with a **Developer ID Installer** certificate and has Apple notarize it. Customers use the same Download for Mac button once the signed file is in place. [Apple distribution guide](https://developer.apple.com/macos/distribution/)

The repository includes a manually triggered **Mac installer** workflow for a GitHub-hosted Mac. Once the changes are in the repository's default branch, run it from **Actions** to validate the package. Validation can run without an Apple account. [Workflow](../.github/workflows/mac-installer.yml)

To produce a signed release, configure the `mac-signing` GitHub environment with these secrets from the publisher's Apple Developer team, then run the workflow with **sign_and_notarize** enabled.

| Environment secret | Value |
| --- | --- |
| `MAC_INSTALLER_P12_BASE64` | Base64 of the exported Developer ID Installer certificate and private key in PKCS#12 format |
| `MAC_INSTALLER_P12_PASSWORD` | Password for that export |
| `MAC_INSTALLER_IDENTITY` | Full certificate identity, including the team ID |
| `ASC_KEY_ID`, `ASC_ISSUER_ID` | Key ID and issuer ID for a team App Store Connect API key authorized for notarization |
| `ASC_PRIVATE_KEY_BASE64` | Base64 of that key's `.p8` file |

The **Crayon-mac-signed** workflow artifact contains the signed, notarized `CrayonSetup.pkg` and checksum. Use those two files in `mac/dist/` when publishing the website. [Apple notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

## Verification

The v0.2 installer was built on Windows. Automated checks cover WPF and Eto toolbar rendering, simulated stroke appearance at different widths and hardnesses, the assistant protocols with mock processes, and installer behavior in a disposable package directory. The package has yet to be run inside Rhino for Mac. Test commands and the native smoke script are in the [test guide](tests/README.md).

For the first native run, use a blank model and run the smoke script. Then check the parts that need the physical display and input devices.

1. Draw on an object, a plane, the 3D cursor and a saved view. Check that the toolbar hit targets align on Retina, brush width follows zoom, and a soft brush has a softer edge than a hard brush. Try a pressure-sensitive stroke if a tablet is available.
2. Erase part of a stroke, press Esc, add a pinned comment, and edit then cancel a change to that comment. Save and reopen the model.
3. On a Mac with RhinoMCP, choose an installed assistant, sign in and ask it to inspect the model. Switch to another open model and check that Layers and the assistant both target the front window.
