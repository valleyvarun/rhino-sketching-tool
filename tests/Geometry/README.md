Run `powershell -ExecutionPolicy Bypass -File tests/Geometry/run.ps1` from the repository root. Pass `-RhinoCommonPath` if the Rhino 8 reference assembly is not in the default NuGet cache. The tests compile the actual screen-space shape recognizer and run without launching Rhino or accessing any document.

The checks cover noisy straight lines, ellipses in both drawing directions, rectangle closure/corners, and rejection of freehand waves, tiny gestures and coincident points.

Native Rhino acceptance checks (require the rebuilt plugin in a test document):

1. Capture view A, draw an overlay, orbit, and confirm the overlay disappears. Restore A through the dropdown and confirm it returns over all model geometry.
2. Capture B from a different camera, draw a second overlay, and alternate A/B. Only each view's own overlays should render and erase. Ordinary object/plane/cursor sketches remain visible in both.
3. Capture C at the exact same camera as A. Confirm the dropdown isolates A and C, save/reopen the 3dm with C selected, and confirm C remains selected. Rename C through Rhino Named Views and check that its sketches remain attached; deleting C should hide its overlays.
4. Resize/dock the sidebar and change viewport aspect ratio. A restored view should keep its overlays. Orbit, pan, zoom or change the lens and the overlay should disappear until its view is restored.
5. In a four-viewport layout, draw a View sketch and confirm its live preview stays in that viewport. Activate Select and verify view overlays never appear as native curves in unrelated viewports.
6. Partially erase a colored view sketch. Undo/redo and save/reopen should preserve its color, brush data and named-view association.
