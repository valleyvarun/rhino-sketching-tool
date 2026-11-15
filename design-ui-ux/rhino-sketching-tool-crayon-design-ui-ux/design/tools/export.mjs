// Crayon design export
// Reads the prototype (design/prototype/index.html) and writes:
//   design/tokens/tokens.json   design tokens (W3C Design Tokens format)
//   design/tokens/tokens.css    the same tokens as CSS variables (same names as the prototype)
//   design/icons/*.svg          the toolbar and option-row icons
//   design/brand/*.svg          the Crayon mark, small mark and lockup
// The prototype is the source of truth: change it, then run `node design/tools/export.mjs` again.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const html = readFileSync(join(ROOT, 'prototype', 'index.html'), 'utf8');
const out = (rel, text) => { const p = join(ROOT, rel); mkdirSync(dirname(p), { recursive: true }); writeFileSync(p, text.replace(/\r?\n/g, '\n')); console.log('wrote', rel); };

/* ---------- 1. tokens ---------- */
// The final look = the base :root block, then the Paper theme, then the logo colours.
function block(startToken){
  const i = html.indexOf(startToken); if (i < 0) throw new Error('missing ' + startToken);
  const body = html.slice(html.indexOf('{', i) + 1, html.indexOf('}', i)).replace(/\/\*[\s\S]*?\*\//g, '');
  const vars = {}; for (const m of body.matchAll(/--([\w-]+)\s*:\s*([^;]+);/g)) vars[m[1]] = m[2].trim();
  return vars;
}
const V = Object.assign({}, block(':root{\n  /* viewport'), block(':root[data-style="paper"]{'), block(':root{ --logo:'));

const GROUPS = {
  text:      ['ink', 'ink-2', 'muted'],
  surface:   ['surface', 'surface-2', 'glass', 'glass-strong', 'well', 'field', 'hover', 'press', 'soft-sel', 'soft-sel-hover', 'line', 'line-2'],
  accent:    ['accent', 'accent-hi', 'accent-ink', 'accent-soft', 'accent-ring'],
  status:    ['danger', 'sel'],
  logo:      ['logo', 'logo-deep', 'logo-body'],
  viewport:  ['vp-bg', 'vp-grid', 'vp-grid-major', 'vp-obj', 'vp-edge'],
};
const NOTES = {
  ink:'Main text and icons', 'ink-2':'Secondary text', muted:'Hints, captions, inactive labels',
  surface:'Floating bars and cards', 'surface-2':'Side panel background', glass:'Toolbar and option row background', 'glass-strong':'Pop-ups',
  well:'Resting button fill', field:'Text fields', hover:'Hover overlay', press:'Pressed overlay',
  'soft-sel':'Selected state: always this soft grey, never the accent', 'soft-sel-hover':'Selected + hover',
  line:'Hairline borders', 'line-2':'Stronger borders',
  accent:'Violet from the Drawing Intelligence presentation. Only for state: active dot, comments, checks, switches, AI tags',
  'accent-hi':'Accent, highlight end', 'accent-ink':'Text on the accent', 'accent-soft':'Accent tint behind accent text', 'accent-ring':'Focus ring / accent glow',
  danger:'Destructive actions', sel:'Selected object outline in the viewport',
  logo:'Logo: the C and the crayon tip', 'logo-deep':'Logo: shadow side of the tip', 'logo-body':'Logo: crayon wrapper',
  'vp-bg':'Viewport background (mock only: Rhino draws this)', 'vp-grid':'Grid (mock only)', 'vp-grid-major':'Grid major lines (mock only)', 'vp-obj':'Model fill (mock only)', 'vp-edge':'Model edges (mock only)',
};
const tok = (value, type, note) => Object.assign({ $value:value }, type ? { $type:type } : {}, note ? { $description:note } : {});
const bez = (v) => v.match(/cubic-bezier\(([^)]+)\)/)[1].split(',').map(Number);
const ms = (v) => Math.round(parseFloat(v) * (v.trim().endsWith('ms') ? 1 : 1000)) + 'ms';

const tokens = {
  $description: 'Crayon design tokens. Generated from design/prototype/index.html by design/tools/export.mjs. Names match the CSS variables in the prototype.',
  color: {},
  font: {
    ui:       tok(V['f-ui'], 'fontFamily', 'All interface text (the system UI font, so it feels native inside Rhino)'),
    mono:     tok(V['f-mono'], 'fontFamily', 'Coordinates and numbers'),
    wordmark: tok('"Instrument Sans", sans-serif', 'fontFamily', 'Only the "crayon" wordmark: weight 600, letter-spacing -0.03em, always lowercase'),
  },
  radius: {
    sm: tok(V['r-sm'], 'dimension'), md: tok(V['r-md'], 'dimension'), lg: tok(V['r-lg'], 'dimension'), xl: tok(V['r-xl'], 'dimension'),
    dock:      tok('16px', 'dimension', 'Floating toolbar (slightly filleted, never a pill)'),
    'option-row': tok('15px', 'dimension', 'The option row under the toolbar'),
    segment:   tok('12px', 'dimension', 'Segmented toggle (e.g. Object / Plane)'),
    button:    tok('10px', 'dimension', 'Buttons and chips in the option row'),
    checkbox:  tok('4px', 'dimension', 'Snap shapes checkbox'),
  },
  shadow: {
    1: tok(V['shadow-1'], null, 'Cards: a hairline only (CSS box-shadow)'),
    2: tok(V['shadow-2'], null, 'Toolbar and option row (CSS box-shadow)'),
    3: tok(V['shadow-3'], null, 'Pop-ups (CSS box-shadow)'),
  },
  motion: {
    ease:   tok(bez(V.ease), 'cubicBezier', 'Gentle ease-out for fades'),
    spring: tok(bez(V.spring), 'cubicBezier', 'Snappy, no overshoot: nothing in Crayon bounces'),
    fast:   tok(ms(V['t-fast']), 'duration', 'Hover, press'),
    med:    tok(ms(V['t-med']), 'duration', 'Panels, rows'),
    slow:   tok(ms(V['t-slow']), 'duration', 'Rare, larger moves'),
  },
  size: {
    'tool-icon':   tok('20px', 'dimension', 'Icons in the toolbar'),
    'tool-button': tok('40px', 'dimension', 'Toolbar button (square hit area)'),
    'row-icon':    tok('15px', 'dimension', 'Icons in the option row'),
    'row-control': tok('30px', 'dimension', 'Height of option-row buttons and chips'),
    'gap-bars':    tok('8px', 'dimension', 'Gap between the toolbar and its option row'),
    'edge-inset':  tok('14px', 'dimension', 'Toolbar distance from the viewport edge'),
  },
};
for (const [group, names] of Object.entries(GROUPS)){
  tokens.color[group] = {};
  for (const n of names) if (V[n]) tokens.color[group][n] = tok(V[n], 'color', NOTES[n]);
}
out('tokens/tokens.json', JSON.stringify(tokens, null, 2) + '\n');

let css = '/* Crayon design tokens. Generated from design/prototype/index.html by design/tools/export.mjs. */\n:root{\n';
for (const [group, names] of Object.entries(GROUPS)){
  css += `  /* ${group} */\n`;
  for (const n of names) if (V[n]) css += `  --${n}:${V[n]};${NOTES[n] ? ' /* ' + NOTES[n] + ' */' : ''}\n`;
}
css += '  /* type */\n' + `  --f-ui:${V['f-ui']};\n  --f-mono:${V['f-mono']};\n  --f-wordmark:"Instrument Sans", sans-serif;\n`;
css += '  /* shape */\n' + ['r-sm', 'r-md', 'r-lg', 'r-xl'].map((n) => `  --${n}:${V[n]};\n`).join('') + '  --r-dock:16px; --r-option-row:15px; --r-segment:12px; --r-button:10px; --r-checkbox:4px;\n';
css += '  /* elevation */\n' + [1, 2, 3].map((n) => `  --shadow-${n}:${V['shadow-' + n]};\n`).join('');
css += '  /* motion */\n' + `  --ease:${V.ease}; --spring:${V.spring}; /* no overshoot: nothing bounces */\n  --t-fast:${V['t-fast']}; --t-med:${V['t-med']}; --t-slow:${V['t-slow']};\n`;
css += '  /* sizes */\n  --tool-icon:20px; --tool-button:40px; --row-icon:15px; --row-control:30px; --gap-bars:8px; --edge-inset:14px;\n}\n';
out('tokens/tokens.css', css);

/* ---------- 2. icons ---------- */
const TINT = 'fill="currentColor" fill-opacity=".18"';
const ICONS = {};
for (const m of html.matchAll(/^ {2}(\w+): (SF|SV)\((['`])(.*)\3\),?$/gm)) ICONS[m[1]] = { weight:m[2] === 'SF' ? 1.8 : 1.6, body:m[4].replaceAll('${TINT}', TINT) };
const ball = html.match(/ball: {2}\['(.*?)', '(.*?)'\]/);
ICONS.tObj = { weight:1.8, body:ball[1] }; ICONS.tPlane = { weight:1.8, body:ball[2] };
const EXPORT = {
  'tool-select':'select', 'tool-pen':'pen', 'tool-eraser':'eraser', 'tool-marks':'marks', 'tool-saved-views':'views',
  'pen-object':'tObj', 'pen-plane':'tPlane', 'pick-a-face':'movePlane',
  'tab-layers':'tabLayers', 'tab-agent':'tabAgent',
  'plus':'plus', 'sync':'sync', 'rename':'rename', 'close':'close', 'chevron':'chevron', 'eye':'eye', 'trash':'trash',
};
for (const [file, key] of Object.entries(EXPORT)){
  const ic = ICONS[key]; if (!ic) throw new Error('icon not found: ' + key);
  // the shutter disc and flash only exist for the click animation; a static icon leaves them out
  const body = ic.body.replace(/<circle class="ic-shut"[^>]*\/>/, '').replace(/<path class="ic-flash"[^>]*\/>/, '');
  out(`icons/${file}.svg`, `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="${ic.weight}" stroke-linecap="round" stroke-linejoin="round">${body}</svg>\n`);
}
// the hand-drawn tick of the Snap shapes checkbox (drawn in the accent colour, it overflows its box)
const tick = html.match(/<path class="ck-a" d="([^"]+)"\/><path class="ck-b" d="([^"]+)"\/>/);
out('icons/check-tick.svg', `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="${V.accent}" stroke-linecap="round" stroke-linejoin="round"><path d="${tick[1]}" stroke-width="2.3"/><path d="${tick[2]}" stroke-width="1.1" opacity=".35"/></svg>\n`);

/* ---------- 3. brand ---------- */
const a = html.indexOf('const crayonMark = (() => {'), b = html.indexOf('\n})();', a);
const crayonMark = new Function(html.slice(a, b + 6) + '\nreturn crayonMark;')();
const pick = (s, cls) => s.match(new RegExp(`<path class="${cls}"[^>]*? d="([^"]+)"`))[1];
function mark(small, c, id){
  const inner = crayonMark(small), gi = inner.indexOf('<g transform');
  const cPaths = (gi < 0 ? inner : inner.slice(0, gi)).replaceAll('class="lg-a"', `fill="${c.acc}"`);
  if (small) return { defs:'', body:cPaths };
  const g = inner.slice(gi), T = g.match(/transform="([^"]+)"/)[1];
  // Transparent background: the crayon is cut out of the C with a mask (in the app it is cut in the panel colour).
  const defs = `<mask id="${id}-cut" maskUnits="userSpaceOnUse" x="0" y="0" width="64" height="64"><rect width="64" height="64" fill="#fff"/><path transform="${T}" d="${pick(g, 'lg-h')}" fill="#000" stroke="#000" stroke-width="3.4" stroke-linejoin="round"/></mask>`
    + `<mask id="${id}-wrap" maskUnits="userSpaceOnUse" x="-20" y="-20" width="80" height="80"><rect x="-20" y="-20" width="80" height="80" fill="#fff"/><path d="${pick(g, 'lg-w')}" fill="none" stroke="#000" stroke-width="1.3"/></mask>`;
  const body = `<g mask="url(#${id}-cut)">${cPaths}</g><g transform="${T}"><path d="${g.match(/<path class="lg-a" d="([^"]+)"/)[1]}" fill="${c.acc}"/><path d="${pick(g, 'lg-d')}" fill="${c.deep}"/><path d="${pick(g, 'lg-b')}" fill="${c.body}" mask="url(#${id}-wrap)"/></g>`;
  return { defs, body };
}
const svg64 = (m) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64">${m.defs ? '<defs>' + m.defs + '</defs>' : ''}${m.body}</svg>\n`;
const C = {
  light: { acc:V.logo, deep:V['logo-deep'], body:V['logo-body'] },
  dark:  { acc:'#b165c4', deep:V.logo, body:'#f2f1ed' },
  mono:  { acc:'#1d2025', deep:'#1d2025', body:'#1d2025' },
  white: { acc:'#ffffff', deep:'#ffffff', body:'#ffffff' },
};
out('brand/crayon-mark.svg', svg64(mark(false, C.light, 'cm')));
out('brand/crayon-mark-on-dark.svg', svg64(mark(false, C.dark, 'cmd')));
out('brand/crayon-mark-mono.svg', svg64(mark(false, C.mono, 'cmm')));
out('brand/crayon-mark-white.svg', svg64(mark(false, C.white, 'cmw')));
out('brand/crayon-c.svg', svg64(mark(true, C.light)));
out('brand/crayon-c-on-dark.svg', svg64(mark(true, C.dark)));
out('brand/crayon-c-mono.svg', svg64(mark(true, C.mono)));
// Lockup: mark + the lowercase wordmark. The word is live text in Instrument Sans 600; outline it before print use.
const lock = (c, ink, id) => { const m = mark(false, c, id);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 232 64" width="232" height="64"><defs>${m.defs}</defs>${m.body}`
    + `<text x="70" y="45.5" font-family="Instrument Sans, sans-serif" font-weight="600" font-size="45" letter-spacing="-1.35" fill="${ink}">crayon</text></svg>\n`; };
out('brand/crayon-lockup.svg', lock(C.light, '#1d2025', 'cl'));
out('brand/crayon-lockup-on-dark.svg', lock(C.dark, '#f2f1ed', 'cld'));
console.log('done');
