# Checks for the Mac version

## In Rhino for Mac

Install Crayon, restart Rhino, and open a new blank model. Run `RunPythonScript`
and choose [Rhino/smoke.py](Rhino/smoke.py). It checks that Rhino loaded the Mac
assembly, opens the Crayon panel, clicks through its three tabs via WebKit, and checks
that the viewport redraw created a toolbar bitmap. Read the PASS lines in command
history. Draw a stroke and try the hardware checks in the [Mac guide](../README.md#verification)
to verify the visible output and tablet input. This script requires a native Mac run.

## Assistant protocols

The shared fixture exercises both Mac provider adapters with local mock CLI
processes on macOS or Linux. Run it with the Unix command in the
[chat protocol guide](../../tests/chat-protocol/README.md).

## Drawing

```powershell
.\mac\tests\Drawing\run.ps1
```

Needs Rhino 8 installed (for RhinoCommon and Eto; pass `-RhinoSystem` if it is not in
Program Files) and the .NET 8 SDK.

On Windows the toolbar over the viewport, its option row and the comment labels are
painted with WPF. A Mac has no WPF, so there the same painting code runs on
`rhino-plugin/Mac/MacDrawing.cs`, which makes the part of WPF's drawing it uses out of
Eto. This test builds the plug-in's sources both ways into two small programs, has each
draw the interface with the plug-in's own (private) painting methods, and compares the
pictures. Eto draws through its WPF platform here and through its Mac platform on a Mac,
so what this proves is `MacDrawing.cs` itself: the icons' paths and arcs, rounded boxes,
transforms, clipping, fades, opacity, and where text is put.

- `bin/out/wpf`, `bin/out/eto`: the pictures. `bin/out/compare`: each pair side by side,
  with their difference in red.
- The toolbar and the icons hold no text and must match; the script fails if they do not.
- Pictures with text differ, and are only reported: the Mac build sets text in the system
  font (regular or bold) rather than Segoe UI's four weights. The colour picker's square
  differs as well: its fade to black is exact in the Mac build.
- The Mac build has two ways of handing a picture to Rhino (as a PNG, or pixel by pixel as
  a fallback). Both are run and must give identical pictures.

The option row is laid out by the test the way `Toolbar.OptionsFrame` does it in a wide
viewport, since that method needs a Rhino view to ask; the painting of it is the
plug-in's. The list of saved views and the comment labels need a Rhino document or
display, and are not drawn here (a stand-in for a label is, in `pieces`).

**Strokes.** On a Mac a stroke is painted with round points, "discs", because Rhino for
Mac draws no sprites (see `mac/README.md`). The Mac program has the plug-in build strokes
into discs (`StrokeDabs.Build`, which needs no Rhino), and a stand-in for Rhino's display
lays each disc down: that ink, that wide, over what is there. It then measures, and fails
if any of this does not hold:

- a stroke 8 pixels wide or more has the requested centre opacity within 0.05;
- a soft brush has a lighter edge than a hard brush at the same centre opacity;
- seen from further off, down to a fifth of a pixel wide, a stroke leaves ink in
  proportion to its width: it neither vanishes nor hardens into a solid line;
- a stroke drawn with varying pressure takes no more than 150 kinds of disc (each is one
  call to Rhino per frame; they take a dozen to a few dozen).

`strokes.png` is a page of strokes to look at, at the size they were drawn and from
further off. The stand-in paints discs with clean edges and no limits. What Rhino for Mac
does with the points it is handed (their size on a Retina display, how faint a point it
draws, how big a point may be) is what this cannot show.

## Installer

From the repository root, run the test against the same file the website serves.

```bash
bash mac/tests/verify-package.sh mac/dist/CrayonSetup.pkg
```

On macOS this opens the package with `pkgutil` and queries Apple Installer for its package information. Git Bash on Windows uses the system's `bsdtar` to read the XAR and extract its scripts. Linux requires `bsdtar`. The test runs the extracted setup scripts against a temporary home directory, a stand-in for Rhino's Yak, and simulated publisher downloads. Checks cover first installation, activation over the higher-numbered Mac preview, reinstalls, rollback, user identity, missing dependencies, and Intel and Apple silicon Macs.

Use the repository's **Mac installer** workflow to run this check on a GitHub-hosted Mac. To verify the interactive wizard and plug-in together, open the downloaded package on a Mac with Rhino installed, finish the wizard, and run the Rhino smoke test above.
