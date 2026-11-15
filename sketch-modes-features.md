# Sketch Modes: feature list

15 Nov 2026 · Anmol Singla

## What this is

A clickable Rhino window in the browser where people sketch on the model, then pick which sketches to hand to an AI as context. No AI is connected yet: this list says what the mockup does and what the AI would receive. Open it here: [Rhino Sketch Modes](https://claude.ai/artifact/95SEg9fWYkaVX3UaGqtaaj).

## In the viewport

The sketch tools sit in a mode bar above the viewport. They work with a mouse or a pen (pen pressure sets the line width).

- **3D cursor:** a marker you drop on any surface. It lines up with that surface and snaps to corners. It means “here” for new sketch planes and for the AI.
- **Sketch on planes:** ink lands on the grid, on a plane through the 3D cursor, or on a plane facing the camera.
- **Sketch on objects:** ink wraps onto the surface under the pen, curved ones too. Holding still at the end of a stroke snaps it into a clean line or circle.
- **Sketch on the site plan:** the view splits into a 2D plan and the 3D model. A redline on the plan lands on the 3D model in the same place.
- **Marks with meaning:** a circle marks an area, a stroke dragged out of a face means pull or push by that distance, a zigzag means cut, two crossing strokes mean remove, and a tap pins a note. Each mark gets a number and records the object and face it sits on.
- **Ring menu:** hold Space, or press and hold the pen, to pick a mode from a ring around the pointer.
- **Eraser:** drag over ink or marks to delete them, or flip the pen.

## In the side panel (Sketch tab)

The panel lists every sketch and mark, and stays linked to the viewport in both directions.

- **Fly back:** click a row and the camera returns to the angle it was drawn from. Click again to zoom in close. Hovering a row highlights it in the model.
- **Two-way selection:** clicking a mark's label in the viewport opens its row. In Navigate mode, clicking a sketch selects it, and clicking an object narrows the list to that object's sketches.
- **Statuses:** each mark is Open, Done or Dismissed. “Only open” hides finished items.
- **What it means:** expanding a row shows one sentence, e.g. “Pull the top face of Tower out by 3.0 m.” You can fix a misread gesture by changing the mark type, typing the exact distance or editing a note. A short history sits underneath.
- **Grouping:** by object, by kind, or in drawing order. Rows can be renamed by double-clicking.
- **Apply by hand:** pull and remove marks can be applied straight to the model.
- **What changed:** every model change is listed with its own Undo, e.g. “Main block: length 12.0 → 14.0 m (Mark 1, by you)”. “Show in model” tints added parts green, changed parts blue and removed parts red, with a dashed outline of the old shape. “Copy” puts a plain-text summary on the clipboard.

## AI context (the part to integrate)

The user chooses what the AI sees; nothing is sent anywhere yet. Any sketch, mark or redline can be added with the **AI** button on its row, and the 3D cursor with a checkbox in the 3D Cursor tab. Added items show as chips in the “AI context” box, and their labels in the viewport get an AI badge. “Show what the AI gets” displays the text below, and “Copy” copies it.

Each item carries its number and name, its type and status, a one-sentence meaning, where it is (centre and size, or start point and direction for a pull) and the view it was drawn in. Units are meters, Z up. Example from the mockup:

```
Model: Pavilion.3dm, meters, Z up
3D cursor ("here"): (-3.2, -1.6, 4.0) on Main block · top face, pointing (0.0, 0.0, 1.0)

2 · Region 4.2 × 2.4 m (Region mark, open)
  Means: An area on the south face of Main block, about 4.2 × 2.4 m.
  Centre (-3.2, -4.0, 2.0), size 4.2 × 0.0 × 2.4 m
  Drawn in Perspective

3 · Pull 3.0 m (Pull mark, open)
  Means: Pull the top face of Tower out by 3.0 m.
  Starts at (9.0, 3.0, 10.0), direction (0.0, 0.0, 1.0)
  Drawn in Perspective

Window idea (ink)
  Means: A closed outline on Curved wall, about 2.5 × 2.3 m.
  Centre (-15.1, 2.0, 1.8), size 2.1 × 1.3 × 2.3 m
  Drawn in Perspective
```

## Not built yet

- **No AI connection.** There is no prompt box or send step yet. Where the user types the request is still open.
- **Format.** The context is plain text for now. Each item already holds its type, object, face, points and numbers, so JSON would be easy if that suits the AI better.
- **Shape detail.** The text gives a centre and size, not the stroke itself. Open question: should the AI also get the stroke points, or a picture of the view with the marks drawn on it?
- **Acting on marks.** Only pull and remove can be applied by hand. Regions, cuts and notes need the AI (or a tool) to act on them, and the result should land in “What changed” with its own Undo.
- **Gesture reading** uses simple rules in the mockup. Misreads are fixed by hand in the panel.
