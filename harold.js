"use strict";

// Harold traces the user's three-loop sketch, always facing right.
(() => {
  const DURATION = 30000;
  const PURPLE = "#a33399";
  const INK = "#806d42";
  const SKIN = "#fffaf0";
  const PAJAMAS = "#fffefa";
  const clamp = (value, lo = 0, hi = 1) => Math.max(lo, Math.min(hi, value));
  const ease = value => { const t = clamp(value); return t * t * (3 - 2 * t); };
  const mix = (a, b, t) => a + (b - a) * t;

  // Right-facing profile redrawn from the supplied film: large round skull,
  // small upturned nose, little ear, three hairs, and a loose footed sleeper.
  // Coordinates are in character units, with his feet at y = 0.
  const artwork = {
    head: new Path2D("M-12-155 C-34-157-51-173-50-194 C-49-217-34-236-12-243 C7-250 27-245 35-232 C41-224 38-216 40-208 L51-209 L49-201 C60-192 62-179 53-169 C45-160 33-156 22-155 Z"),
    ear: new Path2D("M5-201 C-6-208-17-198-12-188 C-9-181-3-177 5-180 M-2-197 Q-8-199-6-192 L-1-188"),
    hair: new Path2D("M-9-237 Q-17-229-17-220 M4-239 Q-4-230-4-221 M18-236 Q15-237 12-232"),
    eye: new Path2D("M25-204 C20-212 18-221 22-224 C29-226 32-216 33-209"),
    pupil: new Path2D("M30-216 Q33-211 33-206"),
    mouth: new Path2D("M49-183 Q46-181 43-183"),
    body: new Path2D("M-12-155 Q-29-154-37-135 Q-40-119-31-105 C-35-84-31-56-22-42 Q-9-32 10-38 Q27-40 30-59 C33-79 26-103 24-120 Q22-145 11-153 Z"),
    bodyOutline: new Path2D("M-12-155 Q-29-154-37-135 Q-40-119-31-105 C-35-84-31-56-22-42 M10-38 Q27-40 30-59 C33-79 26-103 24-120 Q22-145 11-153"),
    freeSleeve: new Path2D("M-17-141 Q-4-142 3-125 L17-102 Q20-94 10-88 Q3-85-2-92 L-22-114 Q-27-128-17-141"),
    freeHand: new Path2D("M12-96 L24-90 Q31-86 30-82 L25-83 L31-74 Q32-70 29-71 L22-77 L26-69 Q26-65 22-69 L16-75 L19-68 Q18-64 15-69 L7-80 L7-91 Z"),
    leg: new Path2D("M-14-5 Q-16 10-11 25 L-8 39 Q-10 47-3 49 L27 49 Q35 48 33 42 Q29 37 14 35 L12 26 Q17 10 14-5"),
    legCrease: new Path2D("M12 34 Q18 39 24 39"),
    hand: new Path2D("M-2-6 Q5-9 9-4 L14-7 Q19-9 19-4 L14 1 Q20 3 18 7 Q11 12 1 8 L-4 4 Z"),
  };


  // Traced from the supplied screenshot in slide-relative coordinates.
  // The route includes off-screen lead-in and lead-out for a full entrance/exit.
  const route = [
    [-.09,.23], [0,.267], [.08,.305], [.17,.305], [.26,.274],
    [.31,.23], [.32,.15], [.318,.10], [.30,.061], [.27,.058],
    [.20,.097], [.16,.157], [.14,.257], [.127,.42], [.125,.60],
    [.125,.66], [.16,.79], [.20,.855], [.27,.87], [.30,.83],
    [.34,.74], [.37,.60], [.395,.38], [.43,.22], [.46,.18],
    [.52,.14], [.57,.15], [.63,.22], [.66,.32], [.66,.43],
    [.655,.55], [.63,.66], [.59,.73], [.54,.75], [.48,.76],
    [.44,.73], [.428,.69], [.428,.58], [.438,.54], [.48,.516],
    [.54,.516], [.59,.55], [.66,.615], [.72,.62], [.80,.635],
    [.85,.621], [.88,.601], [.91,.53], [.945,.43], [.952,.34],
    [.946,.28], [.925,.242], [.88,.233], [.85,.242], [.83,.27],
    [.81,.32], [.79,.37], [.78,.44], [.775,.52], [.79,.58],
    [.81,.66], [.83,.72], [.87,.75], [.94,.775], [1,.8], [1.10,.81],
  ];

  function createScene(ctx) {
    let width = 1, height = 1, scale = 1;
    let samples = [], totalLength = 0;

    function resize(w, h) {
      width = Math.max(1, w);
      height = Math.max(1, h);
      scale = Math.min(.66, width / 2100, height / 1250) * 0.55;
      samples = [];
      totalLength = 0;
      for (let i = 0; i < route.length - 1; i++) {
        const a = route[Math.max(0, i - 1)], b = route[i];
        const c = route[i + 1], d = route[Math.min(route.length - 1, i + 2)];
        for (let j = 0; j < 30; j++) {
          const t = j / 30;
          const point = [0, 1].map(axis => .5 * (
            2*b[axis] + (-a[axis]+c[axis])*t +
            (2*a[axis]-5*b[axis]+4*c[axis]-d[axis])*t*t +
            (-a[axis]+3*b[axis]-3*c[axis]+d[axis])*t*t*t
          ));
          addSample(point);
        }
      }
      addSample(route.at(-1));
    }

    function addSample(point) {
      const p = {x: point[0] * width, y: point[1] * height};
      const previous = samples.at(-1);
      if (previous) totalLength += Math.hypot(p.x-previous.x, p.y-previous.y);
      samples.push({...p, distance: totalLength});
    }

    function poseAt(time) {
      const distance = totalLength * clamp(time / 28400);
      let low = 0, high = samples.length - 1;
      while (low < high) {
        const mid = Math.floor((low + high) / 2);
        if (samples[mid].distance < distance) low = mid + 1;
        else high = mid;
      }
      const index = Math.max(1, low);
      const a = samples[index - 1], b = samples[index];
      const f = clamp((distance - a.distance) / (b.distance - a.distance || 1));
      const tip = [mix(a.x,b.x,f), mix(a.y,b.y,f)];
      // Raise the crayon near the top of the page, lower it near the bottom.
      // Its tip stays exactly on the route; the body remains upright.
      const aim = mix(-.75, .75, clamp((tip[1] / height - .06) / .81));
      return {
        x: tip[0] - scale * (18 + Math.cos(aim) * 117),
        y: tip[1] + scale * (135 - Math.sin(aim) * 117),
        moving: time < 28400, drawing: true, aim, tip, index,
        walkingDistance: time / 320 * Math.max(scale * 22, 1),
      };
    }

    function paint(path, fill) {
      if (fill) { ctx.fillStyle = fill; ctx.fill(path); }
      ctx.stroke(path);
    }

    function limb(x, y, phase, back, moving) {
      // A gentle step with foot lift, rather than spinning a rigid whole figure.
      const step = moving ? Math.sin(phase) : 0;
      const lift = moving ? Math.max(0, Math.cos(phase)) * 7 : 0;
      ctx.save();
      ctx.translate(x + step * 10, y - lift);
      ctx.rotate(-step * .18);
      paint(artwork.leg, back ? "#f6f2e8" : PAJAMAS);
      ctx.lineWidth = 1.4;
      paint(artwork.legCrease);
      ctx.restore();
    }

    function drawHarold(pose, time, tip) {
      const gait = pose.moving ? pose.walkingDistance / Math.max(scale * 22, 1) : 0;
      const settle = pose.moving ? Math.sin(gait) : 0;
      const bob = 0; // Keep the crayon tip registered to the drawn path.
      ctx.save();
      ctx.translate(pose.x, pose.y);
      // Always a positive scale: Harold never mirrors, flips, or turns around.
      ctx.scale(scale, scale);
      ctx.strokeStyle = INK;
      ctx.lineWidth = 2;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";

      limb(-11, -49, gait + Math.PI, true, pose.moving);
      limb(13, -49, gait, false, pose.moving);
      ctx.save();
      ctx.translate(0, -bob);
      ctx.fillStyle = PAJAMAS;
      ctx.fill(artwork.body);
      paint(artwork.bodyOutline);
      // The free arm is now on the front of the torso, reaching forward.
      ctx.save();
      ctx.translate(-15, -133);
      ctx.rotate(settle * .07);
      ctx.translate(15, 133);
      paint(artwork.freeHand, SKIN);
      paint(artwork.freeSleeve, PAJAMAS);
      ctx.restore();

      // A slight attentive nod while drawing; the profile stays right-facing.
      ctx.save();
      ctx.translate(2, -155);
      ctx.rotate(pose.drawing ? -.035 + Math.sin(time / 900) * .025 : -.015);
      ctx.translate(-2, 155);
      paint(artwork.head, SKIN);
      ctx.lineWidth = 1.5;
      paint(artwork.ear);
      paint(artwork.hair);
      paint(artwork.eye);
      paint(artwork.pupil);
      paint(artwork.mouth);
      ctx.restore();

      // The forearm follows the current drawing. Harold's purple crayon makes
      // the larger picture ahead of him, just as in the supplied animation.
      const shoulder = [18, -135];
      const aim = pose.aim;
      const wrist = [shoulder[0] + Math.cos(aim) * 61, shoulder[1] + Math.sin(aim) * 61];
      const elbow = [shoulder[0] + 28, shoulder[1] + 31 + Math.sin(aim) * 20];
      const sleeve = new Path2D();
      sleeve.moveTo(shoulder[0] - 7, shoulder[1] - 10);
      sleeve.quadraticCurveTo(shoulder[0] + 12, shoulder[1] - 7, elbow[0] + 5, elbow[1] - 9);
      sleeve.lineTo(wrist[0] - 7, wrist[1] - 8);
      sleeve.quadraticCurveTo(wrist[0] + 3, wrist[1], wrist[0] - 5, wrist[1] + 10);
      sleeve.quadraticCurveTo(elbow[0] - 5, elbow[1] + 12, elbow[0] - 13, elbow[1] + 4);
      sleeve.quadraticCurveTo(shoulder[0] - 7, shoulder[1] + 13, shoulder[0] - 7, shoulder[1] + 3);
      ctx.lineWidth = 2;
      paint(sleeve, PAJAMAS);
      ctx.save();
      ctx.translate(...wrist);
      ctx.rotate(aim);
      ctx.fillStyle = PURPLE;
      ctx.beginPath();
      ctx.moveTo(8, -5); ctx.lineTo(42, -5); ctx.lineTo(56, 0);
      ctx.lineTo(42, 5); ctx.lineTo(8, 5); ctx.closePath(); ctx.fill();
      ctx.strokeStyle = "#83257e";
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(39, -4); ctx.lineTo(39, 4); ctx.stroke();
      ctx.strokeStyle = INK;
      ctx.lineWidth = 1.7;
      paint(artwork.hand, SKIN);
      ctx.restore();
      ctx.restore();
      ctx.restore();
    }


    function render(time) {
      if (!samples.length) return;
      time = clamp(time, 0, DURATION);
      ctx.clearRect(0, 0, width, height);
      const pose = poseAt(time);
      ctx.globalAlpha = time > 28800 ? 1 - ease((time - 28800) / 1200) : 1;
      ctx.strokeStyle = PURPLE;
      ctx.lineWidth = clamp(width / 220, 6, 10);
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      ctx.beginPath();
      ctx.moveTo(samples[0].x, samples[0].y);
      for (let i = 1; i < pose.index; i++) ctx.lineTo(samples[i].x, samples[i].y);
      ctx.lineTo(...pose.tip);
      ctx.stroke();
      drawHarold(pose, time, pose.tip);
      ctx.globalAlpha = 1;
    }

    return { resize, render, poseAt, duration: DURATION };
  }

  // Also usable by the local frame renderer for visual checks, without a browser.
  if (typeof module !== "undefined" && module.exports) {
    module.exports = { createScene };
    return;
  }

  const slide = document.querySelector("#slide-4");
  const canvas = slide?.querySelector(".harold-background");
  const ctx = canvas?.getContext("2d");
  if (!ctx) return;
  const scene = createScene(ctx);
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  let elapsed = 0, previousTime = null, frameId = 0, visible = false;

  function resize() {
    const dpr = Math.min(devicePixelRatio || 1, 2);
    canvas.width = Math.round(slide.clientWidth * dpr);
    canvas.height = Math.round(slide.clientHeight * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    scene.resize(slide.clientWidth, slide.clientHeight);
    scene.render(reducedMotion.matches ? 28600 : elapsed);
  }

  function tick(now) {
    if (previousTime !== null) elapsed = (elapsed + now - previousTime) % scene.duration;
    previousTime = now;
    scene.render(elapsed);
    frameId = requestAnimationFrame(tick);
  }

  function updatePlayback() {
    cancelAnimationFrame(frameId);
    previousTime = null;
    if (reducedMotion.matches) scene.render(28600);
    else if (visible && !document.hidden) frameId = requestAnimationFrame(tick);
  }

  new ResizeObserver(resize).observe(slide);
  new IntersectionObserver(([entry]) => {
    const entering = entry.isIntersecting && entry.intersectionRatio >= .5;
    if (entering && !visible) elapsed = 0;
    visible = entering;
    updatePlayback();
  }, { root: document.querySelector("main"), threshold: [0, .5] }).observe(slide);
  document.addEventListener("visibilitychange", updatePlayback);
  reducedMotion.addEventListener("change", updatePlayback);
  resize();
})();
