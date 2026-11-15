"use strict";

// Adapted from drawing-intelligence/js/draw-tool.js: a separate annotation
// canvas and toggled tools. Clearing removes ink, never the slide underneath.
(() => {
  const slide = document.querySelector("#slide-4");
  const canvas = slide?.querySelector(".drawing-canvas");
  const ctx = canvas?.getContext("2d");
  if (!ctx) return;

  const buttons = slide.querySelectorAll("[data-drawing-tool]");
  const strokes = [];
  let tool = null;
  let activeStroke = null;
  let pointerId = null;
  let width = 1;
  let height = 1;

  function finishStroke() {
    const capturedPointer = pointerId;
    pointerId = null;
    activeStroke = null;
    if (capturedPointer !== null && canvas.hasPointerCapture(capturedPointer)) {
      canvas.releasePointerCapture(capturedPointer);
    }
  }

  function setTool(next) {
    finishStroke();
    tool = next;
    canvas.dataset.tool = tool || "";
    buttons.forEach(button => {
      if (button.dataset.drawingTool === "draw") button.setAttribute("aria-pressed", String(tool === "draw"));
    });
  }

  function pointFrom(event) {
    const rect = canvas.getBoundingClientRect();
    // Store proportions so drawings survive resizes without being cleared.
    return {
      x: (event.clientX - rect.left) / rect.width,
      y: (event.clientY - rect.top) / rect.height,
    };
  }

  function paintSegment(stroke, from, to) {
    ctx.save();
    ctx.globalCompositeOperation = "source-over";
    ctx.strokeStyle = "#a33399";
    ctx.fillStyle = "#a33399";
    ctx.lineWidth = stroke.size;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.beginPath();
    if (!from) {
      ctx.arc(to.x * width, to.y * height, stroke.size / 2, 0, Math.PI * 2);
      ctx.fill();
    } else {
      ctx.moveTo(from.x * width, from.y * height);
      ctx.lineTo(to.x * width, to.y * height);
      ctx.stroke();
    }
    ctx.restore();
  }

  function resize() {
    finishStroke();
    width = Math.max(1, slide.clientWidth);
    height = Math.max(1, slide.clientHeight);
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    for (const stroke of strokes) {
      stroke.points.forEach((point, index) => {
        paintSegment(stroke, index ? stroke.points[index - 1] : null, point);
      });
    }
  }

  function appendPoints(event) {
    const coalesced = event.getCoalescedEvents?.();
    const events = coalesced?.length ? coalesced : [event];
    for (const sample of events) {
      const point = pointFrom(sample);
      const previous = activeStroke.points.at(-1);
      if (point.x === previous.x && point.y === previous.y) continue;
      activeStroke.points.push(point);
      paintSegment(activeStroke, previous, point);
    }
  }

  canvas.addEventListener("pointerdown", event => {
    if (!tool || pointerId !== null || !event.isPrimary || event.button !== 0) return;
    event.preventDefault();
    pointerId = event.pointerId;
    canvas.setPointerCapture(pointerId);
    activeStroke = { tool, size: 6, points: [pointFrom(event)] };
    strokes.push(activeStroke);
    paintSegment(activeStroke, null, activeStroke.points[0]);
  });

  canvas.addEventListener("pointermove", event => {
    if (event.pointerId !== pointerId || !activeStroke) return;
    event.preventDefault();
    appendPoints(event);
  });

  canvas.addEventListener("pointerup", event => {
    if (event.pointerId !== pointerId || !activeStroke) return;
    appendPoints(event);
    finishStroke();
  });
  canvas.addEventListener("pointercancel", event => {
    if (event.pointerId === pointerId) finishStroke();
  });
  canvas.addEventListener("lostpointercapture", event => {
    if (event.pointerId === pointerId) finishStroke();
  });

  buttons.forEach(button => {
    button.addEventListener("click", () => {
      const next = button.dataset.drawingTool;
      if (next === "clear") {
        finishStroke();
        strokes.length = 0;
        ctx.clearRect(0, 0, width, height);
      } else {
        setTool(tool === next ? null : next);
      }
    });
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && tool) setTool(null);
  });
  window.addEventListener("blur", finishStroke);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) finishStroke();
  });

  new ResizeObserver(resize).observe(slide);
  new IntersectionObserver(([entry]) => {
    if (!entry.isIntersecting || entry.intersectionRatio < .5) setTool(null);
  }, { root: document.querySelector("main"), threshold: [0, .5] }).observe(slide);
  resize();
})();
