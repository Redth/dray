#!/usr/bin/env node
// Builds monaco-yaml and the Compose schema into wwwroot, so the compose editor knows the spec.
//
// What it buys: completion of every key compose accepts, hover documentation for each, and
// validation as you type — `imagee:` underlined before anything runs. That is the YAML language
// server VS Code uses, not something written here; Dray only hands it the schema.
//
// Why a bundling step when the other vendor scripts only copy: monaco-yaml ships ES modules with a
// dozen dependencies, and its worker imports Monaco's own worker runtime. esbuild flattens each into
// one file — the main-thread half as an ES module monaco.js imports, the worker as a classic script
// started the same way Monaco's own workers are. Nothing here runs at runtime but those two files.
//
// The schema is the Compose Specification's own, pinned to a commit and checked by hash. It is the
// one input not in node_modules — no npm package carries a current copy — so it is fetched once
// when the pin changes and verified on every run, and the vendored copy is what ships.
//
//   node build/vendor-monaco-yaml.mjs           rebuild wwwroot/lib/monaco-yaml
//   node build/vendor-monaco-yaml.mjs --check   fail if the vendored copy is stale (CI gate)

import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const modules = join(root, 'node_modules');
const target = join(root, 'src', 'Dray.Ui', 'wwwroot', 'lib', 'monaco-yaml');
const manifestPath = join(target, 'vendored.json');

const check = process.argv.includes('--check');

// The Compose Specification, schema/compose-spec.json at this commit (2026-09-17). Apache-2.0.
// To move the pin: change both lines, run this script, and read the schema's diff before shipping.
const SCHEMA = {
  commit: '914ec15d1fa498969c0df5c1d672306db3256089',
  sha256: 'd61cc3df8c6e6a727043e84f3405c69ffd63d341f629db8480f1d2ee5405b10c',
};

const schemaUrl = `https://raw.githubusercontent.com/compose-spec/compose-spec/${SCHEMA.commit}/schema/compose-spec.json`;

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function hashOf(bytes) {
  return sha256(bytes).slice(0, 16);
}

function versionOf(name) {
  return JSON.parse(readFileSync(join(modules, name, 'package.json'), 'utf8')).version;
}

if (!existsSync(join(modules, 'monaco-yaml')) || !existsSync(join(modules, 'esbuild'))) {
  console.error('monaco-yaml is not installed. Run `npm install` first.');
  process.exit(1);
}

// ---------------------------------------------------------------- bundle

// Minified, but with names kept: a stack trace out of the language server should still say which
// function threw. Deterministic for a given esbuild and input, which is what makes --check work.
const common = {
  bundle: true,
  write: false,
  minify: true,
  keepNames: true,
  legalComments: 'none',
  platform: 'browser',
  target: 'es2022',
  logLevel: 'error',
  absWorkingDir: root,
};

async function bundle() {
  const [main, worker] = await Promise.all([
    // The half that registers providers with the editor. It never imports `monaco-editor` itself —
    // configureMonacoYaml is handed the instance — so Dray's AMD build is the only Monaco there is.
    build({ ...common, entryPoints: [join(modules, 'monaco-yaml', 'index.js')], format: 'esm' }),

    // The language server, plus the slice of Monaco's worker runtime it runs on. A classic script,
    // because monaco.js starts workers from a same-origin blob with importScripts.
    build({ ...common, entryPoints: [join(modules, 'monaco-yaml', 'yaml.worker.js')], format: 'iife' }),
  ]);

  return {
    'monaco-yaml.js': Buffer.from(main.outputFiles[0].contents),
    'yaml.worker.js': Buffer.from(worker.outputFiles[0].contents),
  };
}

// ---------------------------------------------------------------- schema

async function schema() {
  const vendored = join(target, 'compose-spec.json');

  // Already have the pinned bytes: no network. A CI check never fetches.
  if (existsSync(vendored) && sha256(readFileSync(vendored)) === SCHEMA.sha256) return readFileSync(vendored);

  if (check) return null;

  const response = await fetch(schemaUrl);
  if (!response.ok) throw new Error(`Could not fetch the Compose schema: ${response.status} ${schemaUrl}`);

  const bytes = Buffer.from(await response.arrayBuffer());
  const actual = sha256(bytes);

  if (actual !== SCHEMA.sha256) {
    throw new Error(`The Compose schema at ${SCHEMA.commit} hashed to ${actual}, not the pinned ${SCHEMA.sha256}.`);
  }

  return bytes;
}

// ---------------------------------------------------------------- run

const bundled = await bundle();
const spec = await schema();

const files = { ...bundled };
if (spec) files['compose-spec.json'] = spec;

const manifest = {
  note: 'Generated by build/vendor-monaco-yaml.mjs. Do not edit, and do not edit the files it lists.',
  'monaco-yaml': versionOf('monaco-yaml'),
  esbuild: versionOf('esbuild'),
  schema: { source: schemaUrl, sha256: SCHEMA.sha256 },
  files: Object.fromEntries(Object.entries(files).map(([name, bytes]) => [name, hashOf(bytes)])),
};

if (check) {
  const problems = [];

  if (!existsSync(manifestPath)) {
    problems.push('wwwroot/lib/monaco-yaml has never been vendored.');
  } else {
    const current = JSON.parse(readFileSync(manifestPath, 'utf8'));

    if (current['monaco-yaml'] !== manifest['monaco-yaml']) {
      problems.push(`vendored monaco-yaml is ${current['monaco-yaml']}, package.json wants ${manifest['monaco-yaml']}.`);
    }

    if (current.schema?.sha256 !== SCHEMA.sha256) problems.push('the Compose schema pin moved; rebuild to fetch it.');
    if (!spec) problems.push('compose-spec.json is missing or does not match the pinned hash.');

    for (const [file, hash] of Object.entries(manifest.files)) {
      const path = join(target, file);

      if (!existsSync(path)) problems.push(`missing: ${file}`);
      else if (hashOf(readFileSync(path)) !== hash) problems.push(`stale or modified: ${file}`);
    }
  }

  if (problems.length > 0) {
    console.error('\x1b[31m\x1b[1m✗ Vendored monaco-yaml is stale\x1b[0m\n');
    for (const problem of problems) console.error(`  ${problem}`);
    console.error('\n  Run: npm run vendor:monaco-yaml');
    process.exit(1);
  }

  console.log(`\x1b[32m\x1b[1m✓ monaco-yaml ${manifest['monaco-yaml']} vendored and current\x1b[0m — Compose schema ${SCHEMA.commit.slice(0, 7)}.`);
  process.exit(0);
}

rmSync(target, { recursive: true, force: true });
mkdirSync(target, { recursive: true });

for (const [name, bytes] of Object.entries(files)) writeFileSync(join(target, name), bytes);

// Licences travel with the code: monaco-yaml is MIT, the Compose Specification Apache-2.0.
cpSync(join(modules, 'monaco-yaml', 'LICENSE.md'), join(target, 'LICENSE-monaco-yaml.md'));
writeFileSync(
  join(target, 'LICENSE-compose-spec.txt'),
  'compose-spec.json is from https://github.com/compose-spec/compose-spec, licensed under the\n'
    + 'Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0\n',
);

writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

const bytes = Object.keys(files).reduce((sum, f) => sum + statSync(join(target, f)).size, 0);

console.log(`\x1b[32m\x1b[1m✓ Vendored monaco-yaml ${manifest['monaco-yaml']}\x1b[0m`);
console.log(`  ${Object.keys(files).length} files, ${(bytes / 1024 / 1024).toFixed(1)} MB → wwwroot/lib/monaco-yaml`);
console.log(`  Compose schema pinned at ${SCHEMA.commit.slice(0, 7)}`);
