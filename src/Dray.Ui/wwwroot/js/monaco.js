// Monaco, wired to Dray's tokens.
//
// Loaded on demand: the editor is 5 MB and most sessions never open a file, so nothing here runs
// until someone actually opens one.

import { tokenColor, definedColors, monoFontStack, currentMode } from './tokens.js';

let loading = null;

/**
 * Load the AMD bundle once and hand back `monaco`.
 *
 * Monaco ships an AMD build, which wants its own loader and a `require` of its own. That is
 * confined to this function so nothing else in Dray has to know about it.
 */
function loadMonaco() {
  if (loading) return loading;

  loading = new Promise((resolve, reject) => {
    // Derived from this module's own URL rather than written out. Dray.Ui is a razor class
    // library, so its assets are served from /_content/Dray.Ui/ and not from the app root — and
    // the package name is the kind of thing that gets renamed once and breaks silently.
    const base = new URL('../lib/monaco/vs', import.meta.url).href;

    // Language services run in workers. They are same-origin here, but the macOS WebView serves
    // from a custom scheme where a direct worker URL is refused — so the worker is started from a
    // tiny same-document blob that immediately imports the real one. This is Monaco's own
    // documented workaround, not a trick.
    self.MonacoEnvironment = {
      getWorkerUrl(_moduleId, label) {
        const worker = label === 'json' ? 'json.worker' : 'editor.worker';
        const url = `${base}/assets/`;

        return URL.createObjectURL(new Blob([
          `self.MonacoEnvironment = { baseUrl: ${JSON.stringify(url)} };`,
          `importScripts(${JSON.stringify(`${url}${worker}.js`)});`,
        ], { type: 'text/javascript' }));
      },
    };

    const script = document.createElement('script');
    script.src = `${base}/loader.js`;

    script.onload = () => {
      // eslint-disable-next-line no-undef
      require.config({ paths: { vs: base } });
      // eslint-disable-next-line no-undef
      require(['vs/editor/editor.main'], () => {
        followAppearance(self.monaco);
        resolve(self.monaco);
      }, reject);
    };

    script.onerror = () => reject(new Error('Could not load the editor.'));
    document.head.appendChild(script);
  });

  return loading;
}

// ---------------------------------------------------------------- compose

/**
 * Where a compose file's model lives. The YAML language server picks a schema by matching the
 * model's URI, so every compose editor gets a URI under this path and nothing else does — a
 * docker-compose.yml opened out of a container is a compose file, but a values.yaml is not.
 */
const COMPOSE_ROOT = '/__dray-compose/';

let yamlLoading = null;
let modelCount = 0;

/**
 * Turn on the YAML language server for compose files, once: completion of every key the Compose
 * Specification defines, hover documentation for each, and validation as you type.
 *
 * monaco-yaml is the language server VS Code's YAML extension runs. It is bundled at build time by
 * build/vendor-monaco-yaml.mjs, along with the specification's own schema pinned by hash, so this
 * works with the network unplugged like everything else.
 */
function configureYaml(monaco) {
  if (yamlLoading) return yamlLoading;

  const lib = new URL('../lib/monaco-yaml/', import.meta.url).href;

  yamlLoading = (async () => {
    const [{ configureMonacoYaml }, schema] = await Promise.all([
      import(`${lib}monaco-yaml.js`),
      fetch(`${lib}compose-spec.json`).then((r) => r.json()),
    ]);

    // monaco-yaml asks Monaco to start its worker by name, the way Monaco did before 0.55. Monaco
    // 0.55 wants the Worker itself, and a two-message handshake — "ignore", then the options —
    // that its own JSON service performs in vs/common/workers.js. This does the same, and is the
    // only adaptation: the provider code and the language server are untouched.
    const editor = Object.create(monaco.editor, {
      createWebWorker: {
        value: (options) => {
          const worker = new Worker(URL.createObjectURL(new Blob(
            [`importScripts(${JSON.stringify(`${lib}yaml.worker.js`)});`],
            { type: 'text/javascript' },
          )), { name: options.label });

          worker.postMessage('ignore');
          worker.postMessage(options.createData);

          return monaco.editor.createWebWorker({ worker, host: options.host, keepIdleModels: options.keepIdleModels });
        },
      },
    });

    configureMonacoYaml(Object.create(monaco, { editor: { value: editor } }), {
      // The schema is handed over rather than fetched by the server: nothing here may reach for
      // the network, and a $ref to a remote schema would do exactly that.
      enableSchemaRequest: false,
      hover: true,
      completion: true,
      validate: true,
      format: true,
      schemas: [{ uri: 'https://dray.local/schemas/compose-spec.json', fileMatch: [`**${COMPOSE_ROOT}**`], schema }],
    });
  })().catch((error) => {
    // A compose file still opens and saves without the language server; it just knows less.
    console.error('Dray: the YAML language server did not start.', error);
  });

  return yamlLoading;
}

const SEVERITY = { error: 8, warning: 4 }; // monaco.MarkerSeverity, which is not loaded at import time

/**
 * Re-resolve the palette whenever the page's appearance changes, however it changed.
 *
 * The stylesheet follows the system appearance by itself, through media queries; Monaco cannot,
 * because its theme is colours resolved once into JavaScript. A head that reports the change
 * (IPlatformTheme on macOS) already calls `retheme`, but the web head's theme never changes and so
 * never reports — and an editor left dark in a light window is the result. Watching the two
 * things the stylesheet itself keys on covers every head. Monaco's theme is global, so one
 * listener serves every editor on the page.
 */
function followAppearance(monaco) {
  const refresh = () => defineTheme(monaco);

  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', refresh);
  new MutationObserver(refresh).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
}

function defineTheme(monaco) {
  const mode = currentMode();
  const surface = tokenColor('surface');
  const line = tokenColor('line');
  const brand = tokenColor('brand');
  const ink = tokenColor('ink');

  monaco.editor.defineTheme('dray', {
    // Inheriting gives us a complete token map for syntax; only the chrome is Dray's.
    base: mode === 'dark' ? 'vs-dark' : 'vs',
    inherit: true,
    rules: [],
    colors: definedColors({
      'editor.background': surface,
      'editor.foreground': ink,
      'editorLineNumber.foreground': tokenColor('muted'),
      'editorLineNumber.activeForeground': ink,
      'editorCursor.foreground': brand,

      // Monaco accepts #rrggbbaa, so the brand doubles as the selection without needing a tint
      // token that nothing else in the app would use.
      'editor.selectionBackground': brand && `${brand}33`,

      'editorIndentGuide.background1': line,
      'editorWidget.background': surface,
      'editorWidget.border': line,
      'editorGutter.background': surface,
      'scrollbarSlider.background': tokenColor('line-strong') && `${tokenColor('line-strong')}66`,
      focusBorder: tokenColor('focus'),

      // Squiggles in the app's own error and warning colours, so a compose problem reads as the
      // same problem it is in the banner above the editor.
      'editorError.foreground': tokenColor('danger'),
      'editorWarning.foreground': tokenColor('warn'),
      'editorHoverWidget.background': surface,
      'editorHoverWidget.border': line,
      'editorSuggestWidget.background': surface,
      'editorSuggestWidget.border': line,
      'editorSuggestWidget.selectedBackground': brand && `${brand}22`,

      // The review diff in the app's own ok and danger hues, at a strength that tints the text
      // rather than drowning it — Monaco's defaults are a saturated green and red.
      'diffEditor.insertedLineBackground': tokenColor('ok') && `${tokenColor('ok')}1f`,
      'diffEditor.insertedTextBackground': tokenColor('ok') && `${tokenColor('ok')}33`,
      'diffEditor.removedLineBackground': tokenColor('danger') && `${tokenColor('danger')}1f`,
      'diffEditor.removedTextBackground': tokenColor('danger') && `${tokenColor('danger')}33`,
    }),
  });

  monaco.editor.setTheme('dray');
}

export const editor = {
  /**
   * Create an editor over `el`.
   *
   * `dotnet` receives OnDirtyChanged as the user types and OnSave for the save shortcut, so
   * unsaved state lives in C# and survives the component re-rendering.
   */
  async create(el, dotnet, { text, language, readOnly, schema, name }) {
    const monaco = await loadMonaco();
    defineTheme(monaco);

    // A compose file gets a model of its own, under the path the language server matches. Started
    // before the editor and not awaited: the editor is usable at once, and completion arrives
    // with the worker a moment later.
    let model = null;

    if (schema === 'compose') {
      configureYaml(monaco);

      const uri = monaco.Uri.file(`${COMPOSE_ROOT}${++modelCount}/${name || 'compose.yaml'}`);
      model = monaco.editor.createModel(text, 'yaml', uri);
    }

    const instance = monaco.editor.create(el, {
      ...(model ? { model } : { value: text, language }),
      readOnly,

      // A config file is read as much as it is written, and Dray's own type scale is the one the
      // rest of the app uses.
      fontSize: 12,
      // The same stack app.css gives .mono, read off a real element rather than duplicated here,
      // so the editor cannot drift from the rest of the app's monospace text.
      fontFamily: monoFontStack(),
      lineHeight: 1.6,

      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      renderLineHighlight: 'all',
      smoothScrolling: true,
      automaticLayout: true,
      tabSize: 2,

      // Whitespace matters in the formats most often edited here, and a stray trailing space in a
      // YAML file is worth being able to see.
      renderWhitespace: 'selection',
      scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
    });

    const state = { instance, monaco, dotnet, model, clean: text };

    instance.onDidChangeModelContent(() => {
      const dirty = instance.getValue() !== state.clean;
      if (dirty === state.dirty) return;

      state.dirty = dirty;
      dotnet.invokeMethodAsync('OnDirtyChanged', dirty);
    });

    // Cmd/Ctrl+S saves. The editor swallows the keystroke either way, so not wiring it would mean
    // the most reflexive shortcut in existence silently doing nothing.
    instance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      dotnet.invokeMethodAsync('OnSaveRequested');
    });

    return state;
  },

  getValue(state) {
    return state?.instance?.getValue() ?? '';
  },

  /** The text as last loaded or saved — what is on disk, as far as this editor knows. */
  getClean(state) {
    return state?.clean ?? '';
  },

  /** Called after a successful save: the current text becomes the new clean baseline. */
  markClean(state) {
    if (!state?.instance) return;

    state.clean = state.instance.getValue();
    state.dirty = false;
  },

  /** Reload with different content — a different file, or a discarded edit. */
  setValue(state, text, language) {
    if (!state?.instance) return;

    const model = state.instance.getModel();
    state.monaco.editor.setModelLanguage(model, language);

    // pushEditOperations rather than setValue: setValue clears the undo stack, and discarding an
    // edit should be undoable like anything else.
    model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null);
    state.instance.setScrollPosition({ scrollTop: 0 });

    state.clean = text;
    state.dirty = false;
  },

  /** Re-resolve the palette after the OS appearance changes. */
  retheme(state) {
    if (state?.monaco) defineTheme(state.monaco);
  },

  /**
   * Annotate every ${VAR} with what it will actually resolve to.
   *
   * Monaco's `after` content is the inlay-hint mechanism: it draws text that is not in the model,
   * so nothing here can be selected, copied, or saved into the file. That matters — the annotation
   * must never become part of what compose reads.
   *
   * `annotations` is [{ line, column, length, text, kind }] with 1-based positions, computed in C#
   * by ComposeInterpolation so the rules live in one tested place rather than being re-implemented
   * in JavaScript against the same spec.
   */
  annotate(state, annotations) {
    if (!state?.instance) return;

    const monaco = state.monaco;
    const model = state.instance.getModel();
    if (!model) return;

    const decorations = (annotations ?? []).map((a) => ({
      range: new monaco.Range(a.line, a.column, a.line, a.column + a.length),
      options: {
        // Marks the reference itself, so it is visibly a thing that gets replaced rather than
        // literal text.
        inlineClassName: `mc-sub mc-sub--${a.kind}`,

        after: {
          content: a.text,
          inlineClassName: `mc-hint mc-hint--${a.kind}`,
        },

        // A problem is worth finding from the scrollbar without reading the file. Monaco needs a
        // real colour rather than a CSS variable, so it comes from the same token resolver the
        // theme uses — the palette stays in tokens.json either way.
        overviewRuler: a.kind === 'ok' || a.kind === 'default'
          ? undefined
          : {
              color: tokenColor(a.kind === 'required' ? 'danger' : 'warn'),
              position: monaco.editor.OverviewRulerLane.Right,
            },

        hoverMessage: a.hover ? { value: a.hover } : undefined,
      },
    }));

    // The collection replaces its own previous decorations, so re-annotating on every edit does
    // not stack them up.
    state.subs = state.instance.createDecorationsCollection
      ? (state.subs
          ? (state.subs.set(decorations), state.subs)
          : state.instance.createDecorationsCollection(decorations))
      : state.instance.deltaDecorations(state.subs ?? [], decorations);
  },

  /**
   * Show what compose objected to, at the place it objected.
   *
   * Markers rather than decorations: they are what Monaco's own language services report through,
   * so they get the squiggle, the gutter, the scrollbar mark, the hover and F8 to jump between
   * them — and they sit alongside the YAML server's own without either replacing the other.
   */
  problems(state, problems) {
    const model = state?.instance?.getModel();
    if (!model) return;

    state.monaco.editor.setModelMarkers(model, 'dray', (problems ?? []).map((p) => ({
      startLineNumber: p.line,
      startColumn: p.column,
      endLineNumber: p.endLine,
      endColumn: p.endColumn,
      message: p.message,
      severity: SEVERITY[p.severity] ?? SEVERITY.error,
      source: p.source,
    })));
  },

  /**
   * Insert lines before a 1-based line, as one undoable edit — how Dray's quick actions change a
   * file. An edit rather than a replacement, so the undo stack, the cursor and every other line are
   * left exactly as they were; the inserted lines are selected so it is obvious what arrived.
   */
  insert(state, line, text) {
    const model = state?.instance?.getModel();
    if (!model) return;

    const monaco = state.monaco;
    const last = model.getLineCount();
    let range;
    let insert = text;

    if (line <= last) {
      range = new monaco.Range(line, 1, line, 1);
    } else {
      // Past the end of a file with no trailing newline: start a line of our own first.
      const column = model.getLineMaxColumn(last);
      range = new monaco.Range(last, column, last, column);
      if (model.getLineContent(last).length > 0) insert = model.getEOL() + text;
    }

    state.instance.pushUndoStop();
    state.instance.executeEdits('dray', [{ range, text: insert, forceMoveMarkers: true }]);
    state.instance.pushUndoStop();

    const added = text.split('\n').length - 1;
    const first = Math.min(line, model.getLineCount());
    state.instance.setSelection(new monaco.Range(first, 1, first + Math.max(added - 1, 0), model.getLineMaxColumn(first + Math.max(added - 1, 0))));
    state.instance.revealLinesInCenterIfOutsideViewport(first, first + added);
    state.instance.focus();
  },

  /** Put the cursor on a line and bring it into view — following a problem from the list. */
  reveal(state, line, column) {
    if (!state?.instance) return;

    state.instance.setPosition({ lineNumber: line, column: column || 1 });
    state.instance.revealLineInCenterIfOutsideViewport(line);
    state.instance.focus();
  },

  focus(state) {
    state?.instance?.focus();
  },

  dispose(state) {
    state?.instance?.dispose();

    // Created explicitly, so not disposed with the editor. Left behind, it would hold the URI and
    // its markers for the life of the page.
    state?.model?.dispose();
  },
};

/**
 * Monaco's own diff editor, read-only: what is on disk against what is about to replace it.
 *
 * Side by side when there is room and inline when there is not — Monaco decides, by the width it
 * is given. The models are created here and disposed with the editor, since a diff editor does not
 * own the models it is handed.
 */
export const diff = {
  async create(el, { original, modified, language }) {
    const monaco = await loadMonaco();
    defineTheme(monaco);

    const models = {
      original: monaco.editor.createModel(original, language),
      modified: monaco.editor.createModel(modified, language),
    };

    const instance = monaco.editor.createDiffEditor(el, {
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      renderSideBySide: true,
      useInlineViewWhenSpaceIsLimited: true,
      renderOverviewRuler: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 12,
      fontFamily: monoFontStack(),
      lineHeight: 1.6,
      scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
    });

    instance.setModel(models);
    return { instance, models };
  },

  dispose(state) {
    state?.instance?.dispose();
    state?.models?.original?.dispose();
    state?.models?.modified?.dispose();
  },
};
