// xterm.js, wired to Dray's tokens.
//
// xterm is the terminal, not a starting point for one. Everything a terminal has to get right —
// escape-sequence parsing, reflow, selection, IME, GPU rendering, character widths — is the
// library's, and the addons below are its own. This file resolves the palette, hands keystrokes to
// C#, and stays out of the way.
//
// Loaded on demand: most sessions never open a terminal, and this pulls three quarters of a
// megabyte.

import { tokenColor, definedColors, monoFontStack } from './tokens.js';

let loading = null;

function loadXterm() {
  if (loading) return loading;

  loading = (async () => {
    // Derived from this module's own URL: Dray.Ui is a razor class library, so its assets live
    // under /_content/Dray.Ui/ and not at the app root.
    const base = new URL('../lib/xterm', import.meta.url).href;

    const css = document.createElement('link');
    css.rel = 'stylesheet';
    css.href = `${base}/xterm.css`;
    document.head.appendChild(css);

    // xterm ships UMD bundles that attach to globals, so they load as plain scripts. The core
    // must be present before any addon, hence the sequential await.
    const script = (name) => new Promise((ok, fail) => {
      const el = document.createElement('script');
      el.src = `${base}/${name}`;
      el.onload = ok;
      el.onerror = () => fail(new Error(`Could not load ${name}`));
      document.head.appendChild(el);
    });

    await script('xterm.js');

    // Addons load in parallel; none of them depends on another.
    await Promise.all([
      script('addon-fit.js'),
      script('addon-webgl.js'),
      script('addon-canvas.js'),
      script('addon-web-links.js'),
      script('addon-search.js'),
      script('addon-unicode11.js'),
      script('addon-clipboard.js'),
    ]);

    return self;
  })();

  return loading;
}

function paletteFor() {
  const brand = tokenColor('brand');

  const theme = {
    background: tokenColor('surface'),
    foreground: tokenColor('ink'),
    cursor: brand,
    cursorAccent: tokenColor('surface'),
    selectionBackground: brand && `${brand}44`,

    // The sixteen ANSI colours stay as xterm ships them, deliberately. They are not Dray's palette
    // to choose: programs inside the container pick colours by index expecting the conventional
    // meanings, and remapping red to the brand would make every error message the wrong colour.
  };

  return definedColors(theme);
}

/**
 * Whether this WebView will actually give us a WebGL2 context.
 *
 * Asked before attaching, and it has to be: loading the WebGL addon where the context is refused
 * does not throw. The addon half-initialises, renders nothing, and then throws from its own
 * `dispose()` — which runs inside the terminal's dispose, inside the component's, and took down
 * the whole Blazor circuit with an "unhandled error, needs to reload". WKWebView on macOS refuses
 * it, so this is the normal path there rather than an edge case.
 */
function supportsWebgl() {
  try {
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl2');

    if (!gl) return false;

    // Release it immediately; this was a question, not a renderer.
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return true;
  } catch {
    return false;
  }
}

/**
 * Attach the fastest renderer this WebView will accept: WebGL, else canvas, else xterm's DOM
 * renderer, which always works.
 */
function attachRenderer(term, xterm) {
  if (supportsWebgl()) {
    try {
      const webgl = new xterm.WebglAddon.WebglAddon();

      // The GPU process can still go away later — a sleep, a driver reset. Falling back then is
      // the difference between a terminal that stops painting and one that keeps working.
      webgl.onContextLoss(() => {
        try {
          webgl.dispose();
        } catch {
          // Already half-gone; the canvas renderer below is what matters.
        }

        attachCanvas(term, xterm);
      });

      term.loadAddon(webgl);
      return 'webgl';
    } catch {
      // Fall through.
    }
  }

  return attachCanvas(term, xterm);
}

function attachCanvas(term, xterm) {
  try {
    term.loadAddon(new xterm.CanvasAddon.CanvasAddon());
    return 'canvas';
  } catch {
    return 'dom';
  }
}

export const terminal = {
  /**
   * Create a terminal over `el`.
   *
   * `dotnet` receives OnInput for every keystroke and OnResize whenever the element changes size,
   * so the exec session in C# stays the single owner of the connection.
   */
  async create(el, dotnet) {
    const xterm = await loadXterm();

    const term = new xterm.Terminal({
      fontFamily: monoFontStack(),
      fontSize: 12,
      lineHeight: 1.2,
      theme: paletteFor(),
      cursorBlink: true,

      // Enough history to scroll back through a build, bounded so a runaway process cannot
      // exhaust memory.
      scrollback: 5000,

      // The container's shell owns echo and line editing. Turning them on here would double every
      // keystroke.
      convertEol: false,
      allowProposedApi: true,
    });

    const fit = new xterm.FitAddon.FitAddon();
    const search = new xterm.SearchAddon.SearchAddon();

    term.loadAddon(fit);
    term.loadAddon(search);
    term.loadAddon(new xterm.ClipboardAddon.ClipboardAddon());

    // Unicode 11 has to be activated as well as loaded, or the terminal keeps its default width
    // tables and every line after an emoji is drawn shifted.
    const unicode = new xterm.Unicode11Addon.Unicode11Addon();
    term.loadAddon(unicode);
    term.unicode.activeVersion = '11';

    // Opening a URL is the host's decision, not the page's: on a native head this must reach the
    // system browser rather than navigating the WebView away from the app.
    term.loadAddon(new xterm.WebLinksAddon.WebLinksAddon((event, uri) => {
      event.preventDefault();
      dotnet.invokeMethodAsync('OnLinkActivated', uri);
    }));

    term.open(el);

    // After open(), because the renderers need the element to exist.
    const renderer = attachRenderer(term, xterm);

    const state = { term, fit, search, dotnet, renderer };

    term.onData(data => dotnet.invokeMethodAsync('OnInput', data));

    // Size is measured, not assumed: the remote pty needs the real column count or anything
    // full-screen draws to the wrong width.
    const push = () => {
      try {
        fit.fit();
      } catch {
        // Called while the element is hidden or detached, where there is nothing to measure.
        return;
      }

      dotnet.invokeMethodAsync('OnResize', term.cols, term.rows);
    };

    state.observer = new ResizeObserver(() => push());
    state.observer.observe(el);

    // The first fit has to wait for layout; measuring in the same frame as open() gives the
    // element's pre-layout size.
    requestAnimationFrame(push);

    return state;
  },

  write(state, text) {
    state?.term?.write(text);
  },

  focus(state) {
    state?.term?.focus();
  },

  /** Which renderer actually attached, so the UI can say when it fell back. */
  renderer(state) {
    return state?.renderer ?? 'unknown';
  },

  find(state, needle, forward) {
    if (!state?.search || !needle) return false;

    return forward
      ? state.search.findNext(needle, { incremental: false })
      : state.search.findPrevious(needle, { incremental: false });
  },

  clearSearch(state) {
    state?.search?.clearDecorations();
  },

  /** Re-resolve the palette after the OS appearance changes. */
  retheme(state) {
    if (!state?.term) return;
    state.term.options.theme = paletteFor();
  },

  clear(state) {
    state?.term?.clear();
  },

  dispose(state) {
    state?.observer?.disconnect();

    // Never allowed to throw. This runs from the component's DisposeAsync, and an exception here
    // reaches Blazor as an unhandled error on the circuit — the whole app then offers the user a
    // reload link because a terminal failed to tear down.
    try {
      state?.term?.dispose();
    } catch (error) {
      console.warn('[dray] terminal disposed with an error', error);
    }
  },
};
