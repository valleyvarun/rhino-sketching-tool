Run `powershell -ExecutionPolicy Bypass -File tests/Geometry/run.ps1` from the repository root. Pass `-RhinoCommonPath` if the Rhino 8 reference assembly is not in the default NuGet cache. The tests compile the actual screen-space shape recognizer and run without launching Rhino or accessing any document.

The checks cover noisy straight lines, ellipses in both drawing directions, rectangle closure/corners, and rejection of freehand waves, tiny gestures and coincident points.

Native Rhino acceptance checks (require the rebuilt plugin in a test document):

1. Capture view A, pick it in **Over view**, and draw. Orbit and verify that the sketch stays on its original plane in the model. Restore A and verify that it lines up with the original pen path.
2. Capture B from a different camera, draw on B, and frame both sketches in another viewport. Each should stay on its own plane. Select and erase each sketch at its projected position.
3. Capture C at the same camera as A. Check that A and C can be picked separately. Rename or delete C and verify its sketches keep their world positions.
4. Change viewport aspect ratio, pan, zoom, and change the lens. The sketch should project like other 3D geometry and retain its world coordinates.
5. Open an older 3dm with sketches drawn over a saved view. Check them from other angles and verify their original model geometry survives save and reopen.
6. Partially erase a colored view sketch from a different viewport. Undo/redo and save/reopen should preserve its geometry, color, brush data and source-view metadata.
