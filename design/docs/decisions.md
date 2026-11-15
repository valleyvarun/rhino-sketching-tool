# Decisions

What we decided while designing Crayon, and why. Read this before changing something that looks arbitrary: most of it was tried another way first.

## Product and brand

- **Name: Crayon.** From *Harold and the Purple Crayon*, the reference in the Drawing Intelligence research: drawing as a way to talk to the computer.
- **Logo:** a crayon drawing a brush C, plus the lowercase wordmark *crayon* in Instrument Sans. Always the plain mark and wordmark, **never inside an app-icon tile**.
- **Accent: violet `#9731b0`**, from the Drawing Intelligence presentation. Used **only for state** (active tool, comments, ticks, the Crayon switch, AI tags, the agent). It replaced an earlier cobalt blue.
- **Theme: White Paper.** Calm white surfaces that sit inside Rhino without looking like Rhino's own panels or like iOS glass.

## Look and feel

- **Selected = soft grey**, never the accent. **No blue or black filled buttons.**
- **Corners slightly filleted, never pills.** Motion is snappy with no overshoot; nothing bounces.
- **Checkboxes are a hand-drawn tick**, drawn on and rubbed out, never iOS switches. The only switch is Crayon on / off, and it has filleted corners and a square-ish knob.
- **Sliders are soft bars with the value inside, in the UI font.** Thin native sliders and monospace numbers looked "vibe coded".
- **Section headings: fold-out sections, all open at the start**, one style everywhere (chosen from six options in [`../explorations/section-headers.html`](../explorations/section-headers.html)). Small all-caps labels were dropped.
- **No explanation text** under buttons or in banners (e.g. "Every new stroke uses these settings", "Crayon is off, Rhino works as usual"). The interface should explain itself.
- **No black hint bars** at the bottom of the viewport. Small informational tooltips and one line of help in the option row are fine.
- **Words:** a pen stroke is a *sketch*, never *ink*. New ones are *Sketch 1, Sketch 2…*.

## Toolbar

- **Five tools:** Select, Pen, Eraser, Marks (comments), Saved views. Each icon has one small animation when it turns on.
- **Layouts were replaced by Saved views**, which map 1:1 to Rhino's Named Views (after a review with the backend engineer). Restoring a view **snaps instantly**, as in Rhino, with no fly-through.
- **Marks are just comments** that stick to surfaces or sketch lines. Gestures for area, pull, remove and cut were too much.
- **Hovering geometry shows its Rhino layer name**, never an invented object name: the layer is what the plug-in can actually read from Rhino.
- **No keyboard shortcuts.** In Rhino, typing goes to the command line, Space and right-click repeat the last command, and users set their own aliases. Number keys, letter keys, the Space tool ring and Shift + right-click were all removed.
- On a side dock, the option row sits **level with the tool that opened it**.

## Side panel

- **Three tabs: Sketch · Layers · Agent**, each with an icon. The old Info tab was folded into Sketch (the 3D cursor).
- **Sketch** works like brushes in Illustrator: presets plus stroke settings that apply to every new stroke.
- **Layers stays flat and simple.** Card boxes and stroke-thumbnail boxes were rejected as gimmicky. Eye and lock are **always visible**, not on hover. No location text ("on Walls") in rows.
- **The details chevron** next to AI comes from the alternative panel design: it opens a small drawer with what the item is, its length and view, the comment text, and Zoom to fit / Turn into curve / Delete.
- **Sending to the agent is deliberate.** Selecting only selects. The agent gets items from the **AI** button on a row or **Send to agent** (Select tool or Layers bar). Selection-as-context and the lasso were tried and removed.
- **Crayon on / off** sits in the header: off hides the toolbar so you can just model. No message; the grey switch says it.
- **Light / dark changes Crayon's interface only**, never Rhino's viewport or theme.
- **No status text** in the header ("Model ready" was removed).

## Agent

- Adapted from the Gecko panel: replies with folding steps, a result with **Undo ↔ Redo**, copy / good / bad / time, attach, voice (a violet glow), a filling send button. **No suggested prompts.**
- **Skills, not a question.** Instead of asking "what should the agent do?", the composer has skills, each a separate job that can be trained on its own: Sketch to 3D, Edit model, Variations, Clean up, and Ask set apart. **Skills stay general**: Floor plan and Facade were too architecture-specific.
- **The model menu shows real model names** (e.g. *Opus 5.5*), supplied by the backend. "Codex / GPT-5 / Claude" mixed a product, a model and a model family.
- **The agent's icon is the Thinking loop**: a dot circled by one hand-drawn crayon loop, always violet (chosen from ten sleek options in [`../explorations/agent-icons.html`](../explorations/agent-icons.html), after a round of full kid-style doodles was too childish). It is the agent everywhere: the tab, Send to agent, the Layers note, every reply. **While the agent thinks, the loop runs**, on the tab and at the top of the reply.

## 3D cursor

- Modelled on Varun's Blender app: a point you place that holds a **drawing plane** (sketch in mid-air at an exact spot) and is **"here" for the agent** (new things are built at the cursor).
- The section: X / Y / Z, **Plane** (Surface · Top · Front · Side), *Draw on its plane*, *Agent can use it as "here"*, **Place cursor**, **Look at it** (the view snaps onto the plane) and reset. *Grid follows the cursor* and *Snap to corners* were folded in (drawing on its plane moves the grid; placing it always snaps to corners).

## Open

- **Turn into curves**: keep it, move it to the ⋯ menu only, or leave it to the agent's Clean up skill? Varun decides; see [Open questions](../README.md#open-questions).
