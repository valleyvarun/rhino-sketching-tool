# Notes for coding agents

This repo is **Crayon**, a sketch-to-3D plug-in for Rhino 8. The interface design lives in [`design/`](design/README.md); the working prototype, `design/prototype/index.html`, is the source of truth for the UI.

## Before you implement the interface

The design has open questions that only Varun can answer. **Before building any part of the interface from `design/`, ask Varun every question listed under "Open questions" in [`design/README.md`](design/README.md#open-questions)**, one at a time, and wait for an answer. Build what Varun decides, then write the answer into that section so it isn't asked again.

## Building the interface

Build from [`design/specs/README.md`](design/specs/README.md), [`design/specs/components.json`](design/specs/components.json) and [`design/tokens/tokens.json`](design/tokens/tokens.json) (tokens.css has the same names). Behaviour is in [`design/docs/behaviour.md`](design/docs/behaviour.md); the reasons behind each choice are in [`design/docs/decisions.md`](design/docs/decisions.md). Use the tokens, never new values. Crayon adds **no keyboard shortcuts** (they clash with Rhino's command line).

## Words

In everything the user sees, a pen stroke is a **sketch** (new ones are *Sketch 1*, *Sketch 2*…), never *ink*. Comments are *comments*, not *marks*.
