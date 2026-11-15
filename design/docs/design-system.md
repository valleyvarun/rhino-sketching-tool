# Design system

The look of Crayon in one page. Every value here is a token in [`tokens/tokens.json`](../tokens/tokens.json) and [`tokens/tokens.css`](../tokens/tokens.css), with the same names as the CSS variables in the [prototype](../prototype/index.html).

**The idea:** a calm, white interface that sits inside Rhino without looking like Rhino's own panels, and without looking like iOS glass. One accent colour, used sparingly. Snappy motion, nothing bounces.

## Colour

| Role | Token | Value | Use |
| --- | --- | --- | --- |
| Accent | `--accent` | `#9731b0` | The violet from the Drawing Intelligence presentation. **Only for state**: the dot under the active tool, comments, the Snap shapes tick, switches, AI tags, focus. |
| Accent tint | `--accent-soft` | 10 % violet | Behind accent text |
| Text | `--ink` | `#15171c` | Text and icons |
| Secondary text | `--ink-2` | `#4a505d` | |
| Muted | `--muted` | `#878d99` | Hints, captions, inactive labels |
| Bars and cards | `--surface` | `#ffffff` | Toolbar, option row, cards |
| Panel | `--surface-2` | `#f6f7f9` | Side panel background |
| Button fill | `--well` | `#f1f2f5` | Resting buttons and chips |
| Selected | `--soft-sel` | `#d9dde4` | **Selected is always this soft grey**, never the accent and never black |
| Lines | `--line`, `--line-2` | 8 % / 14 % ink | Hairlines |
| Destructive | `--danger` | `#e5484d` | Clear all, delete confirms |

Rules:
- **No blue, no black fills** for buttons. Primary buttons in the panel are neutral grey (`--soft-sel`).
- The accent marks *what is on or yours*. It is never a large fill.
- Light theme only for the demo. Dark mode is not final.

## Type

| Role | Token | Font |
| --- | --- | --- |
| Interface | `--f-ui` | The system UI font (Segoe UI on Windows), so it feels native in Rhino. 12–13 px in the toolbar and rows. |
| Numbers, coordinates | `--f-mono` | IBM Plex Mono |
| Wordmark only | `--f-wordmark` | Instrument Sans 600, letter-spacing −0.03em, **always lowercase**: "crayon" |

## Shape

Slightly filleted corners. **Never pill-shaped.**

| Thing | Radius |
| --- | --- |
| Toolbar | 16 px (`--r-dock`) |
| Option row | 15 px (`--r-option-row`) |
| Segmented toggle | 12 px outer, 9 px for the sliding thumb |
| Buttons, chips | 10 px (`--r-button`) |
| Checkbox | 4 px |
| Cards | 14 px (`--r-lg`) |

## Elevation

White surfaces lift with a soft shadow plus a 1 px hairline, never a heavy border.

- `--shadow-1`: cards (hairline only)
- `--shadow-2`: toolbar and option row
- `--shadow-3`: pop-ups

## Motion

- Easing `--spring` = `cubic-bezier(.3,0,.2,1)`: quick, **no overshoot, nothing bounces**.
- Durations: 140 ms (hover, press), 260 ms (rows, panels), 420 ms (rare, larger moves).
- Things only move when they mean something: the active-tool dot glides between tools, the segmented thumb slides to the new choice, the toolbar glides when it snaps to an edge.
- Tool icons each have one small moment when the tool turns on (see [behaviour.md](behaviour.md#motion)). The panel's tab icons do the same when you switch tabs.
- Respect *reduce motion*: skip the animations.

## Icons

- In [`icons/`](../icons/): 24 × 24 grid, round caps and joins, `currentColor`.
- Stroke 1.8 px; a soft fill (`currentColor` at 18 %) on the main shape gives them body.
- Shown at **20 px** in the toolbar and **15 px** in option rows.
- The toolbar set: Select, Pen (pencil with its curve), Eraser, Marks (speech bubble with two lines), Saved views (camera).
- Object / Plane: a sphere and a flat square, each with a line drawn across.
- Panel tabs: Sketch (the pencil), Layers (two stacked sheets), Agent (the **Thinking loop**: a dot circled by one hand-drawn crayon loop with a faint second pass, always in the accent colour; it runs while the agent thinks). The plain sparkle is kept for the *Sketch to 3D* skill.

## Components

**Toolbar (dock).** Five tools, a grip on the leading end, a minimize chevron on the trailing end. Icons only; the label shows as a tooltip. The active tool is marked by a 4 px accent dot under its icon (beside it when docked on a side).

**Option row.** Sits under (or beside) the toolbar with an 8 px gap. Holds the active tool's options and, at most, one short line of help text. There are **no black hint bars** at the bottom of the viewport.

**Segmented toggle** (Object / Plane). A sliding soft-grey thumb under the chosen option; it slides from the previous choice and never replays.

**Buttons and chips.** 30 px tall, `--well` fill, `--press` on hover, 10 px radius. Saved-view chips show a × on hover to delete.

**Switch** (Crayon on / off, in the panel header). 32 × 18 px with **filleted corners, never a pill**, and a square-ish white knob: accent fill when on, `--line-2` when off. It is the only switch; settings everywhere else use the checkbox below.

**Checkbox** (Snap shapes). A 16 px box with a **hand-drawn tick** in the accent colour that overflows the box. It is drawn on when checked and rubbed out when unchecked.

**Colour swatch.** One swatch (black by default) inside a rainbow ring. The ring opens the colour picker and turns slowly on hover.

**Comment pin.** A numbered accent dot with a white label holding the comment text. On hover, a small dark tag above it names what it is attached to: *Sketch · ‹name›* (click to rename) or *Layer · ‹layer›*.

**Hover hint.** A small dark chip beside the pointer, e.g. while placing a comment: *Sketch · Window idea*, *Layer · Massing*.

**Sync indicator.** The *Named Views* label with two arrows that turn once whenever the saved views change.

## Logo

- The mark: a crayon (shaded wax tip, ink wrapper with two paper edges) **drawing a brush C** whose tail breaks into three dry strands. Files in [`brand/`](../brand/).
- The wordmark is **"crayon", lowercase, in Instrument Sans 600**.
- Show it as **the mark plus the wordmark, as is**. **Never inside an app-icon tile** (no iOS-style rounded square).
- From **24 px down**, use the **C on its own** (`crayon-c.svg`): the crayon is too small to read. This is the Rhino panel tab icon at 16 px.
- Colours: C and tip `#9731b0`, tip shadow `#6d237f`, wrapper `#1d2025`. On dark, the wrapper turns light (`crayon-mark-on-dark.svg`). One-colour and white versions are included.
- The lockup SVGs use live text in Instrument Sans. Outline the text before using them in print.
- In the prototype, the mark is drawn in code (`crayonMark()`) from the same geometry, so it takes its colours from the theme.
