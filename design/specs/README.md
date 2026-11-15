# Building Crayon from the design

This folder is for whoever implements the interface (Varun, or the coding agent working for him). You should not need to read the prototype's HTML: everything it shows is described here and in the tokens.

**Before you start:** answer the [open questions](../README.md#open-questions).

## Read in this order

1. [`../tokens/tokens.json`](../tokens/tokens.json) (or [`tokens.css`](../tokens/tokens.css)): every colour, radius, shadow, font and duration, as W3C design tokens. Use these names; don't invent values.
2. [`components.json`](components.json): every component with its size, tokens, states, motion and which Rhino host it lives in.
3. [`../docs/behaviour.md`](../docs/behaviour.md): how each tool, panel tab and Rhino command behaves, step by step, and the agent hook (`window.Crayon.send`).
4. [`../docs/design-system.md`](../docs/design-system.md): the rules behind the look (when to use the accent, corner radii, motion).
5. [`../docs/decisions.md`](../docs/decisions.md): what was decided and why, so you don't undo a choice by accident.
6. [`../icons/`](../icons/) and [`../brand/`](../brand/): SVGs, `currentColor`, ready to inline.

If anything disagrees, the prototype ([`../prototype/index.html`](../prototype/index.html)) wins: open it in Chrome or Edge and try it. `?frame=rhino` shows it inside a Rhino-style window.

## Where each piece lives in Rhino

| Piece | Rhino host |
| --- | --- |
| Toolbar + option row | A borderless, transparent **WebView2 window** that floats over the active viewport and follows it |
| Side panel (Sketch · Layers · Agent) | A dockable **Rhino panel** hosting a WebView2 (a tab next to Properties and Layers) |
| Sketches, comment pins, plane tint, 3D cursor, snap dot | A **DisplayConduit** in the viewport |
| Saved views | Rhino **Named Views** (`_-NamedView` Save / Restore / Rename / Delete) |
| Crayon's layers | A `Crayon` parent layer with `Sketches`, `Comments` and `Curves` sublayers |
| Hover hints | The **layer full path** of the object under the pointer (never an invented object name) |
| Undo | Rhino's own undo: sketches are Rhino objects |

The web parts can share one HTML/CSS bundle built on `tokens.css`; the C# side passes events both ways (`WebView2.CoreWebView2.PostWebMessageAsJson` / `WebMessageReceived`).

## Suggested build order

1. **Tokens and fonts**: load `tokens.css`; the UI font is the system font (Segoe UI Variable), the wordmark is Instrument Sans 600.
2. **The panel shell**: header (logo, Crayon switch, light/dark), the three tabs, fold-out section headings.
3. **Sketch tab**: presets, stroke settings, the 3D cursor section. Store presets per user.
4. **Toolbar window**: the five tools, the option row, docking to the viewport edges.
5. **Pen and Eraser** with the DisplayConduit; sketches become objects on `Crayon › Sketches`.
6. **Layers tab**: Rhino layers + Crayon layers, AI buttons, details drawer, selection bar.
7. **Comments (Marks)** and **Saved views** (Named Views).
8. **Agent tab**: composer, skills, model list from the backend, replies with steps and Undo / Redo, connected through `window.Crayon.send`.

## Things that are easy to get wrong

- **No keyboard shortcuts.** In Rhino, typing goes to the command line and Space repeats the last command. Everything in Crayon is a click.
- **Sending to the agent is deliberate.** Selecting only selects; the agent gets items only from the **AI** button or **Send to agent**.
- **The accent is for state only.** Selected things are soft grey, never violet; no blue or black filled buttons.
- **Corners are slightly filleted, never pills.** The Crayon switch is not an iOS switch; checkboxes use the hand-drawn tick.
- **Words:** a pen stroke is a *sketch* (Sketch 1, Sketch 2…), never *ink*. No all-caps labels, no explanation text under buttons, no black hint bars.
- **Light / dark changes Crayon's interface only**, never Rhino's viewport.
- **The model menu shows real model names** from the backend, not product names.
