# Crayon for Rhino 8

Crayon combines pressure-sensitive sketching, pinned comments, saved-view annotations,
and an assistant in Rhino's dockable sidebar. Run the `Crayon` Rhino command to open it.

## Build and load

Requires Rhino 8 for Windows, a .NET SDK, and .NET Framework 4.8 reference assemblies.

```powershell
# Build and automatically update the installed plugin.
.\scripts\Build.ps1

# Build only, without installing.
.\scripts\Build.ps1 -Stage
```

The normal build is `bin/Release/RhinoSketchingTool.rhp`. The staged build is
`bin/Validation/RhinoSketchingTool.rhp`. Install by loading the `.rhp` in Rhino.
Rhino keeps loaded plugin assemblies locked until it exits; a running session cannot
hot-reload a rebuilt plugin. The build script installs immediately when possible;
otherwise a hidden updater waits for Rhino to close and then installs automatically.
The previous build is backed up alongside the installed file. Update status is written
to `bin/update-status.txt`. If Crayon was installed with the setup wizard, Rhino loads the
package in `%APPDATA%\McNeel\Rhinoceros\packages\8.0\crayon-rhino` instead: close Rhino and run
`.\scripts\Install-Package.ps1` to install the current build as a new package version. `scripts/Update-Plugin.ps1` can also install an already staged build.
You can also run `dotnet build RhinoSketchingTool.csproj -c Release` directly from
this folder when local PowerShell scripts are disabled.

## Drawing and views

The toolbar holds Select, Pen, Eraser, Comments and Saved views, sized and styled as the
prototype's dock bar (`design-ui-ux/.../design/prototype/index.html`), light or dark with
Crayon's own switch. Every control has a tooltip. Drag its grip to dock it on any edge;
double-click the grip to put it back. Under it, an option row changes with the tool:

- **Pen**: Object | Plane, colour, Snap shapes (the hand-drawn tick).
- **Eraser**: Whole sketch | Part.
- **Comments**: one line of help.
- **Saved views**: Save view, then a chip per Rhino Named View (click to go there, x to delete).

**Saved views are cameras only.** They are Rhino Named Views, in sync with Rhino's Named
Views panel both ways. Going to one changes the camera and nothing else. Sketches are 3D
model content: every sketch shows in every view and never disappears when you orbit.
Nothing is captured automatically. (Sketches tied to a view by earlier builds now show
in every view too.)

Crayon is turned off only with the switch in the panel header; Esc never turns it off.

The colour picker offers presets, saturation/value, hue and hex entry. Colours belong
to individual sketches. Snap shapes conservatively recognizes lines, rectangles and
ellipses for Plane, Cursor and View. Object drawing stays on its surface, so it remains
freehand. Brush pressure, opacity, hardness and presets are available in Sketch.

## Assistants and attachments

The Agent composer includes an **Effort** selector for both Claude Code and Codex.
Its choices follow the selected model's supported levels. Each agent remembers its
own choice; changes apply to the next prompt and preserve the conversation. Choose
**Default** to remove the override (Codex uses the model's advertised default).
Models that do not advertise effort support show only Default.

### Annotation groups

Layers lists sketches and comments in separate, initially collapsed groups. Each
collection has exactly one active group, chosen using its small checkbox; new strokes
or comments go into that group. Eye buttons independently control individual items
and whole groups. Showing a group preserves any individually hidden items. Existing
untagged annotations belong to Group 1. Membership, eye states, active groups and the
following settings are saved in the Rhino document.

Above search, all three switches default on:

- **Context only from active group** supplies only the active sketch group's stroke
  coordinates and the active comment group's text/positions in each assistant prompt.
  Turning it off supplies all annotation groups. The snapshot supersedes earlier
  annotation context; assistants are instructed to ignore other groups when using
  Rhino tools or viewport images. This scopes annotation context, not access to the
  ordinary Rhino model or an assistant's general tool permissions.
- **New group for new prompt** creates and activates a fresh group in each collection
  after a successfully completed assistant turn, but only when its active group
  contains annotations. Empty sketch and comment groups stay active independently;
  hidden items still count, and deleted items do not. Failed, interrupted or denied turns
  keep their current groups. Turning it off keeps collecting in the selected groups.
- **Visibility off after context used** hides the populated sketch/comment groups
  included in a prompt after it completes successfully. It also works when automatic
  group creation is off. Failed, interrupted or denied turns leave visibility alone.
  Changing active groups while the assistant works does not change which groups get
  hidden. Empty groups stay visible, and the eye controls can show used groups again.

The snapshot sent with a prompt stays fixed while the assistant works. Visibility
controls affect drawing display independently of context-group membership.

On first opening Agent, choose **Claude Code** or **Codex**. Each uses its
installed CLI and existing account; sign-in and Locate actions are available in the
account menu. Crayon does not store API keys. Provider choices, conversations, models,
pending attachments and drafts are kept separate while switching assistants.

The composer's **+** opens a file picker. Attached files appear above the input and can
be removed before sending. Images are sent as image input; other files are supplied as
explicit local file references for the assistant's file-reading tools. Images are limited
to 5 MB. New chat and account settings have separate controls in the chat header.

Codex uses its local `app-server` protocol, including ChatGPT sign-in, model discovery,
streaming and approval requests. Claude uses its streaming CLI protocol. Installing a
CLI and signing in is required for that provider; neither is bundled in this plugin.

### Automatic RhinoMCP connection

Crayon checks for McNeel's **Rhino-MCP-Platform** on startup. If it is missing, the
Agent tab explains how to install it and provides **Open Package Manager**. Install
the package and restart Rhino; no MCP configuration files or terminal commands are
needed. Sketching remains available without the package.

When an assistant starts, Crayon loads the installed package, starts its local server
if needed, and supplies its document-specific HTTP `/agent` endpoint to Claude Code
or Codex. Configuration applies only to that assistant process; existing user CLI
settings are not rewritten. Chats reset when their document is closed or replaced so
they cannot continue using the previous document's connection. Disabled, outdated or
failed dependencies produce an actionable message with **Retry**.
Codex MCP tool approvals appear in chat with **Allow once**, **Allow for chat**, and
**Deny**; they are not silently declined. Crayon checks the local server before each
message and reconnects the assistant after a reported transport failure. Failed tool
rows expose the actual error in their tooltip. A failed modeling operation is never
automatically replayed, avoiding duplicate geometry.

Selecting either assistant, returning to the Agent tab, or reopening its panel checks
the server immediately. This uses the same RhinoMCP host API as `MCPStart`, without
its interactive port prompt. A healthy server is reused; a stopped server starts
automatically and idle assistant transports reconnect even if the port is reused.
Opening the panel during a running turn does not interrupt it.

This integration targets McNeel's package (plug-in ID
`2668d7ed-f507-4a68-8295-8172147a0e39`), including its RhinoAI naming in version 0.4.
Other unrelated projects named RhinoMCP are not interchangeable with it. The public
host API is accessed without an assembly dependency so a missing package cannot
prevent Crayon from loading. See [McNeel's platform documentation](https://mcneel.github.io/RhinoAI/docs/).

## Source layout

| Folder | Responsibility |
| --- | --- |
| `Plugin/` | Rhino plugin registration, lifecycle and command |
| `Drawing/` | Sketch state, pointer input, conduits, brush rendering, comments, saved views and shape recognition |
| `UI/` | Docked sidebar hosts, viewport bars, popups and shared painting |
| `UI/Web/` | Embedded sidebar and assistant pages |
| `Agent/` | Conversations, attachments, RhinoMCP discovery and session configuration |
| `Agent/Providers/` | Claude and Codex CLI transport adapters |
| `Infrastructure/` | Shared JSON serialization |
| `Assets/Brand/` | Official Crayon logo files copied from `design-ui-ux` |
| `scripts/` | Build and headless UI validation |

Logical embedded-resource names stay stable when files move. The plugin GUID and panel
GUID also remain unchanged. The design reference is
`../design-ui-ux/rhino-sketching-tool-crayon-design-ui-ux/design/`.

## Validation

```powershell
.\scripts\Build.ps1 -Stage
node .\scripts\verify-ui.mjs
..\tests\Geometry\run.ps1
dotnet build ..\tests\chat-protocol\ChatProtocol.csproj -c Release
& ..\tests\chat-protocol\bin\Release\net48\ChatProtocol.exe (Get-Command node).Source ..\tests\chat-protocol\mock-cli.js
```

The UI smoke test uses installed Chrome (or `CHROME_PATH`), an isolated temporary profile,
and the actual embedded HTML. It checks provider selection, attachment actions, composer
height, named-view selection and responsive sidebar widths. Screenshots go to
`bin/UiValidation`. It does not send assistant prompts or modify accounts.

[`../tests/Native/README.md`](../tests/Native/README.md) describes the isolated Rhino
integration test for capture/restore, sketch isolation, erasing, save/reopen and viewport
bar bounds. It uses a new blank Rhino session and exits that session after testing.
