// Shared colour maths for the Dray token pipeline.
// OKLCH -> linear sRGB -> gamma sRGB, plus WCAG relative luminance and contrast.
// No dependencies: this runs in CI with a bare Node install.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

export const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
export const tokensPath = join(repoRoot, 'design', 'tokens.json');

export function loadTokens() {
  return JSON.parse(readFileSync(tokensPath, 'utf8'));
}

/** OKLCH [L, C, H] -> gamma-encoded sRGB channels in 0..1 (may be out of gamut). */
export function oklchToSrgb([L, C, H]) {
  const h = (H * Math.PI) / 180;
  const a = C * Math.cos(h);
  const b = C * Math.sin(h);

  const l_ = L + 0.3963377774 * a + 0.2158037573 * b;
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b;
  const s_ = L - 0.0894841775 * a - 1.2914855480 * b;

  const l = l_ ** 3;
  const m = m_ ** 3;
  const s = s_ ** 3;

  const lr = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
  const lg = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
  const lb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

  return [gamma(lr), gamma(lg), gamma(lb)];
}

const gamma = (v) => (v <= 0.0031308 ? 12.92 * v : 1.055 * Math.pow(Math.max(v, 0), 1 / 2.4) - 0.055);
const clamp01 = (v) => Math.min(1, Math.max(0, v));

/** True when the OKLCH value falls outside sRGB — a silent clip we want to catch at build time. */
export function isOutOfGamut(oklch, epsilon = 0.001) {
  return oklchToSrgb(oklch).some((c) => c < -epsilon || c > 1 + epsilon);
}

export function toHex(oklch) {
  return (
    '#' +
    oklchToSrgb(oklch)
      .map((c) => Math.round(clamp01(c) * 255).toString(16).padStart(2, '0'))
      .join('')
  );
}

export function toCss([L, C, H]) {
  return `oklch(${L.toFixed(3)} ${C.toFixed(3)} ${H})`;
}

const linearize = (v) => {
  const c = clamp01(v);
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
};

export function relativeLuminance(oklch) {
  const [r, g, b] = oklchToSrgb(oklch).map(linearize);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrastRatio(a, b) {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  const [hi, lo] = la > lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * Mix `amount` of `a` into `b`, in OKLCH. Used for the dark-mode pill tints.
 *
 * Hue is an angle, and it needs two things lightness and chroma do not.
 *
 * A grey has no hue. Its H is written as 0 because the field has to hold something, not because
 * the colour is red — so interpolating towards it drags the result round the wheel towards red.
 * That is how the dark `--ok-tint` ended up at hue 27: 18% of moss's 152, mixed with a grey whose
 * hue meant nothing. A green pill on a red ground, and a success banner that looked like a
 * failure. So a chroma-free end contributes no hue at all; the other end's is kept.
 *
 * And where both ends do have a hue, the mix takes the short way round the wheel. Straight
 * interpolation from 350 to 10 passes through every hue in between — the long way, through green.
 */
export function mix(a, b, amount) {
  const grey = 0.002;

  const hue = () => {
    if (a[1] < grey && b[1] < grey) return b[2];
    if (a[1] < grey) return b[2];
    if (b[1] < grey) return a[2];

    let delta = ((a[2] - b[2] + 540) % 360) - 180;
    return (b[2] + delta * amount + 360) % 360;
  };

  return [
    b[0] + (a[0] - b[0]) * amount,
    b[1] + (a[1] - b[1]) * amount,
    hue(),
  ];
}

/**
 * Resolve one theme's semantic map into { role: [L,C,H] }.
 * A role is either a primitive name or a { mix, into, amount } expression.
 */
export function resolveTheme(tokens, theme) {
  const prims = tokens.primitives;
  const lookup = (name) => {
    const v = prims[name];
    if (!v) throw new Error(`tokens.json: unknown primitive "${name}"`);
    return v;
  };

  const out = {};
  for (const [role, value] of Object.entries(tokens.semantic[theme])) {
    if (role.startsWith('$')) continue;
    if (typeof value === 'string') {
      out[role] = lookup(value);
    } else if (value && typeof value === 'object' && value.mix) {
      out[role] = mix(lookup(value.mix), lookup(value.into), value.amount);
    } else {
      throw new Error(`tokens.json: role "${role}" in "${theme}" is neither a primitive ref nor a mix`);
    }
  }
  return out;
}

export const themes = ['light', 'dark'];
