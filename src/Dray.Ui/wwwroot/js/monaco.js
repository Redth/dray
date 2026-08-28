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
      require(['vs/editor/editor.main'], () => resolve(self.monaco), reject);
    };

    script.onerror = () => reject(new Error('Could not load the editor.'));
    document.head.appendChild(script);
  });

  return loading;
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
  async create(el, dotnet, { text, language, readOnly }) {
    const monaco = await loadMonaco();
    defineTheme(monaco);

    const instance = monaco.editor.create(el, {
      value: text,
      language,
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

    const state = { instance, monaco, dotnet, clean: text };

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

  focus(state) {
    state?.instance?.focus();
  },

  dispose(state) {
    state?.instance?.dispose();
  },
};
