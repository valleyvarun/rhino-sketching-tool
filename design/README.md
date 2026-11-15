# Crayon: design

Design files for **Crayon**, the sketch-to-3D plug-in for Rhino 8. Everything you need to build the interface is in this folder: a working prototype, the design tokens, the logo, the icons and the specs.

Start with **[index.html](index.html)**: it links everything and shows the colours, logo and icons on one page.

- **Try it:** [`prototype/index.html`](prototype/index.html), or [`prototype/index.html?frame=rhino`](prototype/index.html?frame=rhino) to see it inside a Rhino 8-style window.
- **Build it:** [`specs/README.md`](specs/README.md) (reading order, Rhino hosts, build order) with [`specs/components.json`](specs/components.json) and [`tokens/tokens.json`](tokens/tokens.json). You shouldn't need to read the prototype's HTML.
- **Why it is the way it is:** [`docs/decisions.md`](docs/decisions.md).

## Folder

```
design/
├── index.html            Start page: links, colours, logo, icons
├── prototype/
│   └── index.html        The working UI prototype. Source of truth for everything below.
│                         ?frame=rhino shows it inside a Rhino 8-style window
├── specs/
│   ├── README.md         How to build it: reading order, Rhino hosts, build order, pitfalls
│   └── components.json   Every component: sizes, tokens, states, motion, Rhino host
├── tokens/
│   ├── tokens.json       Design tokens (W3C Design Tokens format)
│   └── tokens.css        The same tokens as CSS variables (same names as the prototype)
├── brand/
│   ├── crayon-*.svg      Mark, small mark (C only) and lockup, in each colourway
│   └── logo-sheet.html   The logo at every size, with colour and font switches
├── icons/                Toolbar, option-row and panel-tab icons (24 × 24, currentColor)
├── docs/
│   ├── behaviour.md      How every tool, panel tab and Rhino command behaves
│   ├── design-system.md  Colour, type, shape, motion, components, logo rules
│   └── decisions.md      What we decided and why
├── tools/
│   └── export.mjs        Regenerates tokens, icons and logo files from the prototype
└── explorations/         Earlier rounds (names, icons, agent icons, section headers, themes, logos,
                          an alternative panel), for reference only
```

## Open questions

These are for Varun to decide **before building the interface**. The prototype shows the current design in the meantime. (Coding agents working in this repo are told to ask them first: see `AGENTS.md` at the repo root.)

1. **Turn into curves: keep it, move it or drop it?** Today Crayon can replace a hand-drawn sketch with a clean Rhino curve (a line, a circle or a smooth curve) on the Curves layer. It appears in three places: the bar under Layers when sketches are selected, the details drawer on a sketch's row, and the ⋯ menu in Layers (*Turn all sketches into curves*). The designers aren't sure it belongs in the interface. Options:
   - **a.** keep it as it is;
   - **b.** keep it only in the ⋯ menu;
   - **c.** drop it and leave it to the agent (the *Clean up* skill already turns sketches into clean curves).

   *Answer:* (to fill in)

## Run the prototype

Open `prototype/index.html` in Chrome or Edge. It is a single file; it only needs an internet connection for three.js (the 3D view) and Google Fonts.

To serve the whole folder instead (so the start page and its links work too):

```bash
python -m http.server 5173
```

Then open `http://localhost:5173/design/`.

## How the pieces map to Rhino

| In the prototype | In Rhino |
| --- | --- |
| Toolbar + option row (floating over the viewport) | A borderless, transparent WebView2 window that follows the viewport |
| Side panel (Sketch · Layers · Agent) | A normal dockable Rhino panel (like Gecko) hosting our HTML |
| Sketches, comment pins, the plane preview, the snap dot | Drawn in the viewport with a `DisplayConduit` |
| Saved views | Rhino Named Views (`_-NamedView`) |
| Layer names shown on hover | The object's layer full path from Rhino |

The 3D scene, model and grid colours in the prototype are a stand-in for Rhino's own viewport.

## Keeping files in sync

The prototype is the source of truth. After changing it, regenerate the tokens, icons and logo files:

```bash
node design/tools/export.mjs
```

## Status (15 November 2026)

**Ready to build.** Toolbar and option rows, Pen, Eraser, comments, Saved views, the side panel (Sketch with presets and the 3D cursor, Layers, Agent), the Crayon switch, light / dark, the logo, colour and icons. One open question remains (above).

The prototype's agent answers with a script; connect the real one through `window.Crayon.send` and `window.Crayon.models` (see [behaviour.md](docs/behaviour.md)).
