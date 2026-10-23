#!/usr/bin/env node
// The app icon, drawn from the mark in design/icons.json on the palette in design/tokens.json:
//
//   design/brand/app-icon.svg                             light master
//   design/brand/app-icon-dark.svg                        dark master, for an appearance variant
//   src/Dray.MacOS/Assets.xcassets/AppIcon.appiconset     every PNG macOS asks for, + Contents.json
//   src/Dray.Windows/Platforms/Windows/Dray.ico           the exe's icon: taskbar, Alt+Tab, toasts
//   src/Dray.Windows/Platforms/Windows/TitleBarIcon.svg   the tile beside "Dray" in the title bar
//   src/Dray.DevHost/wwwroot/favicon.svg                  the same, in the DevHost's browser tab
//
// The masters are deterministic text, so --check compares them like any other generated file. The
// PNGs need a rasteriser, and rather than take an npm dependency for one this drives a Chrome or
// Edge already on the machine (set CHROME to point at one). So the PNGs and the .ico are committed,
// and design/brand/app-icon.rendered records a hash of exactly what they were drawn from — --check
// fails when the mark or the palette has moved on and they have not.
//
//   node build/gen-app-icon.mjs           write masters and render PNGs
//   node build/gen-app-icon.mjs --check   fail if anything is stale (CI; needs no browser)

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { loadTokens, repoRoot, resolveTheme, toHex } from './color.mjs';

const check = process.argv.includes('--check');

const { geometry, marks } = JSON.parse(readFileSync(join(repoRoot, 'design', 'icons.json'), 'utf8'));
const mark = marks.dray;
const tokens = loadTokens();
const light = resolveTheme(tokens, 'light');
const dark = resolveTheme(tokens, 'dark');

// ------------------------------------------------------------------ the tile
//
// One drawing, two frames. macOS uses Apple's icon grid: an 824-point body on a 1024 canvas with a
// 185 corner radius, the margin left for the shadow the tile draws. Windows icons cast no shadow
// and fill their square — at 16px in a title bar, a 1.5px gutter would only make the mark smaller.
// The mark is drawn on its own 24-unit grid, centred on the canvas, so a frame centres the grid
// rather than measuring the drawing.
//
// Small renders get a heavier stroke. At 16px the 2-unit stroke lands at three-quarters of a
// pixel and the mark dissolves; the same drawing a little bolder is what survives a Finder list.

const MAC = { inset: 100, radius: 185, scale: 24, shadow: true };     // 24 units -> 576 px of an 824 body
const WINDOWS = { inset: 0, radius: 200, scale: 30, shadow: false };  // 24 units -> 720 px of a 1024 body
const strokeFor = (px) => (px <= 16 ? 3 : px <= 32 ? 2.5 : geometry.strokeWidth);

function tile({ top, bottom, ink, load, loadOpacity }, stroke, frame = MAC) {
  const { inset, radius, scale, shadow } = frame;
  const body = 1024 - 2 * inset;
  const offset = 512 - 12 * scale;
  const filter = shadow ? `
    <filter id="drop" x="-10%" y="-10%" width="120%" height="125%">
      <feDropShadow dx="0" dy="10" stdDeviation="10" flood-opacity="0.3"/>
    </filter>` : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">
  <defs>
    <linearGradient id="body" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="${top}"/>
      <stop offset="1" stop-color="${bottom}"/>
    </linearGradient>${filter}
  </defs>
  <rect x="${inset}" y="${inset}" width="${body}" height="${body}" rx="${radius}" fill="url(#body)"${shadow ? ' filter="url(#drop)"' : ''}/>
  <rect x="${inset + 1}" y="${inset + 1}" width="${body - 2}" height="${body - 2}" rx="${radius - 1}" fill="none" stroke="white" stroke-opacity="0.12" stroke-width="2"/>
  <g transform="translate(${offset} ${offset}) scale(${scale})" fill="none" stroke="${ink}" stroke-width="${stroke}"
     stroke-linecap="${geometry.linecap}" stroke-linejoin="${geometry.linejoin}">
    <path d="${mark.load}" fill="${load}" fill-opacity="${loadOpacity}" stroke="none"/>
    ${mark.svg.replaceAll("'", '"')}
  </g>
</svg>
`;
}

const lightTile = {
  top: toHex(light.brand),
  bottom: toHex(light['brand-hi']),
  ink: toHex(light['on-brand']),
  load: toHex(light['on-brand']),
  loadOpacity: 0.2,
};
const darkTile = {
  top: toHex(dark.line),
  bottom: toHex(dark.bg),
  ink: toHex(dark.brand),
  load: toHex(dark.brand),
  loadOpacity: 0.18,
};

// ------------------------------------------------------------------ macOS icon set

// [point size, scale] — the ten slots of a macOS app icon set.
const SLOTS = [16, 32, 128, 256, 512].flatMap((pt) => [[pt, 1], [pt, 2]]);
const fileFor = (pt, scale) => `icon_${pt}x${pt}${scale === 2 ? '@2x' : ''}.png`;

const appiconset = join(repoRoot, 'src/Dray.MacOS/Assets.xcassets/AppIcon.appiconset');
const appiconContents = JSON.stringify(
  {
    images: SLOTS.map(([pt, scale]) => ({
      filename: fileFor(pt, scale),
      idiom: 'mac',
      scale: `${scale}x`,
      size: `${pt}x${pt}`,
    })),
    info: { author: 'dray', version: 1 },
  },
  null,
  2
) + '\n';

const renders = SLOTS.map(([pt, scale]) => ({
  file: fileFor(pt, scale),
  px: pt * scale,
  svg: tile(lightTile, strokeFor(pt * scale)),
}));

// ------------------------------------------------------------------ Windows
//
// One .ico, embedded in the exe as its Win32 icon (ApplicationIcon). Explorer, the taskbar, Alt+Tab
// and toasts all read it from there, and MAUI gives the window the exe's icon. A frame for every
// size Windows asks for between 100% and 200% scale, so none is a resample of another, each a PNG
// as-is — which Windows has read inside an .ico since Vista.
//
// The title bar draws its 16 effective pixels from an SVG instead, so it is sharp at any display
// scale; it is 16 to 32 physical pixels there, so it takes the 32px stroke. The DevHost's favicon
// is the same file, at the same size.

const windowsDir = join(repoRoot, 'src/Dray.Windows/Platforms/Windows');
const icoPath = join(windowsDir, 'Dray.ico');
const icoRenders = [16, 20, 24, 32, 40, 48, 64, 256].map((px) => ({
  file: `Dray.ico@${px}`,
  px,
  svg: tile(lightTile, strokeFor(px), WINDOWS),
}));
const smallTile = tile(lightTile, strokeFor(32), WINDOWS);

/** An ICONDIR, one entry per frame, then each PNG. A width or height of 256 is written as 0. */
function ico(frames) {
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(1, 2);                           // type: icon
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach(({ px, png }, i) => {
    const at = 6 + 16 * i;
    header.writeUInt8(px % 256, at);
    header.writeUInt8(px % 256, at + 1);
    header.writeUInt16LE(1, at + 4);                    // planes
    header.writeUInt16LE(32, at + 6);                   // bits per pixel
    header.writeUInt32LE(png.length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...frames.map((f) => f.png)]);
}

const stamp = createHash('sha256')
  .update([...renders, ...icoRenders].map((r) => `${r.file}\n${r.svg}`).join('\n'))
  .digest('hex') + '\n';

// ------------------------------------------------------------------ text outputs

const outputs = [
  [join(repoRoot, 'design/brand/app-icon.svg'), tile(lightTile, geometry.strokeWidth)],
  [join(repoRoot, 'design/brand/app-icon-dark.svg'), tile(darkTile, geometry.strokeWidth)],
  [join(appiconset, 'Contents.json'), appiconContents],
  [join(windowsDir, 'TitleBarIcon.svg'), smallTile],
  [join(repoRoot, 'src/Dray.DevHost/wwwroot/favicon.svg'), smallTile],
  [join(repoRoot, 'design/brand/app-icon.rendered'), stamp],
];

const rel = (p) => p.slice(repoRoot.length + 1);

if (check) {
  // Line endings aside: a Windows checkout with autocrlf hands back CRLF for these text files.
  const current = (path) => readFileSync(path, 'utf8').replaceAll('\r\n', '\n');
  const stale = outputs.filter(([path, content]) => !existsSync(path) || current(path) !== content);
  const missing = [...renders.map((r) => join(appiconset, r.file)), icoPath].filter((p) => !existsSync(p));
  for (const [path] of stale) console.error(`  \x1b[31mSTALE\x1b[0m ${rel(path)}`);
  for (const path of missing) console.error(`  \x1b[31mMISSING\x1b[0m ${rel(path)}`);
  if (stale.length || missing.length) {
    console.error('\n\x1b[31m\x1b[1mApp icon out of date.\x1b[0m Run: node build/gen-app-icon.mjs');
    process.exit(1);
  }
  console.log('\x1b[32m✓ App icon current.\x1b[0m');
  process.exit(0);
}

// ------------------------------------------------------------------ render
//
// One headless launch for every size. The page draws each SVG onto a canvas of exactly that many
// pixels and writes the PNG out as text, which --dump-dom hands back. Screenshots were the other
// route, and a browser window will not shrink to 16x16.

function findBrowser() {
  if (process.env.CHROME) return process.env.CHROME;
  const candidates = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
  ];
  const found = candidates.find((c) => existsSync(c));
  if (found) return found;
  for (const name of ['google-chrome', 'chromium', 'chromium-browser', 'microsoft-edge']) {
    const r = spawnSync('which', [name], { encoding: 'utf8' });
    if (r.status === 0 && r.stdout.trim()) return r.stdout.trim();
  }
  return null;
}

const browser = findBrowser();
if (!browser) {
  console.error('\x1b[31mNo Chrome or Edge found to render the PNGs.\x1b[0m Set CHROME to a Chromium-based browser.');
  process.exit(1);
}

const work = mkdtempSync(join(tmpdir(), 'dray-icon-'));
const page = join(work, 'render.html');
writeFileSync(page, `<!doctype html><meta charset="utf-8"><body><script>
const jobs = ${JSON.stringify([...renders, ...icoRenders].map(({ file, px, svg }) => ({ file, px, svg })))};
Promise.all(jobs.map((job) => new Promise((done, fail) => {
  const img = new Image();
  img.onload = () => {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = job.px;
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, job.px, job.px);
    done({ file: job.file, png: canvas.toDataURL('image/png').split(',')[1] });
  };
  img.onerror = () => fail(new Error(job.file));
  img.src = URL.createObjectURL(new Blob([job.svg], { type: 'image/svg+xml' }));
}))).then((out) => {
  const pre = document.createElement('pre');
  pre.id = 'out';
  pre.textContent = JSON.stringify(out);
  document.body.append(pre);
});
</script>`);

const result = spawnSync(browser, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  `--user-data-dir=${join(work, 'profile')}`,
  '--virtual-time-budget=10000',
  '--dump-dom',
  pathToFileURL(page).href,
], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

const match = /<pre id="out">([\s\S]*?)<\/pre>/.exec(result.stdout ?? '');
rmSync(work, { recursive: true, force: true });
if (!match) {
  console.error(`\x1b[31mThe browser rendered nothing.\x1b[0m ${browser}\n${result.stderr ?? ''}`);
  process.exit(1);
}

const decode = (s) => s.replaceAll('&quot;', '"').replaceAll('&amp;', '&');
const pngs = new Map(JSON.parse(decode(match[1])).map(({ file, png }) => [file, Buffer.from(png, 'base64')]));

mkdirSync(appiconset, { recursive: true });
for (const { file } of renders) {
  writeFileSync(join(appiconset, file), pngs.get(file));
  console.log(`  \x1b[32m✎\x1b[0m ${rel(join(appiconset, file))}`);
}

mkdirSync(windowsDir, { recursive: true });
writeFileSync(icoPath, ico(icoRenders.map(({ file, px }) => ({ px, png: pngs.get(file) }))));
console.log(`  \x1b[32m✎\x1b[0m ${rel(icoPath)}`);

for (const [path, content] of outputs) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
  console.log(`  \x1b[32m✎\x1b[0m ${rel(path)}`);
}
console.log(`\x1b[32m✓ App icon rendered with ${browser}.\x1b[0m`);
