// Dray browser interop. Vendored, tiny, and the only JS the app ships.
// Everything here is behaviour the platform gives us but Blazor cannot reach from C#.

export const dialog = {
  /**
   * showModal() is what buys focus trapping, focus restore, Escape-to-close and the top layer.
   * Hand-rolling any of those is how Sherpa ended up intercepting every Tab keypress.
   */
  show(el) {
    if (el && !el.open) el.showModal();
  },

  close(el) {
    if (el && el.open) el.close();
  },
};

export const theme = {
  /**
   * Push the platform's resolved window colours into the token layer before first paint.
   *
   * The WebView is transparent over the native window, so `--bg` must match what AppKit / WinUI /
   * GTK actually painted, not our fallback. DESIGN.md section 9.1: the seam is invisible only if
   * these agree exactly.
   *
   * @param {"light"|"dark"} mode
   * @param {Record<string,string>} overrides token name (without `--`) to CSS colour
   */
  apply(mode, overrides) {
    const root = document.documentElement;
    root.setAttribute('data-theme', mode);

    for (const [name, value] of Object.entries(overrides ?? {})) {
      if (value) root.style.setProperty(`--${name}`, value);
    }
  },

  /** Follow the OS again, discarding any explicit choice. */
  clear() {
    document.documentElement.removeAttribute('data-theme');
  },
};

export const clipboard = {
  async write(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Clipboard access is gated in some WebView configurations; the host falls back to native.
      return false;
    }
  },
};
