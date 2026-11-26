`run_in_rhino.py` is an integration test for the rebuilt `rhino-plugin/bin/Validation/RhinoSketchingTool.rhp`. It runs only in a new Rhino process using the `CrayonValidation` settings scheme and refuses to modify a nonempty initial document.

Launch the installed Rhino executable with these arguments in a hidden window:

```powershell
Start-Process 'C:\Program Files\Rhino 8\System\Rhino.exe' -WindowStyle Hidden -ArgumentList '/scheme=CrayonValidation /nosplash /notemplate /runscript="_-RunPythonScript C:\rhino-sketching-tool\tests\Native\run_in_rhino.py _Enter _-Exit _Enter"'
```

Adjust the repository path if needed. The final `Exit` closes only this newly launched process. The script writes its assertions and any exception to `tests/Native/bin/rhino-script.log`; its scratch 3dm stays in that same ignored output folder. Create `tests/Native/bin` before the first run.

Checks cover native view capture/restore, duplicate names and identical-camera views, rename/deletion, and pointer-driven sketches on a camera plane. Shaded viewport captures check that Over view sketches appear over a solid in their source view and follow ordinary depth in another view. The tests also verify fixed world coordinates after a camera move and 3dm reopen, erasing from the moved camera, and brush metadata after a partial erase. The toolbar's actual WPF renderers also export QA images into `rhino-plugin/bin/UiValidation`.

The integration test covers actual drawing/erasing, save/reopen, exact toolbar/picker rendering, and dock/popup bounds for Top, Left and Right at 1200x700, 280x180 and 200x140 viewport sizes. Inspect the exported toolbar and picker images after changing their renderers.

`groups_in_rhino.py` uses the same isolated launch pattern. Groups are sublayers of
Rhino's `Sketch` and `Comments` layers; it verifies where new strokes and comments go,
independent groups being drawn in, item and group eyes (a group's eye is its layer's
light bulb, both ways), what the agent gets, a new group as a prompt is sent, hiding
groups once a prompt has gone through (and not after a failed one), the two option
toggles, and `.3dm` save/reopen, including that the layers alone give the groups back.
It makes no model requests and writes results to `bin/groups-rhino.log`.

`groups_delete_in_rhino.py`, launched the same way, covers adding, renaming and deleting
groups together with their layers, a deleted group returning with its layer, sublayers
made, renamed or deleted in Rhino, and a file from before groups were layers being given
its sublayers. Results go to `bin/groups-delete.log`.

Both were rewritten on 2026-11-18 for groups as layers and have not been run since.
# RhinoMCP integration

Run `mcp_in_rhino.py` in a **new, blank** Rhino process with `/scheme=CrayonValidation
/notemplate`, using the same `_-RunPythonScript` launch pattern as the drawing tests.
It requires McNeel's Rhino-MCP-Platform and both assistant CLIs installed. It loads the
staged plugin, verifies dependency detection and automatic server startup/reuse,
then launches `verify-mcp.mjs` while pumping Rhino's UI thread.

The client probe checks MCP initialization, tool discovery, a read-only `get_context`
call, real Codex `mcpServerStatus/list`, and real Claude Code `mcp_status`. It sends
**no model prompts** and does not edit assistant configuration. Results are written to
`bin/mcp-rhino.log` and `bin/mcp-clients.log`. The fixture contains local executable
paths and a loopback URL, never credentials. Close only this dedicated test process
after completion.

The same native script then stops/restarts RhinoMCP and runs `ChatProtocol.exe
--live-mcp` with the fixture. **This stage sends one real Codex model prompt** and
tests the production `CodexSession` approval path. The harness allows only the exact
ten-box test macro, denies any other approval, and checks that ten real Rhino objects
exist in its originally blank document. No user document is touched. Build the chat
protocol test project first. Detailed results go to `bin/mcp-model.log`.
