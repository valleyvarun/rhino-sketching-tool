import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineGeometry } from 'three/addons/lines/LineGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';

// Rhino is Z-up; three.js defaults to Y-up. This has to be set before any
// camera or light is created, because they copy it at construction.
THREE.Object3D.DEFAULT_UP.set(0, 0, 1);

const GRID_SIZE = 20; // units along each side of the construction plane

const canvas = document.getElementById('viewport');

// Transparent canvas: the background gradient comes from style.css.
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
renderer.shadowMap.enabled = true;

const scene = new THREE.Scene();

const perspCamera = new THREE.PerspectiveCamera(40, 1, 0.1, 500);
perspCamera.position.set(16, -18, 12);

// Parallel camera for the axis-aligned views (keys 1 / 2 / 3).
const orthoCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 1000);
let orthoHalfHeight = 10; // world units from the view centre to the top edge at zoom 1

let camera = perspCamera; // whichever camera is currently in use

const controls = new OrbitControls(camera, canvas); // orbits around the origin
controls.enableDamping = true;

// Rhino's default perspective navigation:
//   right-drag          rotate
//   Shift + right-drag  pan
//   Ctrl + right-drag   zoom (drag up to zoom in)
//   wheel               zoom toward the cursor
// The left button is left free for selecting and drawing.
controls.mouseButtons = { LEFT: null, MIDDLE: null, RIGHT: THREE.MOUSE.ROTATE };
controls.zoomToCursor = true;
// OrbitControls already turns Shift + rotate into a pan. Ctrl needs a hand:
// swap the right button to dolly before OrbitControls sees the press, and flip
// the zoom direction for the drag, since OrbitControls zooms in on drag-down.
canvas.addEventListener(
  'pointerdown',
  (event) => {
    const ctrlZoom = event.button === 2 && event.ctrlKey && !event.shiftKey;
    // As in Rhino, plain right-drag pans in the parallel views.
    const plain = camera === orthoCamera ? THREE.MOUSE.PAN : THREE.MOUSE.ROTATE;
    controls.mouseButtons.RIGHT = ctrlZoom ? THREE.MOUSE.DOLLY : plain;
    controls.zoomSpeed = ctrlZoom ? -1 : 1;
  },
  { capture: true }
);
canvas.addEventListener('pointerup', () => (controls.zoomSpeed = 1));
controls.minDistance = 2;
controls.maxDistance = 150;

// ---------------------------------------------------------------- lights

scene.add(new THREE.HemisphereLight(0xffffff, 0xc4c8d0, 1.3));

const sun = new THREE.DirectionalLight(0xffffff, 2);
sun.position.set(-6, -10, 16);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
sun.shadow.normalBias = 0.03;
// The shadow camera is a box around the origin; size it to cover the grid.
const reach = GRID_SIZE * 0.75;
Object.assign(sun.shadow.camera, {
  left: -reach,
  right: reach,
  bottom: -reach,
  top: reach,
  near: 1,
  far: 60,
});
scene.add(sun);

// ------------------------------------------------- construction plane

scene.add(createGrid(GRID_SIZE));

// Invisible floor that only shows the shadows falling on it.
const floor = new THREE.Mesh(
  new THREE.PlaneGeometry(GRID_SIZE, GRID_SIZE),
  new THREE.ShadowMaterial({ opacity: 0.2, depthWrite: false })
);
floor.receiveShadow = true;
scene.add(floor);

// --------------------------------------------------------- sample solids

const surface = new THREE.MeshStandardMaterial({
  color: 0xf4f3ef,
  roughness: 0.7,
  // Nudge faces back a hair so the edge lines drawn on them don't flicker.
  polygonOffset: true,
  polygonOffsetFactor: 1,
  polygonOffsetUnits: 1,
});
const edgeLine = new THREE.LineBasicMaterial({ color: 0x22262d });
const solids = []; // meshes that "draw on surface" can hit

// three.js builds its primitives Y-up, so anything with a "vertical" axis
// gets rotateX(90°) to stand it up along Z.
addSolid(new THREE.BoxGeometry(3, 3, 3), -4.5, 4.5, 1.5);
addSolid(new THREE.CylinderGeometry(1.25, 1.25, 4.5, 64).rotateX(Math.PI / 2), 4.5, 4.5, 2.25);
addSolid(new THREE.SphereGeometry(1.75, 64, 32), -4.5, -4.5, 1.75);
addSolid(new THREE.TorusGeometry(1.6, 0.55, 32, 96), 4.5, -4.5, 0.55);

function addSolid(geometry, x, y, z) {
  const mesh = new THREE.Mesh(geometry, surface);
  mesh.position.set(x, y, z);
  mesh.castShadow = true;
  mesh.receiveShadow = true;
  // Outline the hard edges (faces meeting at more than 30°), CAD-style.
  mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 30), edgeLine));
  scene.add(mesh);
  solids.push(mesh);
  return mesh;
}

// -------------------------------------------------------------- drawing
//
// Left-drag draws a freehand stroke or erases, depending on the active tool.
// Where a stroke lands in 3D depends on the drawing method:
//   surface  on whatever solid is under the pointer
//   plane    on the drawing plane: the construction plane (world XY) unless
//            Shift + Ctrl + click has moved it onto a surface
//   cursor   on a plane through the 3D cursor, facing the camera
//            (Shift + click moves the 3D cursor)

const state = { tool: 'draw', method: 'plane', erase: 'stroke' };
const strokes = []; // { points: Vector3[], line: Line2 }
const ink = new LineMaterial({ color: 0x2457d6, linewidth: 3 }); // width in pixels

const raycaster = new THREE.Raycaster();
const groundPlane = new THREE.Plane(new THREE.Vector3(0, 0, 1), 0);
const SURFACE_LIFT = 0.02; // keeps strokes from sinking into the surface
const MAX_REACH = 300; // ignore plane hits absurdly far away (near the horizon)

// The 3D cursor: a small axis cross that stays the same size on screen.
const cursor3d = new THREE.AxesHelper(1);
cursor3d.material.depthTest = false;
cursor3d.renderOrder = 10;
cursor3d.position.set(0, 0, 2);
cursor3d.visible = false;
scene.add(cursor3d);

function aimRay(event) {
  const rect = canvas.getBoundingClientRect();
  const ndc = new THREE.Vector2(
    ((event.clientX - rect.left) / rect.width) * 2 - 1,
    -((event.clientY - rect.top) / rect.height) * 2 + 1
  );
  raycaster.setFromCamera(ndc, camera);
}

function hitSurface() {
  const hit = raycaster.intersectObjects(solids, false)[0];
  if (!hit) return null;
  const normal = hit.face.normal.clone().transformDirection(hit.object.matrixWorld);
  return { point: hit.point.addScaledVector(normal, SURFACE_LIFT), object: hit.object, normal };
}

// The plane the "plane" method draws on. Starts as the construction plane;
// Shift + Ctrl + click on a surface moves it to that surface (tangent at the
// clicked point), and Shift + Ctrl + click on empty space puts it back.
let drawPlane = groundPlane;

// A translucent square showing where a custom drawing plane is.
const planeMarker = new THREE.Mesh(
  new THREE.PlaneGeometry(4, 4),
  new THREE.MeshBasicMaterial({
    color: 0x2457d6,
    transparent: true,
    opacity: 0.12,
    depthWrite: false,
    side: THREE.DoubleSide,
  })
);
planeMarker.add(
  new THREE.LineSegments(
    new THREE.EdgesGeometry(planeMarker.geometry),
    new THREE.LineBasicMaterial({ color: 0x2457d6 })
  )
);
planeMarker.visible = false;
scene.add(planeMarker);

function pickDrawPlane(event) {
  aimRay(event);
  const hit = hitSurface();
  if (hit) {
    setDrawPlane(hit.normal, hit.point);
  } else {
    drawPlane = groundPlane;
    usePlaneMethod();
  }
}

function setDrawPlane(normal, point) {
  drawPlane = new THREE.Plane().setFromNormalAndCoplanarPoint(normal, point);
  planeMarker.position.copy(point);
  planeMarker.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), normal);
  usePlaneMethod();
}

function usePlaneMethod() {
  state.tool = 'draw';
  state.method = 'plane';
  syncToolbar();
}

// 1 / 2 / 3: drawing plane through the 3D cursor, parallel to YZ / ZX / XY.
const axisPlaneNormals = {
  1: new THREE.Vector3(1, 0, 0),
  2: new THREE.Vector3(0, 1, 0),
  3: new THREE.Vector3(0, 0, 1),
};
// Each of those keys also switches to the matching parallel view, centred on
// the 3D cursor and looking straight at the plane. 0 returns to perspective.
const axisViews = {
  1: { name: 'Right', from: new THREE.Vector3(1, 0, 0) },
  2: { name: 'Front', from: new THREE.Vector3(0, -1, 0) },
  // Nudged off vertical so "up" on screen is +Y, as in Rhino's Top view.
  3: { name: 'Top', from: new THREE.Vector3(0, -1e-4, 1) },
};
const perspTarget = new THREE.Vector3(); // perspective orbit centre, kept while in a parallel view

function setView(key) {
  const view = axisViews[key];
  if (camera === perspCamera) perspTarget.copy(controls.target);

  if (view) {
    const centre = cursor3d.position;
    if (camera === perspCamera) {
      // Match the scale the perspective view had at the cursor.
      const distance = perspCamera.position.distanceTo(centre);
      orthoHalfHeight = distance * Math.tan(THREE.MathUtils.degToRad(perspCamera.fov / 2));
    }
    orthoCamera.zoom = 1;
    orthoCamera.position.copy(centre).addScaledVector(view.from, 200);
    controls.target.copy(centre);
    camera = orthoCamera;
  } else {
    controls.target.copy(perspTarget);
    camera = perspCamera;
  }

  controls.object = camera;
  controls.enableRotate = camera === perspCamera;
  updateProjection();
  controls.update();
  document.getElementById('view-name').textContent = view ? view.name : 'Perspective';
}

window.addEventListener('keydown', (event) => {
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  if (event.key === '0') {
    setView(0);
  } else if (axisPlaneNormals[event.key]) {
    setDrawPlane(axisPlaneNormals[event.key], cursor3d.position);
    setView(event.key);
  }
});

function hitPlane(plane) {
  const point = raycaster.ray.intersectPlane(plane, new THREE.Vector3());
  if (!point || point.distanceTo(camera.position) > MAX_REACH) return null;
  return { point, object: null };
}

function rebuildLine(stroke) {
  if (stroke.points.length < 2) return;
  stroke.line.geometry.dispose();
  stroke.line.geometry = new LineGeometry().setPositions(stroke.points.flatMap((p) => [p.x, p.y, p.z]));
  if (!stroke.line.parent) scene.add(stroke.line);
}

function removeStroke(stroke) {
  scene.remove(stroke.line);
  stroke.line.geometry.dispose();
  strokes.splice(strokes.indexOf(stroke), 1);
}

let drawing = null; // { plane, stroke, object, x, y } while a draw drag is in progress
let erasing = false;

function extendStroke(event) {
  aimRay(event);
  const hit = state.method === 'surface' ? hitSurface() : hitPlane(drawing.plane);

  // Running off a surface, or onto a different solid, ends the current stroke;
  // the next hit starts a new one.
  if (!hit || hit.object !== drawing.object) drawing.stroke = null;
  if (!hit) return;

  if (!drawing.stroke) {
    drawing.stroke = { points: [hit.point], line: new Line2(new LineGeometry(), ink) };
    drawing.object = hit.object;
    strokes.push(drawing.stroke);
  } else {
    // Skip pointer jitter: only add a point after moving a couple of pixels.
    if (Math.hypot(event.clientX - drawing.x, event.clientY - drawing.y) < 2) return;
    drawing.stroke.points.push(hit.point);
    rebuildLine(drawing.stroke);
  }
  drawing.x = event.clientX;
  drawing.y = event.clientY;
}

// Erasing works on screen: anything within a few pixels of the pointer is hit,
// whatever plane or surface it was drawn on. Two modes (state.erase):
//   stroke  remove every stroke the pointer touches, whole
//   part    rub out only the bits under the pointer, splitting strokes
function eraseAt(event) {
  const rect = canvas.getBoundingClientRect();
  const pointer = new THREE.Vector2(event.clientX - rect.left, event.clientY - rect.top);
  const toScreen = (p) => {
    const v = p.clone().project(camera);
    return v.z > 1 ? null : new THREE.Vector2(((v.x + 1) / 2) * rect.width, ((1 - v.y) / 2) * rect.height);
  };
  const RADIUS = 10;
  const STEP = 3; // pixels between points after resampling a touched stroke

  for (const stroke of [...strokes]) {
    const pts = stroke.points.map(toScreen);
    const near = pts.some((a, i) => {
      const b = pts[i + 1];
      if (!a || !b) return false;
      const ab = b.clone().sub(a);
      const t = THREE.MathUtils.clamp(pointer.clone().sub(a).dot(ab) / (ab.lengthSq() || 1), 0, 1);
      return a.clone().addScaledVector(ab, t).distanceTo(pointer) < RADIUS;
    });
    if (!near) continue;
    removeStroke(stroke);
    if (state.erase === 'stroke') continue;

    // Resample so long straight segments have points to cut between, then
    // keep the runs of points that lie outside the eraser.
    const dense = [stroke.points[0]];
    for (let i = 1; i < stroke.points.length; i++) {
      const a = stroke.points[i - 1];
      const b = stroke.points[i];
      const pixels = pts[i - 1] && pts[i] ? pts[i - 1].distanceTo(pts[i]) : 0;
      const steps = Math.max(1, Math.ceil(pixels / STEP));
      for (let s = 1; s <= steps; s++) dense.push(a.clone().lerp(b, s / steps));
    }
    let run = [];
    const endRun = () => {
      if (run.length >= 2) addStroke(run);
      run = [];
    };
    for (const p of dense) {
      const s = toScreen(p);
      if (s && s.distanceTo(pointer) < RADIUS) endRun();
      else run.push(p);
    }
    endRun();
  }
}

function addStroke(points) {
  const stroke = { points, line: new Line2(new LineGeometry(), ink) };
  strokes.push(stroke);
  rebuildLine(stroke);
  return stroke;
}

canvas.addEventListener('pointerdown', (event) => {
  if (event.button !== 0) return;
  canvas.setPointerCapture(event.pointerId);

  if (event.shiftKey && event.ctrlKey) {
    pickDrawPlane(event);
    return;
  }
  if (state.tool === 'erase') {
    erasing = true;
    eraseAt(event);
    return;
  }
  if (state.method === 'cursor' && event.shiftKey) {
    aimRay(event);
    const hit = hitSurface() ?? hitPlane(groundPlane);
    if (hit) cursor3d.position.copy(hit.point);
    return;
  }

  let plane = drawPlane;
  if (state.method === 'cursor') {
    const facing = camera.getWorldDirection(new THREE.Vector3());
    plane = new THREE.Plane().setFromNormalAndCoplanarPoint(facing, cursor3d.position);
  }
  drawing = { plane, stroke: null, object: null, x: 0, y: 0 };
  extendStroke(event);
});

canvas.addEventListener('pointermove', (event) => {
  if (drawing) extendStroke(event);
  else if (erasing) eraseAt(event);
});

function endDrag() {
  // A click without a drag leaves one-point strokes behind; drop them.
  for (const stroke of [...strokes]) if (stroke.points.length < 2) removeStroke(stroke);
  drawing = null;
  erasing = false;
}
canvas.addEventListener('pointerup', endDrag);
canvas.addEventListener('pointercancel', endDrag);

// Toolbar
const hints = {
  'erase-stroke': 'Drag over strokes to delete them whole',
  'erase-part': 'Drag to rub out the parts of strokes under the pointer',
  surface: 'Drag on an object to draw on its surface',
  plane: 'Drag to draw on the drawing plane · Shift + Ctrl + click a surface to move the plane there · 1 / 2 / 3 for Right / Front / Top view at the 3D cursor · 0 for perspective',
  cursor: 'Drag to draw at the 3D cursor · Shift + click to move the cursor',
};
function syncToolbar() {
  for (const button of document.querySelectorAll('.toolbar button')) {
    const on =
      button.dataset.tool === state.tool ||
      button.dataset.method === state.method ||
      button.dataset.erase === state.erase;
    button.setAttribute('aria-pressed', on);
    if (button.dataset.method) button.disabled = state.tool === 'erase';
    if (button.dataset.erase) button.disabled = state.tool === 'draw';
  }
  cursor3d.visible = state.tool === 'draw' && state.method === 'cursor';
  planeMarker.visible = state.tool === 'draw' && state.method === 'plane' && drawPlane !== groundPlane;
  document.getElementById('tool-hint').textContent =
    hints[state.tool === 'erase' ? `erase-${state.erase}` : state.method];
}
document.querySelector('.toolbar').addEventListener('click', (event) => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.dataset.tool) state.tool = button.dataset.tool;
  if (button.dataset.method) state.method = button.dataset.method;
  if (button.dataset.erase) state.erase = button.dataset.erase;
  syncToolbar();
});
syncToolbar();

// A Rhino-style grid on the world XY plane: a line every unit, a darker one
// every `majorEvery` units, the X axis in red and the Y axis in green.
// `size` should be an even whole number.
function createGrid(size, majorEvery = 5) {
  const half = size / 2;
  const minor = new THREE.Color(0xc8ccd3);
  const major = new THREE.Color(0xa4a9b3);
  const xAxis = new THREE.Color(0xd2493a);
  const yAxis = new THREE.Color(0x3c9d5d);

  const positions = [];
  const colors = [];
  const addLine = (x1, y1, x2, y2, color) => {
    positions.push(x1, y1, 0, x2, y2, 0);
    colors.push(color.r, color.g, color.b, color.r, color.g, color.b);
  };
  // One line parallel to X and one parallel to Y, both `offset` from the origin.
  const addPair = (offset, alongX, alongY) => {
    addLine(-half, offset, half, offset, alongX);
    addLine(offset, -half, offset, half, alongY);
  };

  // Later lines paint over earlier ones where they cross, so add them from
  // least to most important: minor, then major, then the axes.
  const offsets = [];
  for (let i = -half; i <= half; i++) offsets.push(i);
  const isMajor = (i) => i % majorEvery === 0;

  offsets.filter((i) => !isMajor(i)).forEach((i) => addPair(i, minor, minor));
  offsets.filter((i) => isMajor(i) && i !== 0).forEach((i) => addPair(i, major, major));
  addPair(0, xAxis, yAxis);

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geometry.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));

  // The grid is a backdrop: it draws before everything else and stays out of
  // the depth buffer, so anything added to the scene paints over it. That
  // keeps geometry lying flat on the plane from flickering against the lines.
  const grid = new THREE.LineSegments(
    geometry,
    new THREE.LineBasicMaterial({ vertexColors: true, depthWrite: false })
  );
  grid.renderOrder = -1;
  return grid;
}

// ------------------------------------------------------------ rendering

// Match the drawing buffer and camera to the canvas's on-screen size.
// Checked every frame, so window resizes and display-scale changes just work.
const bufferSize = new THREE.Vector2();
function fitToCanvas() {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (width === 0 || height === 0) return;

  const pixelRatio = Math.min(window.devicePixelRatio, 2);
  renderer.getSize(bufferSize);
  const upToDate =
    bufferSize.x === width &&
    bufferSize.y === height &&
    renderer.getPixelRatio() === pixelRatio;
  if (upToDate) return;

  renderer.setPixelRatio(pixelRatio);
  renderer.setSize(width, height, false); // false: leave the CSS size alone
  updateProjection();
}

function updateProjection() {
  const aspect = canvas.clientWidth / canvas.clientHeight || 1;
  perspCamera.aspect = aspect;
  perspCamera.updateProjectionMatrix();
  orthoCamera.top = orthoHalfHeight;
  orthoCamera.bottom = -orthoHalfHeight;
  orthoCamera.right = orthoHalfHeight * aspect;
  orthoCamera.left = -orthoHalfHeight * aspect;
  orthoCamera.updateProjectionMatrix();
}

renderer.setAnimationLoop(() => {
  fitToCanvas();
  controls.update();
  cursor3d.scale.setScalar(
    camera === orthoCamera
      ? (orthoHalfHeight / orthoCamera.zoom) * 0.11
      : camera.position.distanceTo(cursor3d.position) * 0.04
  );
  renderer.render(scene, camera);
});
