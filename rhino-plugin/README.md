# Crayon for Rhino 8

Crayon combines pressure-sensitive sketching, pinned comments, saved-view annotations,
and an assistant in Rhino's dockable sidebar. Run the `Crayon` Rhino command to open it.

## Build and load

Requires Rhino 8 for Windows, a .NET SDK, and .NET Framework 4.8 reference assemblies.
(Crayon for Rhino 8 for Mac is built from these same sources by `../mac`: what is
Windows-only here has a Mac counterpart under `#if MAC`. See
[`../mac/README.md`](../mac/README.md).)

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
`.\scripts\Install-Package.ps1` to install the current build as a new package version. Like
the setup wizard, it replaces whatever Crayon Rhino has, including one Rhino registered from
a build folder. `scripts/Update-Plugin.ps1` can also install an already staged build.
You can also run `dotnet build RhinoSketchingTool.csproj -c Release` directly from
this folder when local PowerShell scripts are disabled.

## Drawing and views

The toolbar holds Select, Pen, Eraser, Comments and Saved views, sized and styled as the
prototype's dock bar (`design-ui-ux/.../design/prototype/index.html`), light or dark with
Crayon's own switch. Every control has a tooltip. Drag its grip to dock it on any edge;
double-click the grip to put it back. Under it, an option row changes with the tool:

- **Pen**: Object | Plane | 3D cursor | Over view, colour, Snap shapes (the hand-drawn tick). With
  Plane, **Pick a face** then a click puts the drawing plane on an object's face (the *On a face*
  chip's x goes back); with 3D cursor, **Place cursor** then a click moves the cursor; with Over
  view, **Pick a view** opens the list of saved views (see below). **Ctrl + Shift + click**
  with the pen picks a face as well, as it selects one in Rhino (on empty space: back to the
  viewport's own plane). Other modifier keys are left to Rhino.
- **Eraser**: Whole sketch | Part.
- **Comments**: one line of help. A comment can carry a **picture**: point at a comment and
  click the picture button at the end of its label to choose a file, or press Ctrl + V while
  typing a comment with a picture on the clipboard (Windows). It shows as a small thumbnail
  above the label; the button in its corner (two diagonal arrows) makes it larger and smaller
  again, with any tool. Made larger, it has an x that takes it off (Comments tool). The
  picture is kept in the 3dm file on the comment itself, no larger than 1280 pixels.
- **Saved views**: Save view, then a button that says which saved view the viewport is in
  (*Saved views* when it is in none) and opens the list of them all: click one to go there,
  x (twice) to delete it. *Save this view* is at the top of the list too.

**Saved views are cameras.** They are Rhino Named Views, in sync with Rhino's Named Views
panel both ways. Going to one from the Saved views row changes the camera and nothing else,
and nothing is captured automatically. Sketches drawn with Object, Plane or 3D cursor are
3D model content: they show in every view and never disappear when you orbit.

**Over view** is the exception, and is for marking up a view as you would a print of it.
Choose it under the pen, then pick a saved view from the list (or *Save this view* to
save and pick the one on screen): the viewport goes to that view, and what you draw is
drawn over it, not into the model. Such a sketch shows only while a viewport is in that
view, over everything in the model, and disappears as soon as the camera moves away;
going back to the view (from either list, or Rhino's Named Views panel) brings it back.
With the camera moved away, the next press of the pen only returns to the view. The
eraser reaches these sketches in their view only, Select leaves them alone, and Layers
marks them with a small camera. Deleting a saved view in Crayon deletes what was drawn
over it (one Undo step); deleted in Rhino's own panel, its sketches show nowhere but
are still listed in Layers. The agent is told which view such a sketch belongs to, and
that it is not 3D.

Crayon is turned off only with the switch in the panel header; Esc never turns it off.

**Rhino's Auto CPlane is off while Crayon is on.** Both move the viewport's construction
plane (Auto CPlane onto what is selected, Crayon onto a picked face) and each would take
the other's plane for the one to go back to. So while Crayon is on, the drawing plane only
changes when it is picked in Crayon. If Auto CPlane was on, Crayon turns it off the way the
status bar does (Rhino says so in the command history) and turns it on again as soon as
Crayon is off; if it was off, it is left alone. See `Drawing/AutoCPlaneLock.cs`.

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

Layers lists sketches and comments in groups. **A group is a Rhino layer**: a sublayer of
the `Sketch` layer (or of `Comments`), and a sketch is in the group whose layer it is on.
So the groups are saved in the 3dm file, show in Rhino's own Layers panel, and are there
again when the file is opened. What is done in either place is done in both: rename a group
or its layer, turn it off with the group's eye or the layer's light bulb, add a sublayer
in Rhino to get a new group, delete the layer to delete the group.

- The **+** beside Sketches or Comments adds a group, and new sketches go into it. Each
  list has one group being drawn in (its violet tick). There are no groups until something
  is drawn or + is pressed; the layers are made then, not in every document Rhino opens.
- The **trash** deletes a group with one click: its sketches (or comments) and its layer.
  Rhino's Undo brings all of it back. A sketch's own trash works the same way.
- Each item has its own eye too; showing a group again keeps individually hidden items hidden.
- A group that is turned off is not drawn into (the ink would not show): the next sketch
  starts a new group.
- Each group's tick decides whether it goes to the agent with the next message; the eye
  only shows or hides.

A file from before groups were layers has its sketches on the `Sketch` layer itself. When
it is opened, each of its groups is given its sublayer and its sketches are moved there
(without an Undo step, and without the file asking to be saved). Group numbers, the ticks,
the group being drawn in and the two options below are kept as document text.

Under Agent in Layers, both options default on:

- **New group after each prompt**: as a prompt is sent, a new group is started in each list
  whose current group has something in it (an empty group is the new group already). What
  is drawn from then on, also while the agent is still working, belongs to the next
  prompt. It does not wait for the turn to end, so it does not depend on the turn going
  well. Turning it off keeps collecting in the same groups.
- **Hide groups once sent**: when the turn has gone through, the groups the prompt took
  along are turned off and unticked for the agent. Failed, stopped or denied turns, and
  turns in which a tool went wrong, leave them showing, to be sent again. It also works
  with the first option off. The eyes show used groups again.

The snapshot sent with a prompt stays fixed while the assistant works.

On first opening Agent, choose **Claude Code** or **Codex**. Each uses its
installed CLI and existing account; sign-in and Locate actions are available in the
account menu. Crayon does not store API keys. Provider choices, conversations, models,
pending attachments and drafts are kept separate while switching assistants.

The composer's **+** opens a file picker. Attached files appear above the input and can
be removed before sending. Images are sent as image input; other files are supplied as
explicit local file references for the assistant's file-reading tools. Images are limited
to 5 MB. New chat and account settings have separate controls in the chat header.

**Ctrl + V** in the message box pastes text, attaches a picture that is on the clipboard (a
screenshot, a copied image), and attaches files copied in Explorer. Attached images show as
small thumbnails, above the box and in the message once it is sent. Pasted pictures are
kept in `%APPDATA%\Crayon\pasted` for two weeks. (Rhino's panel does not let Ctrl + V reach
the page, so `AgentView` takes the key and hands the page the clipboard.)

A picture on a comment goes to the assistant too, as an image file, when that comment's
group is ticked for the agent: up to six per prompt.

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
| `Mac/` | What Rhino for Mac needs in place of WPF and Win32: drawing, display scale and pen, programs and paths. Compiled into the Mac build only (`#if MAC`) |
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
