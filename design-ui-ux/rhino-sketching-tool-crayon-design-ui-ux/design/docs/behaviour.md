# Behaviour

How Crayon behaves, tool by tool, and what it asks of Rhino. The [prototype](../prototype/index.html) shows all of it working; this page is the written version for building it.

## The pieces

- **Toolbar (the dock)**, floating over the viewport, with an **option row** for the active tool. In Rhino: one borderless, transparent WebView2 window.
- **Side panel** with three tabs: Sketch, Layers, Agent. In Rhino: a dockable panel hosting HTML. Drag its left edge to resize it (300–640 px; double-click resets; remembered).
- **In-viewport drawing**: sketches, comment pins, the drawing-plane preview, the snap dot. In Rhino: a `DisplayConduit`.

## Toolbar

| Tool | Option row |
| --- | --- |
| Select | only when something is selected: *N selected* · **Send to agent** · Clear |
| Pen | Object / Plane · Pick a face · colour · Snap shapes |
| Eraser | Clear all sketches |
| Marks (comments) | one line of help text |
| Saved views | Save view · saved views · Named Views sync |

**No keyboard shortcuts.** Crayon adds none, because they would clash with Rhino: typing goes to Rhino's command line, Space and right-click repeat the last command, and Rhino users set their own aliases. Everything is a click. The only keys are Rhino's own (Esc, Undo) and the usual ones inside a text field (Return to send or confirm).
- **Click a tool** to turn it on; its option row opens.
- **Click the active tool again** to hide or show its option row.
- The option row **stays open** while you draw. It never closes on a click outside.
- **Esc**, in order: closes an open pop-up (the colour picker) → cancels *Pick a face* → closes the option row → goes back to Select.
- **Minimize** (the chevron at the end): only the active tool stays, the option row hides. Click the active tool to bring everything back. Remembered between sessions.

### Motion

- Nothing bounces. Easing is `cubic-bezier(.3,0,.2,1)` (no overshoot).
- The dot under the active tool **glides only when you switch tools**. When the bar changes shape (minimize, restore, docking) it stays put under its icon.
- Each tool icon plays one small animation when the tool turns on: **Marks** draws its two lines in turn, the **camera** blinks its shutter with a small flash (also on *Save view*), the **pen** draws its curve. Pressing an icon gives a tiny press (scale 0.94).
- With *reduce motion* turned on in the OS, all of these are skipped.

## Docking

- Drag the toolbar by its grip (the six dots).
- Where it lands depends on the **pointer**:
  - within **72 px** of the left or right edge → docks on that side and turns **vertical**;
  - near the **top or bottom middle** (within 80 px) → docks there, horizontal;
  - anywhere else → floats where you dropped it.
- **Double-click the grip** to reset it to the bottom centre. The position is remembered.
- It glides into place (240 ms) only while snapping or resetting; otherwise it moves instantly.
- **The option row:**
  - with a top or bottom dock, it sits beside the toolbar (below a top dock, above a bottom one), centred;
  - with a side dock, it sits next to the toolbar, **level with the tool that opened it**, and wraps if the space is narrow.
- A top or bottom dock keeps clear of Rhino's own viewport chrome (e.g. the view tabs). It slides sideways, or steps above or below them when there is no room.

## Pen

- **Object**: the sketch wraps onto the surface under the pointer.
- **Plane**: the sketch lands on the construction plane (the grid) or on a picked face.
  - **Pick a face** arms a one-time pick. The next click on any face moves the drawing plane there, and the row shows a chip: *On ‹layer›* ×. Click × to go back to the grid. Esc cancels the pick.
- **Colour**: one swatch (black by default). Its rainbow ring opens the colour picker and turns slowly on hover.
- **Snap shapes** (checkbox): hold the pen still at the end of a stroke and it becomes a clean line or circle. Keep holding and drag to adjust it. The tick is hand-drawn in the accent colour and is drawn on, or rubbed out, when toggled.

## Eraser

Drag over sketches or comments to erase them. *Clear all sketches* asks for a second click.

## Marks: comments

- Click anywhere to place a comment, then type. Return keeps it. Esc, or leaving it empty, removes it.
- **Where it lands**, in this order:
  1. a **sketch line** within 14 px of the pointer: the pin snaps onto the line (the hover dot fills);
  2. the **surface** under the pointer;
  3. the **grid**.
- **Hover hint** next to the pointer: *Sketch · ‹sketch name›* or *Layer · ‹layer name›*.
- **Naming rule**: never invent a name for geometry. Rhino objects rarely have names, so always show the object's **layer**. Sketches have names we give them, which can be renamed.
- **Linked to the sketch**: a comment on a sketch line keeps a link to that sketch.
  - Hovering the comment shows a small tag, *Sketch · Window idea ✎*. Click the name to rename the sketch; every comment on it and the side panel update.
  - If the sketch is erased, its comments stay but lose the link.
- **Stored per comment**: number, text, 3D point and normal, the linked sketch *or* the object + its layer, and the camera view when it was made.

## Saved views: Rhino Named Views

Saved views **are** Rhino's Named Views. Anything saved here shows up in Rhino's Named Views panel, and the other way round.

| Action | Rhino command |
| --- | --- |
| **Save view** (then type a name; it starts as "View N") | `_-NamedView _Save "View N"`, then `_Rename "View N" "new name"` |
| Click a view | `_-NamedView _Restore "name"` (the camera **snaps instantly**, no fly-through, like Rhino) |
| Hover a view → × | `_-NamedView _Delete "name"` (the chip folds away) |
| Double-click a view | `_-NamedView _Rename "old" "new"` |

- Whenever the list changes, here or in Rhino, the **Named Views** sync arrows turn once (0.9 s) and briefly show in the accent colour.
- The active view is highlighted until you move the camera yourself.
- With the Saved views tool on, left-drag orbits, so you can frame the shot before saving.

## Side panel

The header is the logo and wordmark on the left; on the right, (Agent tab only) **History** and **New chat**, then **Crayon on / off** (a small violet switch) and **Light / dark**. There is no status text. Below it, three tabs with icons: a pencil (Sketch), stacked sheets (Layers) and, for Agent, the **Thinking loop**: a dot circled by one hand-drawn crayon loop, always in violet so it stands apart. It is the agent's icon everywhere: the tab, *Send to agent*, the *sent to the agent* note in Layers, and the top of every reply. Switching tabs plays the icon's own moment: the pencil draws its line, the top sheet lifts as the layer under it slides out, the dot lands and the loop draws itself around it. The Agent tab shows a violet count of the items sent to the agent. **While the agent is thinking** the loop runs (a short stroke chases round it while the dot breathes), on the tab and at the top of the reply next to a quiet *Thinking*; when the answer arrives the loop draws itself once, whole, and stays.

**Crayon on / off** is for when you just want to model: off hides the floating toolbar and its option row, switches you to plain selecting (so nothing gets drawn by accident) and rests Crayon's keys and tool ring. Sketches stay on their layers and the panel stays open; the switch simply turns grey (no message). Turning it back on brings back the tool you had. Remembered between sessions (prototype: `localStorage`, key `crayon.on`).

**Light / dark** switches **Crayon's own interface only** (panel, toolbar, option rows, pop-ups, comment labels). Rhino's viewport keeps its own colours. The choice is remembered (prototype: `localStorage`, key `crayon.ui`).

**Sections** look the same in every tab: a fold-out heading (chevron, title in sentence case, a quiet note on the right such as the preset count or *4 objects*, and sometimes an action like ⋯). Every section starts **open**; click the heading (or Enter / Space) to fold it. No small all-caps labels anywhere.

### Sketch: the stroke every new sketch uses

Like brushes in Illustrator: what you set here is applied to every new stroke. Three sections: Presets, Stroke, 3D cursor.

- **Presets** at the top (Pencil, Marker, Crayon, Redline to start), each shown as a little sample stroke. Click one to use it. Double-click to rename; hover → × to delete.
- **Stroke** settings: colour (four swatches plus the colour picker), weight (1–10 px) and opacity (20–100 %) as soft bars you drag, with the value inside (arrow keys nudge them), and two ticks: pressure (harder presses draw wider) and snap shapes. Ticks everywhere in the panel are the same hand-drawn tick as *Snap shapes* in the toolbar (drawn on when checked, rubbed out when unchecked); no iOS-style switches.
- Change anything and the preset shows *Edited*. **Save to “Preset”** writes the change into it; **Save as new** makes a new preset and asks for a name.
- The colour in the toolbar's option row and this tab are the same setting.
- Presets are kept between sessions (in the prototype: `localStorage`, key `crayon.presets.v1`).
- **3D cursor** (the last section; while it is open the cursor shows in the viewport). A point you place in the model that does two jobs, as in Varun's Blender app: it holds a flat **drawing plane**, so you can sketch in mid-air at an exact spot, and it is **"here" for the agent** (new things are built at the cursor).
  - **X / Y / Z**: its position; type to move it.
  - **Plane**: which way its plane faces: *Surface* (the face it was placed on), *Top* (flat), *Front* or *Side* (upright).
  - *Draw on its plane*: the Pen switches to Plane and every stroke lands on the cursor's plane (shown tinted). The same as choosing *3D cursor* in the Pen's plane menu.
  - *Agent can use it as "here"*: the cursor's position goes with your message.
  - **Place cursor** (then click any face or the grid; it snaps to box corners), **Look at it** (the view snaps straight onto the cursor's plane: Top, Front or Right, or head-on for a slanted face, centred on the cursor) and **↺** (back to the origin).
  - In Rhino: a point plus a plane, like a CPlane origin. *Draw on its plane* can set the active CPlane to it; *Look at it* sets the view to that plane, centred on the cursor.

### Layers: what's in the model, and everything you sketched

Plain rows in two sections (no boxes):
- **Rhino**: the model's layers (Massing, Walls, Site…) with their colour and object count. Eye and padlock are always visible (locked objects can't be selected, as in Rhino). Click a layer to zoom to it.
- **Crayon**: the plug-in's own layers in Rhino, each with a count, eye and padlock, and a fold-out list:
  - **Sketches**: a colour dot and the name (double-click to rename). New ones are called *Sketch 1*, *Sketch 2*… In the interface it is always a **sketch**, never *ink*.
  - **Comments**: the comment's number and its text.
  - **Curves**: sketches you turned into real Rhino curves.
  - Every item ends with **AI** (press it to send that item to the agent; it fills violet; press again to take it back) and a **details** chevron.
- **Details** (from Anmol's panel) opens a small drawer under the row: what it is, its length and the view it was drawn in (*Closed outline · 8.7 m long · drawn in Perspective*); for a comment, its text to edit; and **Zoom to fit**, **Turn into curve** (hand-drawn sketches only) and **Delete**.
- Each item has its own eye. A new sketch flashes once the first time you see it here. Locked sketch layers can't be erased or selected.
- **⋯** in the Crayon header: *Turn all sketches into curves*, *Delete all sketches* (second click to confirm; the model is untouched).

**Selecting, and sending to the agent.** Selecting never sends anything. The agent only gets what you deliberately send it, in either of two ways:
- **AI** on a row in Layers, or
- select sketches (click; **Shift-click** adds or removes; in the viewport the Select tool does the same) and press **Send to agent**, in the Select tool's row or in the bar under Layers.
- Selected rows turn soft grey. The bar under Layers shows *N selected*, **Send to agent**, **Turn into curves** (when hand-drawn sketches are selected) and **Delete**.
- What you sent sits in the Agent message box as chips (× takes one back) and goes with your message, shown as chips on your bubble. The Crayon header in Layers shows how many items are sent.
- **Turn into curves** (*open question for Varun*, see [Open questions](../README.md#open-questions)): the hand-drawn sketch is replaced by clean Rhino curves (a straight stroke becomes a line, a round one a circle, the rest a smooth curve) on the Curves layer, so you can extrude, loft or offset them like any other curve.

### Agent: the chat

Adapted from Gecko, in Crayon's colours.

- Empty state: the agent's icon (the Thinking loop, drawing itself on) and *What are we working on?* (no suggested prompts).
- **Your message**: a grey bubble on the right; copy and time show on hover (always on the latest one).
- **The reply**: the agent's icon above the text. While the agent works the loop runs (a short stroke chases round it, the dot breathes) next to a softly shimmering *Thinking*; when the answer arrives the loop draws itself once, whole, and the text follows.
  - **Steps** appear one at a time (a spinning marker, then a violet dot), then fold away into *N steps* once there is a result.
  - **Result**: a violet check, a bold line (*Built from Canopy outline*) and the detail (*0.3 m slab · 14.1 m² · layer Built by Crayon*), then **Undo** and *Refine it by telling me what to change*. Undo greys it out and becomes **Redo**; Redo puts the change back.
  - Under every reply: **copy**, **good**, **bad**, and the **time**.
- **Composer** (glows violet when focused):
  - **Attach** (paperclip): files show as chips; they go with the message.
  - **Voice mode** (waveform): a violet light rises from the bottom of the panel. *Not connected in the prototype.*
  - **Skills** (the button shows the current one): each tells the model what kind of job this is, so each skill can be trained and tuned on its own.
    - **Sketch to 3D** (`build`): turn what you drew into new geometry.
    - **Edit model** (`edit`): change what's there, following your sketches and comments.
    - **Variations** (`vary`): a few different versions of what you sent, to compare.
    - **Clean up** (`tidy`): turn rough sketches into clean lines, circles and curves (with Undo).
    - **Ask** (`ask`), set apart: questions only, nothing in the model changes.
    The placeholder text follows the skill.
  - **Model** (next to Skills): which model answers. It always shows the **model's real name** (e.g. *Opus 5.5*), never a product or company name. The list comes from the backend (`window.Crayon.models = [{ id, label, sub }]`): whatever the connected agent offers the account, as in Varun's Blender app. The prototype shows Opus 5.5, Fable 5.2, Sonnet 5.5 and Haiku 5.5.
  - **Send**: grey when empty, fills violet as soon as you type, the arrow flies up when sent. Return sends, Shift + Return makes a new line.
- **New chat** keeps the current conversation in **History**; click one there to open it again.

**Connecting the real agent.** The prototype answers with a script. To use the real model, set:

```js
window.Crayon.send = async ({ text, mode, model, files, context, contextText }) => ({
  text,     // the reply
  steps,    // ['Reading 3 sketches from Layers', …], shown one by one
  result,   // optional: { title, detail, undo(), redo() } for a change made in Rhino; Undo calls undo(), Redo calls redo()
});
```

`mode` is the skill: `build`, `edit`, `vary`, `tidy` or `ask`; `model` is the chosen model's id. `context` lists only what was sent to the agent (`id`, `name`, `kind`, plus the 3D cursor when it is shared); `contextText` is the same as plain text with positions, sizes and the view each was drawn in.

## In Rhino: a preview

Open `prototype/index.html?frame=rhino` to see the prototype inside a Rhino 8-style window: menus, the command line (it shows Crayon's commands as you use it), toolbars, the osnap and status bars, and the Crayon panel docked as a tab next to Properties, Layers and Display. The window is a neutral stand-in, not Rhino's own artwork.

## What the plug-in needs from Rhino

- The **layer full path** of the object under the pointer (for hover hints, comments and the *Pick a face* chip).
- **Raycasts** onto surfaces, with the face normal.
- The **construction plane**, and a temporary plane when a face is picked.
- The **Named Views** list, with add, rename, delete and restore, plus change events (to spin the sync arrows).
- The **viewport camera**, saved with each comment and sketch so the panel can jump back to it.
- The **layer table**: names, colours, visibility and lock, with change events, plus a `Crayon` parent layer with `Sketches`, `Comments` and `Curves` sublayers for the plug-in's own items.
