# Install and test Crayon

## Install

1. Save your work and **close Rhino 8**.
2. Run **`wizard/dist/CrayonSetup.exe`** and click **Install Crayon**. It reuses or installs
   WebView2, RhinoMCP, Claude Code and Codex, then installs Crayon as a Rhino package.
   (Unsigned: Windows may ask, choose *More info* > *Run anyway*.)
   If a Crayon is already installed, of any version, setup replaces it: there is nothing to
   uninstall or fix first. Your sketches, settings and chats stay as they are.
3. Open Rhino and type **`Crayon`**. The Agent tab asks once per session: Claude Code or Codex.

## Install on a Mac

Use Rhino 8.17 or later on an Intel or Apple silicon Mac. The optional RhinoMCP
model connection needs Apple silicon and .NET 8. Use Rhino 8.20 or later for its bundled runtime.

Quit Rhino, open [CrayonSetup.pkg](mac/dist/CrayonSetup.pkg), and follow the installer.
Once it finishes, open Rhino and type **`Crayon`**.

Publisher signing, Mac verification and build instructions are in the [Mac guide](mac/README.md).

## What to try

- **Pen** option row: *Object | Plane | 3D cursor*. With Plane, **Pick a face** then click an
  object's face to draw on it (the violet *On a face* chip's x goes back to the construction
  plane). With 3D cursor, **Place cursor** then click where it should go.
- **Saved views** are cameras only: sketch, orbit, save a view, go back. Sketches never disappear.
- **Layers**: the checkbox sends a group to the agent; the violet live tick is the group you draw in.
  + adds a group, the pencil renames, the bin deletes (two clicks).
- **Agent**: New chat and History in the panel header; earlier chats reopen and continue.
  With Codex on "Default", if `~/.codex/config.toml` names a model the account can't use,
  Crayon uses the account's own default model and says so in the chat.
- Light / dark in the panel header changes Crayon only (panel, agent, toolbar).

## Build

To change the release, edit the major.minor.patch number in the file below and rebuild both installers. The website reads the same file when the download page opens. Both installers and the Rhino plug-in take their release number from [release.version.txt](release.version.txt).

```powershell
.\wizard\scripts\Build.ps1                 # plug-in + installer -> wizard/dist/CrayonSetup.exe
.\mac\scripts\Build.ps1                    # Mac plug-in + installer -> mac/dist/CrayonSetup.pkg
.\rhino-plugin\scripts\Install-Package.ps1 # or: install the current build as a Rhino package (Rhino closed)
```

Checks: `node rhino-plugin/scripts/verify-ui.mjs`, the Rhino scripts in `tests/Native`
(see its README), and `tests/chat-protocol`. Run the `tests/Native` scripts **before**
installing: they load the plug-in from the build folder, and Rhino then treats an installed
Crayon package as inactive and removes it. (With a package already loaded, Rhino refuses the
build instead: "ID already in use".) Whatever state the tests leave, running setup or
`Install-Package.ps1` again replaces it.

Branched from `main` at `2edd6a6`; `main`'s later toolbar commits are not merged here.
