// Reading design tokens from JavaScript.
//
// Monaco and xterm both need concrete colours — neither accepts a `var()` — so both have to resolve
// tokens at runtime. This is the one place that knows how.

/**
 * Resolve a design token to `#rrggbb`, or undefined if it cannot be read.
 *
 * The conversion is the whole point. Tokens are authored in OKLCH, and every obvious way of asking
 * the browser to normalise one gives it straight back: `getComputedStyle` preserves the colour
 * space, and a canvas `fillStyle` round-trip hands the same OKLCH string straight back. Code that
 * assumed otherwise silently produced no colour at all, and both libraries fell back to their own
 * themes — which is why the terminal was black in a light window.
 *
 * Painting one pixel and reading it back is what actually forces sRGB, because the canvas bitmap
 * has no choice but to hold real channel values. It also handles the platform overrides, which on
 * macOS arrive from AppKit as space-separated sRGB with alpha rather than as hex.
 *
 * Returns undefined rather than a fallback colour on purpose: a hex literal here would be a second,
 * invisible source of truth for something that lives in design/tokens.json, and it would go stale
 * without anyone noticing. A missing token means the library keeps its own default — a slightly
 * generic component rather than a wrong one.
 */
export function tokenColor(name) {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(`--${name}`).trim();
  if (!raw) return undefined;

  if (/^#[0-9a-f]{6}$/i.test(raw)) return raw;

  const canvas = document.createElement('canvas');
  canvas.width = 1;
  canvas.height = 1;

  const context = canvas.getContext('2d', { willReadFrequently: true });
  if (!context) return undefined;

  // A colour the browser refuses to parse leaves fillStyle at its previous value, so the sentinel
  // below doubles as the failure signal.
  context.fillStyle = '#ff00ff'; // design-lint-ok: a sentinel for an unparseable colour, never painted
  context.fillStyle = raw;

  // Cleared first: the token may be translucent, and compositing it over a stale pixel would give
  // a colour that is in neither the token nor the design.
  context.clearRect(0, 0, 1, 1);
  context.fillRect(0, 0, 1, 1);

  let pixel;

  try {
    pixel = context.getImageData(0, 0, 1, 1).data;
  } catch {
    // Blocked in some hardened WebView configurations.
    return undefined;
  }

  // Fully transparent means nothing was painted, which means the colour did not parse.
  if (pixel[3] === 0) return undefined;

  const hex = (v) => v.toString(16).padStart(2, '0');
  return `#${hex(pixel[0])}${hex(pixel[1])}${hex(pixel[2])}`;
}

/** Build a colour map for a library's theme, dropping every entry whose token did not resolve. */
export function definedColors(entries) {
  return Object.fromEntries(Object.entries(entries).filter(([, value]) => value !== undefined));
}

/**
 * The app's monospace stack, measured rather than restated.
 *
 * app.css owns it; copying the list into JS would mean two places to change and one of them would
 * eventually be forgotten.
 */
export function monoFontStack() {
  const probe = document.createElement('span');
  probe.className = 'mono';
  probe.style.position = 'absolute';
  probe.style.visibility = 'hidden';

  document.body.appendChild(probe);
  const stack = getComputedStyle(probe).fontFamily;
  probe.remove();

  return stack || 'ui-monospace, monospace';
}

/**
 * Which theme is actually on screen.
 *
 * Read from the document rather than passed in from C#, because that is the same source the CSS
 * uses: the native heads stamp data-theme, and the dev host has no stamp at all and follows
 * prefers-color-scheme.
 */
export function currentMode() {
  const stamped = document.documentElement.getAttribute('data-theme');
  if (stamped === 'dark' || stamped === 'light') return stamped;

  return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}
