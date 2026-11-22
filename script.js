"use strict";

const platform = navigator.userAgentData?.platform || navigator.platform;
const reportsMac = platform ? /^mac/i.test(platform) : /\bMacintosh\b/i.test(navigator.userAgent);
// iPadOS can report a Mac platform when requesting a desktop site.
if (reportsMac && !(navigator.maxTouchPoints > 1)) {
  const macDownload = document.getElementById("mac-download");
  macDownload.parentElement.prepend(macDownload);
}

function recordDownload() {
  fetch("https://ceejkfaygodqzzxcvwkk.supabase.co/rest/v1/downloads", {
    method: "POST",
    headers: {
      apikey: "sb_publishable_KsNEFYPZZAjv_PydecVitw_Mrn0UWdm",
      "Content-Type": "application/json",
      Prefer: "return=minimal",
    },
    body: JSON.stringify({ downloaded_at: new Date().toISOString() }),
    credentials: "omit",
    keepalive: true,
  }).then((response) => {
    if (!response.ok) console.warn("Download tracking returned HTTP", response.status);
  }).catch(() => console.warn("Download tracking could not reach Supabase."));
}

for (const link of document.querySelectorAll(".download-buttons a[download]")) {
  link.addEventListener("click", recordDownload);
  link.addEventListener("auxclick", (event) => {
    if (event.button === 1) recordDownload();
  });
}

// Play videos on the visible slide and pause them when another slide is shown.
const slides = document.querySelectorAll(".slide");
document.querySelectorAll("video[data-playback-rate]").forEach((video) => {
  const rate = Number(video.dataset.playbackRate);
  if (Number.isFinite(rate) && rate > 0) {
    video.defaultPlaybackRate = rate;
    video.playbackRate = rate;
  }
});
const slideObserver = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    for (const video of entry.target.querySelectorAll("video")) {
      if (entry.isIntersecting) {
        video.play().catch(() => {
          // Ignore autoplay restrictions without interrupting the presentation.
        });
      } else {
        video.pause();
      }
    }
  }
}, { root: document.querySelector("main"), threshold: 0.5 });

slides.forEach((slide) => slideObserver.observe(slide));

const presentation = document.querySelector("main");

document.addEventListener("keydown", (event) => {
  const direction = {
    ArrowRight: 1,
    ArrowDown: 1,
    ArrowLeft: -1,
    ArrowUp: -1,
  }[event.key];

  if (!direction || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  if (event.target.closest("input, textarea, select, [contenteditable]:not([contenteditable='false']), [role='slider']")) return;

  event.preventDefault();
  if (event.repeat || !slides.length) return;

  const top = presentation.getBoundingClientRect().top;
  let currentIndex = 0;
  let nearestDistance = Infinity;

  slides.forEach((slide, index) => {
    const distance = Math.abs(slide.getBoundingClientRect().top - top);
    if (distance < nearestDistance) {
      nearestDistance = distance;
      currentIndex = index;
    }
  });

  const nextIndex = Math.max(0, Math.min(slides.length - 1, currentIndex + direction));
  presentation.scrollTo({
    top: presentation.scrollTop + slides[nextIndex].getBoundingClientRect().top - top,
    behavior: "instant",
  });
});
