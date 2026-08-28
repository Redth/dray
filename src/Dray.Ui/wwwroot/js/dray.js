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

export const menu = {
  /**
   * Keep a popover under the control that opens it.
   *
   * The popover is in the top layer, which is the whole point — a menu inside a card would
   * otherwise be clipped by the card's own rounded corners. The top layer does not inherit the
   * trigger's position though, and WebKit has no anchor positioning, so the placement is done here
   * on each open: aligned to the trigger's right edge, flipped above it when there is no room
   * below, and always kept on screen.
   */
  attach(trigger, panel) {
    if (!trigger || !panel) return null;

    const place = () => {
      const t = trigger.getBoundingClientRect();
      const p = panel.getBoundingClientRect();

      const top = t.bottom + 4 + p.height > window.innerHeight - 8
        ? Math.max(8, t.top - p.height - 4)
        : t.bottom + 4;

      const left = Math.min(
        Math.max(8, t.right - p.width),
        Math.max(8, window.innerWidth - p.width - 8));

      panel.style.top = `${top}px`;
      panel.style.left = `${left}px`;
    };

    const state = { panel, onToggle: e => { if (e.newState === 'open') place(); } };

    panel.addEventListener('toggle', state.onToggle);
    return state;
  },

  /**
   * Open a menu at a point, for a caller that drew its own button — the data grid renders the ⋯
   * inside a cell it owns, so there is no element here to anchor to.
   */
  openAt(panel, x, y) {
    if (!panel) return;

    if (panel.matches(':popover-open')) panel.hidePopover();
    panel.showPopover();

    const p = panel.getBoundingClientRect();

    const top = y + p.height > window.innerHeight - 8 ? Math.max(8, y - p.height - 4) : y + 4;
    const left = Math.min(Math.max(8, x - p.width), Math.max(8, window.innerWidth - p.width - 8));

    panel.style.top = `${top}px`;
    panel.style.left = `${left}px`;
  },

  close(panel) {
    if (panel?.matches(':popover-open')) panel.hidePopover();
  },

  detach(state) {
    state?.panel?.removeEventListener('toggle', state.onToggle);
  },
};

export const notify = {
  /**
   * Whether the window is in front.
   *
   * The rule everywhere Dray notifies: a notification about something the user is watching is
   * noise. A pull that finishes while its progress bar is on screen has already reported itself.
   */
  focused() {
    return document.hasFocus() && document.visibilityState === 'visible';
  },

  /**
   * Show a system notification, asking for permission the first time.
   *
   * Returns false when permission is missing or refused, which the caller treats as "not shown"
   * rather than as an error — a browser that will not notify is a preference, not a failure.
   *
   * Caveat worth knowing: some browsers only honour requestPermission() during a user gesture, and
   * this is called when an operation finishes rather than when it starts. Where that applies the
   * first request is ignored and the next one — after the user has granted it in site settings —
   * works. That is better than prompting on page load for something that may never happen.
   */
  async show(title, body) {
    if (!('Notification' in window)) return false;

    if (Notification.permission === 'denied') return false;

    if (Notification.permission === 'default') {
      try {
        if ((await Notification.requestPermission()) !== 'granted') return false;
      } catch {
        return false;
      }
    }

    try {
      new Notification(title, { body: body ?? undefined });
      return true;
    } catch {
      // Some contexts (an insecure origin, an embedded frame) construct and throw.
      return false;
    }
  },
};
