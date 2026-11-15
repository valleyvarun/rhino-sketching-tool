using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Color = System.Drawing.Color;
using ScreenPoint = System.Drawing.Point;

namespace RhinoSketchingTool
{
    public enum SketchTool { Draw, Erase, Select }
    public enum DrawMethod { Surface, Plane, Cursor }
    public enum EraseMode { Stroke, Part }

    /// <summary>
    /// The sketching tools. Left-drag in a viewport draws a freehand stroke or erases,
    /// depending on the active tool; the right button is never touched, so Rhino's own
    /// navigation keeps working.
    ///
    /// Where a stroke lands in 3D depends on the drawing method:
    ///   Surface  on whatever surface, polysurface, mesh or SubD is under the pointer
    ///   Plane    on the viewport's construction plane
    ///   Cursor   on a plane through the 3D cursor, facing the camera
    ///
    /// Shift + Ctrl + click on a surface moves the construction plane onto it (on empty
    /// space it puts the plane back), and Shift + click moves the 3D cursor.
    ///
    /// Strokes are ordinary polyline curves on the "Sketch" layer, tagged with a user
    /// string so the eraser only ever touches curves this tool drew.
    /// </summary>
    public sealed class SketchController
    {
        public const string LayerName = "Sketch";
        public const string StrokeKey = "SketchingTool.Stroke";
        public static readonly Color Ink = Color.FromArgb(36, 87, 214);

        const double MinPointSpacing = 2; // pixels the pointer must move before a point is added
        const double EraseRadius = 10;    // pixels around the pointer that the eraser reaches
        const double EraseStep = 3;       // pixels between points after resampling a touched stroke
        const double GrazingLimit = 0.02; // ignore planes seen this close to edge-on (near the horizon)

        readonly SketchMouse mouse;
        readonly SketchConduit conduit;
        readonly Dictionary<Guid, SavedView> savedViews = new Dictionary<Guid, SavedView>();

        SketchTool tool = SketchTool.Draw;
        DrawMethod method = DrawMethod.Plane;
        EraseMode erase = EraseMode.Stroke;
        Point3d cursor = Point3d.Origin;
        bool active;

        DrawDrag drawing;  // set while a draw drag is in progress
        EraseDrag erasing; // set while an erase drag is in progress
        uint undoRecord;
        RhinoDoc undoDoc;

        public SketchController()
        {
            mouse = new SketchMouse(this);
            conduit = new SketchConduit(this);
        }

        /// <summary>Raised when the tool, method, erase mode or active state changes.</summary>
        public event EventHandler Changed;

        /// <summary>True while the tool window is open. Nothing is intercepted otherwise.</summary>
        public bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                EndDrag();
                active = value;
                mouse.Enabled = value;
                conduit.Enabled = value;
                if (value) KeyHook.Install(SetView);
                else KeyHook.Remove();
                Refresh();
            }
        }

        public SketchTool Tool
        {
            get => tool;
            set { if (tool != value) { EndDrag(); tool = value; Refresh(); } }
        }

        public DrawMethod Method
        {
            get => method;
            set { if (method != value) { EndDrag(); method = value; Refresh(); } }
        }

        public EraseMode Erase
        {
            get => erase;
            set { if (erase != value) { EndDrag(); erase = value; Refresh(); } }
        }

        public Point3d Cursor => cursor;

        /// <summary>The 3D cursor is only drawn while it is the thing being drawn on.</summary>
        public bool CursorVisible => active && tool == SketchTool.Draw && method == DrawMethod.Cursor;

        /// <summary>Points of the stroke being drawn right now, for the live preview.</summary>
        public IReadOnlyList<Point3d> LivePoints => drawing?.Points;

        public bool Dragging => drawing != null || erasing != null;

        void Refresh()
        {
            RhinoDoc.ActiveDoc?.Views.Redraw();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------- pointer

        /// <summary>Left button pressed in a viewport. Returns true if the tool used the click.</summary>
        public bool PointerDown(RhinoView view, ScreenPoint point, bool shift, bool ctrl)
        {
            if (!active || tool == SketchTool.Select || view == null) return false;
            EndDrag();

            if (shift && ctrl)
            {
                PickDrawPlane(view, point);
                return true;
            }
            if (tool == SketchTool.Erase)
            {
                erasing = new EraseDrag { View = view, Strokes = CollectStrokes(view.Document) };
                EraseAt(point);
                return true;
            }
            if (method == DrawMethod.Cursor && shift)
            {
                MoveCursor(view, point);
                return true;
            }

            var viewport = view.ActiveViewport;
            drawing = new DrawDrag
            {
                View = view,
                Plane = method == DrawMethod.Cursor
                    ? new Plane(cursor, viewport.CameraDirection)
                    : viewport.ConstructionPlane(),
            };
            ExtendStroke(point);
            return true;
        }

        public void PointerMove(RhinoView view, ScreenPoint point)
        {
            var dragView = drawing?.View ?? erasing?.View;
            if (dragView == null) return;
            if (view == null || view.RuntimeSerialNumber != dragView.RuntimeSerialNumber)
            {
                EndDrag(); // wandered into another viewport
                return;
            }
            if (drawing != null) ExtendStroke(point);
            else EraseAt(point);
        }

        public void PointerUp() => EndDrag();

        void EndDrag()
        {
            if (drawing != null) FinishRun();
            var view = drawing?.View ?? erasing?.View;
            drawing = null;
            erasing = null;
            if (undoRecord != 0)
            {
                undoDoc.EndUndoRecord(undoRecord);
                undoRecord = 0;
                undoDoc = null;
            }
            view?.Document?.Views.Redraw();
        }

        // Everything one drag changes is a single step for Rhino's Undo.
        void BeginChange(RhinoDoc doc)
        {
            if (undoRecord != 0) return;
            undoRecord = doc.BeginUndoRecord("Sketch");
            undoDoc = doc;
        }

        // ------------------------------------------------------------- drawing

        void ExtendStroke(ScreenPoint point)
        {
            var viewport = drawing.View.ActiveViewport;
            Hit hit = null;
            if (ViewLine(viewport, point, out Line line))
            {
                if (method == DrawMethod.Surface)
                {
                    if (drawing.Targets == null) drawing.Targets = CollectTargets(drawing.View.Document);
                    hit = HitSurface(viewport, line, drawing.Targets);
                }
                else
                {
                    hit = HitPlane(viewport, line, drawing.Plane);
                }
            }

            // Running off a surface, or onto a different object, ends the current stroke;
            // the next hit starts a new one.
            if (hit == null || hit.ObjectId != drawing.ObjectId) FinishRun();
            if (hit == null) return;

            if (drawing.Points.Count == 0)
            {
                drawing.ObjectId = hit.ObjectId;
            }
            else
            {
                // Skip pointer jitter: only add a point after moving a couple of pixels.
                double dx = point.X - drawing.Last.X, dy = point.Y - drawing.Last.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < MinPointSpacing) return;
            }
            drawing.Points.Add(hit.Point);
            drawing.Last = point;
            drawing.View.Redraw();
        }

        // Turn the points gathered so far into a curve in the document.
        void FinishRun()
        {
            if (drawing.Points.Count >= 2)
            {
                var doc = drawing.View.Document;
                BeginChange(doc);
                doc.Objects.AddCurve(new PolylineCurve(drawing.Points), StrokeAttributes(doc));
            }
            drawing.Points.Clear();
            drawing.ObjectId = Guid.Empty;
        }

        static ObjectAttributes StrokeAttributes(RhinoDoc doc)
        {
            int layer = doc.Layers.FindByFullPath(LayerName, -1);
            if (layer < 0) layer = doc.Layers.Add(LayerName, Ink);
            var attributes = new ObjectAttributes();
            if (layer >= 0) attributes.LayerIndex = layer;
            attributes.SetUserString(StrokeKey, "1");
            return attributes;
        }

        // ------------------------------------------------------------- erasing

        // Erasing works on screen: anything within a few pixels of the pointer is hit,
        // whatever plane or surface it was drawn on.
        //   Stroke  remove every stroke the pointer touches, whole
        //   Part    rub out only the bits under the pointer, splitting strokes
        void EraseAt(ScreenPoint point)
        {
            var doc = erasing.View.Document;
            var viewport = erasing.View.ActiveViewport;
            var pointer = new Point2d(point.X, point.Y);
            bool changed = false;

            for (int i = erasing.Strokes.Count - 1; i >= 0; i--)
            {
                var stroke = erasing.Strokes[i];
                var screen = new Point2d?[stroke.Points.Length];
                for (int k = 0; k < screen.Length; k++) screen[k] = ToScreen(viewport, stroke.Points[k]);
                if (!Touches(screen, pointer)) continue;

                BeginChange(doc);
                doc.Objects.Delete(stroke.Id, true);
                erasing.Strokes.RemoveAt(i);
                changed = true;
                if (erase == EraseMode.Stroke) continue;

                // Resample so long straight segments have points to cut between, then
                // keep the runs of points that lie outside the eraser.
                var dense = new List<Point3d> { stroke.Points[0] };
                for (int k = 1; k < stroke.Points.Length; k++)
                {
                    Point3d a = stroke.Points[k - 1], b = stroke.Points[k];
                    double pixels = screen[k - 1].HasValue && screen[k].HasValue
                        ? screen[k - 1].Value.DistanceTo(screen[k].Value)
                        : 0;
                    int steps = Math.Max(1, (int)Math.Ceiling(pixels / EraseStep));
                    for (int s = 1; s <= steps; s++) dense.Add(a + (b - a) * ((double)s / steps));
                }

                var run = new List<Point3d>();
                foreach (var p in dense)
                {
                    var s = ToScreen(viewport, p);
                    if (s.HasValue && s.Value.DistanceTo(pointer) < EraseRadius) KeepRun(doc, stroke, run);
                    else run.Add(p);
                }
                KeepRun(doc, stroke, run);
            }

            if (changed) doc.Views.Redraw();
        }

        // What is left of a partly erased stroke goes back as a new, shorter stroke.
        void KeepRun(RhinoDoc doc, Stroke source, List<Point3d> run)
        {
            if (run.Count >= 2)
            {
                var points = run.ToArray();
                var attributes = source.Attributes.Duplicate();
                attributes.ObjectId = Guid.Empty; // a new object: it must not take over the erased stroke's id
                Guid id = doc.Objects.AddCurve(new PolylineCurve(points), attributes);
                if (id != Guid.Empty) erasing.Strokes.Add(new Stroke { Id = id, Points = points, Attributes = attributes });
            }
            run.Clear();
        }

        static bool Touches(Point2d?[] screen, Point2d pointer)
        {
            for (int i = 0; i + 1 < screen.Length; i++)
            {
                if (!screen[i].HasValue || !screen[i + 1].HasValue) continue;
                Point2d a = screen[i].Value, b = screen[i + 1].Value;
                Vector2d ab = b - a, ap = pointer - a;
                double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
                double t = lengthSquared > 0 ? (ap.X * ab.X + ap.Y * ab.Y) / lengthSquared : 0;
                t = Math.Max(0, Math.Min(1, t));
                if ((a + ab * t).DistanceTo(pointer) < EraseRadius) return true;
            }
            return false;
        }

        static Point2d? ToScreen(RhinoViewport viewport, Point3d point)
        {
            // Points behind a perspective camera have no meaningful screen position.
            if (viewport.IsPerspectiveProjection &&
                (point - viewport.CameraLocation) * viewport.CameraDirection <= 0) return null;
            return viewport.WorldToClient(point);
        }

        static List<Stroke> CollectStrokes(RhinoDoc doc)
        {
            var strokes = new List<Stroke>();
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Curve,
                NormalObjects = true,
                LockedObjects = false,
                HiddenObjects = false,
                DeletedObjects = false,
                VisibleFilter = true,
            };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (obj.Attributes.GetUserString(StrokeKey) == null) continue;
                if (!(obj.Geometry is Curve curve)) continue;

                // Strokes are polylines; one that has since been edited into something
                // smoother is approximated by a polyline before it is erased.
                if (!curve.TryGetPolyline(out Polyline polyline))
                {
                    var approximation = curve.ToPolyline(doc.ModelAbsoluteTolerance, 0.05, 0, 0);
                    if (approximation == null || !approximation.TryGetPolyline(out polyline)) continue;
                }
                if (polyline.Count < 2) continue;
                strokes.Add(new Stroke { Id = obj.Id, Points = polyline.ToArray(), Attributes = obj.Attributes });
            }
            return strokes;
        }

        // ------------------------------------------- drawing plane and 3D cursor

        // Shift + Ctrl + click: put the viewport's construction plane on the surface under
        // the pointer (tangent at the clicked point), or back to its default on empty space.
        void PickDrawPlane(RhinoView view, ScreenPoint point)
        {
            var viewport = view.ActiveViewport;
            Hit hit = null;
            if (ViewLine(viewport, point, out Line line))
                hit = HitSurface(viewport, line, CollectTargets(view.Document));

            if (hit != null)
            {
                Vector3d normal = hit.Normal;
                if (normal * viewport.CameraDirection > 0) normal = -normal; // face the viewer
                // Keep the plane's X axis horizontal where that is possible.
                Vector3d x = Vector3d.CrossProduct(Vector3d.ZAxis, normal);
                if (x.Length < 1e-6) x = Vector3d.XAxis;
                x.Unitize();
                SetConstructionPlane(viewport, new Plane(hit.Point, x, Vector3d.CrossProduct(normal, x)));
            }
            else
            {
                SetConstructionPlane(viewport, DefaultPlane(viewport));
            }

            tool = SketchTool.Draw;
            method = DrawMethod.Plane;
            Refresh();
        }

        static void SetConstructionPlane(RhinoViewport viewport, Plane plane)
        {
            // Pushing keeps the old plane on the viewport's stack, so Rhino's own
            // "CPlane Undo" steps back through these changes.
            var cplane = viewport.GetConstructionPlane();
            cplane.Plane = plane;
            viewport.PushConstructionPlane(cplane);
        }

        // The plane Rhino gives a viewport to begin with: world XY, or the plane facing
        // the camera in the standard parallel views (Front, Right, ...).
        static Plane DefaultPlane(RhinoViewport viewport)
        {
            Vector3d direction = viewport.CameraDirection;
            direction.Unitize();
            bool axisAligned = Math.Abs(direction.X) > 0.9999 || Math.Abs(direction.Y) > 0.9999;
            if (viewport.IsParallelProjection && axisAligned)
                return new Plane(Point3d.Origin, viewport.CameraX, viewport.CameraY);
            return Plane.WorldXY;
        }

        // Shift + click (3D cursor method): move the cursor to the surface under the
        // pointer, or to the construction plane where there is none.
        void MoveCursor(RhinoView view, ScreenPoint point)
        {
            var viewport = view.ActiveViewport;
            if (!ViewLine(viewport, point, out Line line)) return;
            Hit hit = HitSurface(viewport, line, CollectTargets(view.Document))
                      ?? HitPlane(viewport, line, viewport.ConstructionPlane());
            if (hit == null) return;
            cursor = hit.Point;
            view.Document.Views.Redraw();
        }

        // ------------------------------------------------------------- views

        /// <summary>
        /// Keys 1 / 2 / 3: switch the active viewport to a parallel Right / Front / Top view
        /// centred on the 3D cursor, with the construction plane through the cursor and
        /// facing the camera, ready to draw on. 0: back to the perspective view as it was.
        /// </summary>
        public void SetView(int key)
        {
            var doc = RhinoDoc.ActiveDoc;
            var view = doc?.Views.ActiveView;
            if (view == null || key < 0 || key > 3) return;
            EndDrag();

            var viewport = view.ActiveViewport;
            if (key == 0)
            {
                RestorePerspective(viewport);
            }
            else
            {
                ShowAxisView(doc, viewport, key);
                tool = SketchTool.Draw;
                method = DrawMethod.Plane;
            }
            Refresh();
        }

        void ShowAxisView(RhinoDoc doc, RhinoViewport viewport, int key)
        {
            // from: where the camera sits relative to the cursor. x / y: the construction plane.
            Vector3d from, up, x, y;
            string name;
            switch (key)
            {
                case 1: name = "Right"; from = Vector3d.XAxis; up = Vector3d.ZAxis; x = Vector3d.YAxis; y = Vector3d.ZAxis; break;
                case 2: name = "Front"; from = -Vector3d.YAxis; up = Vector3d.ZAxis; x = Vector3d.XAxis; y = Vector3d.ZAxis; break;
                default: name = "Top"; from = Vector3d.ZAxis; up = Vector3d.YAxis; x = Vector3d.XAxis; y = Vector3d.YAxis; break;
            }

            var info = new ViewportInfo(viewport);
            if (viewport.IsPerspectiveProjection)
            {
                savedViews[viewport.Id] = new SavedView
                {
                    Info = new ViewportInfo(viewport),
                    Target = viewport.CameraTarget,
                    Name = viewport.Name,
                };

                // Size the parallel view so things at the cursor's depth keep the size
                // they had in perspective.
                info.GetFrustum(out double left, out double right, out double bottom, out double top, out double near, out double far);
                Vector3d forward = viewport.CameraDirection; // not unit length: it reaches to the camera target
                forward.Unitize();
                double depth = (cursor - viewport.CameraLocation) * forward;
                if (depth <= near) depth = (viewport.CameraTarget - viewport.CameraLocation) * forward;
                if (depth <= near) depth = near;
                double halfWidth = (right - left) / 2 * depth / near;
                double halfHeight = (top - bottom) / 2 * depth / near;
                info.ChangeToParallelProjection(true);
                info.GetFrustum(out left, out right, out bottom, out top, out near, out far);
                info.SetFrustum(-halfWidth, halfWidth, -halfHeight, halfHeight, near, far);
            }

            // Stand far enough back that the whole model is in front of the camera.
            double distance = viewport.CameraLocation.DistanceTo(cursor);
            BoundingBox everything = doc.Objects.BoundingBox;
            if (everything.IsValid) distance = Math.Max(distance, everything.Diagonal.Length * 2);
            if (distance < 1) distance = 1;

            info.SetCameraLocation(cursor + from * distance);
            info.SetCameraDirection(-from);
            info.SetCameraUp(up);
            info.TargetPoint = cursor;
            viewport.SetViewProjection(info, true);
            viewport.SetCameraTarget(cursor, false);
            viewport.Name = name;
            SetConstructionPlane(viewport, new Plane(cursor, x, y));
        }

        void RestorePerspective(RhinoViewport viewport)
        {
            if (savedViews.TryGetValue(viewport.Id, out SavedView saved))
            {
                savedViews.Remove(viewport.Id);
                // The viewport may have been resized since the view was saved.
                var size = viewport.Size;
                if (size.Width > 0 && size.Height > 0) saved.Info.FrustumAspect = (double)size.Width / size.Height;
                viewport.SetViewProjection(saved.Info, true);
                viewport.SetCameraTarget(saved.Target, false);
                viewport.Name = saved.Name;
            }
            else if (viewport.IsParallelProjection)
            {
                viewport.SetProjection(DefinedViewportProjection.Perspective, "Perspective", false);
            }
        }

        // ------------------------------------------------------- hit testing

        // The line through the scene that sits under a screen point, pointing away from the camera.
        static bool ViewLine(RhinoViewport viewport, ScreenPoint point, out Line line)
        {
            line = viewport.ClientToWorld(point);
            if (!line.IsValid || line.Length <= 0) return false;
            if ((line.To - line.From) * viewport.CameraDirection < 0) line.Flip();
            return true;
        }

        static Hit HitPlane(RhinoViewport viewport, Line line, Plane plane)
        {
            Vector3d direction = line.Direction;
            direction.Unitize();
            if (Math.Abs(direction * plane.Normal) < GrazingLimit) return null;
            if (!Intersection.LinePlane(line, plane, out double t)) return null;
            Point3d point = line.PointAt(t);
            if (viewport.IsPerspectiveProjection &&
                (point - viewport.CameraLocation) * viewport.CameraDirection <= 0) return null; // behind the camera
            return new Hit { Point = point, Normal = plane.Normal };
        }

        // Nearest point along the line on any visible surface-like object. The line is shot
        // at the objects' display meshes (fast, and it is what you see on screen), then the
        // result is pulled onto the true surface.
        static Hit HitSurface(RhinoViewport viewport, Line line, List<Target> targets)
        {
            var ray = new Ray3d(line.From, line.Direction);
            Target nearest = null;
            double nearestT = double.MaxValue;

            foreach (var target in targets)
            {
                if (!Intersection.LineBox(line, target.Box, 0, out Interval _)) continue;
                target.Prepare();
                if (target.Meshes == null) continue;
                foreach (var mesh in target.Meshes)
                {
                    if (mesh == null) continue;
                    double t = Intersection.MeshRay(mesh, ray);
                    if (t >= 0 && t < nearestT)
                    {
                        nearestT = t;
                        nearest = target;
                    }
                }
            }
            if (nearest == null) return null;

            var hit = new Hit { Point = ray.PointAt(nearestT), ObjectId = nearest.Id, Normal = Vector3d.Unset };
            if (nearest.Brep != null)
            {
                double reach = Math.Max(nearest.Box.Diagonal.Length * 0.05, 1e-6);
                if (nearest.Brep.ClosestPoint(hit.Point, out Point3d onSurface, out ComponentIndex _, out double _, out double _, reach, out Vector3d normal))
                {
                    hit.Point = onSurface;
                    hit.Normal = normal;
                }
            }
            if (!hit.Normal.IsValid || hit.Normal.IsTiny())
            {
                foreach (var mesh in nearest.Meshes)
                {
                    if (mesh == null) continue;
                    if (mesh.ClosestPoint(hit.Point, out Point3d _, out Vector3d meshNormal, nearest.Box.Diagonal.Length * 0.01) >= 0)
                    {
                        hit.Normal = meshNormal;
                        break;
                    }
                }
            }
            if (!hit.Normal.IsValid || hit.Normal.IsTiny()) hit.Normal = -viewport.CameraDirection;
            hit.Normal.Unitize();
            return hit;
        }

        static List<Target> CollectTargets(RhinoDoc doc)
        {
            var targets = new List<Target>();
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD,
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                DeletedObjects = false,
                VisibleFilter = true,
            };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                BoundingBox box = obj.Geometry.GetBoundingBox(false);
                if (!box.IsValid) continue;
                box.Inflate(Math.Max(box.Diagonal.Length * 1e-4, doc.ModelAbsoluteTolerance)); // flat objects have no thickness
                targets.Add(new Target { Id = obj.Id, Object = obj, Box = box });
            }
            return targets;
        }

        // ------------------------------------------------------------- data

        sealed class Hit
        {
            public Point3d Point;
            public Vector3d Normal;
            public Guid ObjectId; // Guid.Empty for a plane
        }

        // An object strokes can be drawn on. Its meshes are only fetched once the pointer
        // actually passes over its bounding box.
        sealed class Target
        {
            public Guid Id;
            public RhinoObject Object;
            public BoundingBox Box;
            public Mesh[] Meshes;
            public Brep Brep;
            bool prepared;

            public void Prepare()
            {
                if (prepared) return;
                prepared = true;

                GeometryBase geometry = Object.Geometry;
                if (geometry is Mesh mesh)
                {
                    Meshes = new[] { mesh };
                    return;
                }

                if (geometry is Brep brep) Brep = brep;
                else if (geometry is Surface surface) Brep = surface.ToBrep(); // includes extrusions

                Meshes = Object.GetMeshes(MeshType.Render);
                if (Meshes != null && Meshes.Length > 0) return;

                // Nothing cached (the object has never been shaded): mesh it ourselves.
                if (Brep != null) Meshes = Mesh.CreateFromBrep(Brep, MeshingParameters.FastRenderMesh);
                else if (geometry is SubD subd) Meshes = new[] { Mesh.CreateFromSubD(subd, 3) };
            }
        }

        sealed class DrawDrag
        {
            public RhinoView View;
            public Plane Plane;
            public List<Target> Targets;
            public readonly List<Point3d> Points = new List<Point3d>();
            public Guid ObjectId;
            public ScreenPoint Last;
        }

        sealed class EraseDrag
        {
            public RhinoView View;
            public List<Stroke> Strokes;
        }

        sealed class Stroke
        {
            public Guid Id;
            public Point3d[] Points;
            public ObjectAttributes Attributes;
        }

        sealed class SavedView
        {
            public ViewportInfo Info;
            public Point3d Target;
            public string Name;
        }
    }
}
