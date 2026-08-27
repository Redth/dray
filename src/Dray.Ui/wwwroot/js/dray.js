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

export const logs = {
  /**
   * Follow-tail that yields to the user.
   *
   * The rule: scrolling up detaches following, and returning to the bottom re-attaches it. A log
   * view that keeps yanking you back to the newest line while you are reading something further
   * up is actively hostile, and it is the single most common way this component is got wrong.
   *
   * `onFollowChanged` is invoked whenever that state flips so the UI can offer a way back.
   */
  attach(el, dotnet) {
    if (!el) return null;

    // A few pixels of slack: browsers do not land exactly on scrollHeight, and requiring an exact
    // match means following silently switches itself off.
    const atBottom = () => el.scrollHeight - el.scrollTop - el.clientHeight < 4;

    const state = { following: true, el };

    state.onScroll = () => {
      const nowFollowing = atBottom();
      if (nowFollowing === state.following) return;

      state.following = nowFollowing;
      dotnet.invokeMethodAsync('OnFollowChanged', nowFollowing);
    };

    el.addEventListener('scroll', state.onScroll, { passive: true });
    return state;
  },

  scrollToBottom(state) {
    if (!state?.el) return;
    state.el.scrollTop = state.el.scrollHeight;
  },

  /** Called after each batch of new lines; a no-op while the user is reading history. */
  followIfAttached(state) {
    if (!state?.el || !state.following) return;
    state.el.scrollTop = state.el.scrollHeight;
  },

  detach(state) {
    if (!state?.el || !state.onScroll) return;
    state.el.removeEventListener('scroll', state.onScroll);
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
