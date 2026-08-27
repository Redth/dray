#!/usr/bin/env node
// Recomputes every contrast pair declared in design/tokens.json and fails on regression.
// DESIGN.md section 11, gate 1. Runs in CI.
//
//   node build/verify-contrast.mjs          report + exit code
//   node build/verify-contrast.mjs --quiet   only failures

import { loadTokens, resolveTheme, contrastRatio, isOutOfGamut, toHex, themes } from './color.mjs';

const quiet = process.argv.includes('--quiet');
const tokens = loadTokens();
const failures = [];
const notes = [];

const fmt = (n) => n.toFixed(2).padStart(6);

for (const theme of themes) {
  const roles = resolveTheme(tokens, theme);
  const exceptions = Object.entries(tokens.contrast.exceptions)
    .filter(([k]) => !k.startsWith('$'))
    .filter(([, e]) => e.applies === theme || e.applies === 'light+dark');
  const excepted = new Set(exceptions.map(([, e]) => e.rule));

  if (!quiet) console.log(`\n\x1b[1m${theme}\x1b[0m`);

  // --- gamut: an out-of-range OKLCH value clips silently and ships a wrong colour ---
  for (const [role, oklch] of Object.entries(roles)) {
    if (isOutOfGamut(oklch)) {
      failures.push(`${theme}: role "${role}" ${JSON.stringify(oklch)} is outside sRGB and will clip to ${toHex(oklch)}`);
    }
  }

  // --- WCAG rules ---
  for (const rule of tokens.contrast.rules) {
    const fg = roles[rule.fg];
    const bg = roles[rule.bg];
    if (!fg || !bg) {
      failures.push(`${theme}: rule "${rule.label}" references an unknown role (${rule.fg} / ${rule.bg})`);
      continue;
    }
    const ratio = contrastRatio(fg, bg);
    const key = `${rule.fg} on ${rule.bg}`;
    const isExcepted = excepted.has(key);
    const pass = ratio >= rule.min;

    if (isExcepted) {
      // A stale exception is as dangerous as a missing one.
      if (pass) failures.push(`${theme}: exception declared for "${key}" but it now passes (${fmt(ratio)} >= ${rule.min}). Delete the exception.`);
      else notes.push(`${theme}: ${key} — ${fmt(ratio)} (excepted)`);
      if (!quiet) console.log(`  ${fmt(ratio)}  \x1b[33mEXCEPT\x1b[0m  ${key.padEnd(30)} ${rule.label}`);
      continue;
    }

    if (!pass) failures.push(`${theme}: ${key} is ${fmt(ratio).trim()}:1, needs ${rule.min}:1 — ${rule.label}`);
    if (!quiet) {
      const tag = pass ? '\x1b[32m  PASS\x1b[0m' : '\x1b[31m  FAIL\x1b[0m';
      console.log(`  ${fmt(ratio)}${tag}  ${key.padEnd(30)} ${rule.label}`);
    }
  }

  // --- separation rules: satisfied by luminance ratio OR hue distance ---
  const minHueDelta = tokens.contrast.separation.minHueDelta ?? 60;
  for (const rule of tokens.contrast.separation.rules) {
    const a = roles[rule.a];
    const b = roles[rule.b];
    const ratio = contrastRatio(a, b);
    const hueDelta = hueDistance(a[2], b[2]);
    const key = `separation ${rule.a} vs ${rule.b}`;
    const isExcepted = excepted.has(key);
    const byHue = hueDelta >= minHueDelta;
    const pass = ratio >= rule.min || byHue;
    const how = byHue ? `${Math.round(hueDelta)}\u00b0 hue` : `${fmt(ratio).trim()}x lum`;

    if (isExcepted) {
      if (pass) failures.push(`${theme}: exception declared for "${key}" but it now passes (${fmt(ratio)} >= ${rule.min}). Delete the exception.`);
      else notes.push(`${theme}: ${key} — ${fmt(ratio)} (excepted)`);
      if (!quiet) console.log(`  ${fmt(ratio)}  \x1b[33mEXCEPT\x1b[0m  ${key.padEnd(30)} ${rule.label}`);
      continue;
    }

    if (!pass) {
      failures.push(
        `${theme}: ${key} — luminance ${fmt(ratio).trim()} (needs ${rule.min}) and hue ${Math.round(hueDelta)}\u00b0 ` +
        `(needs ${minHueDelta}\u00b0). ${rule.label}`
      );
    }
    if (!quiet) {
      const tag = pass ? '\x1b[32m  PASS\x1b[0m' : '\x1b[31m  FAIL\x1b[0m';
      console.log(`  ${fmt(ratio)}${tag}  ${key.padEnd(30)} ${rule.label} \x1b[2m(${how})\x1b[0m`);
    }
  }
}

/** Shortest angular distance between two hues, 0..180. */
function hueDistance(h1, h2) {
  const d = Math.abs(((h1 - h2) % 360 + 360) % 360);
  return d > 180 ? 360 - d : d;
}

if (notes.length && !quiet) {
  console.log('\n\x1b[1mDeclared exceptions\x1b[0m');
  for (const [name, e] of Object.entries(tokens.contrast.exceptions)) {
    if (name.startsWith('$')) continue;
    console.log(`  \x1b[33m${name}\x1b[0m (${e.applies})`);
    console.log(`    why:     ${e.reason}`);
    console.log(`    control: ${e.control}`);
  }
}

if (failures.length) {
  console.error(`\n\x1b[31m\x1b[1mContrast check failed — ${failures.length} problem(s):\x1b[0m`);
  for (const f of failures) console.error(`  · ${f}`);
  console.error('\nFix the token in design/tokens.json, or add a reasoned exception with a compensating control.');
  process.exit(1);
}

console.log(`\n\x1b[32m\x1b[1m✓ Contrast OK\x1b[0m — ${tokens.contrast.rules.length * themes.length} pairs, ${notes.length} declared exception(s).`);
