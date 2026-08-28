// The data grid, over Tabulator.
//
// Dray's tables are the app's densest surface and the one people spend the most time in front of,
// and the behaviour they want — sort by a column, drag a column wider, keep the important columns
// as the window narrows, scroll four hundred rows at sixty frames a second — is a lot of table
// behaviour to write twice, let alone once per page.
//
// NOT IN USE. This renders rows correctly but every column comes back with a null width, so cells
// fall back to their own content and nothing lines up with its heading. What is known, so the next
// attempt does not repeat it:
//
//   - The library is fine. The same column definitions, the same data and the same options, built
//     into a plain div with an explicit width, produce real widths ([180, 110, 160, …]). The live
//     one produces nulls. Cloned from the live table's own getColumnDefinitions(), so it is not the
//     column spec either.
//   - Not the options: fitColumns, responsiveLayout 'hide', layoutColumnsOnNewData false and a
//     custom index were each tested in isolation against the control and all produce widths.
//   - Not the stylesheet (200, 28KB, present in document.styleSheets), not box-sizing, not the
//     element's height (627px), and not the redraw failing to run.
//   - The element IS two pixels wide at construction, because `flex: 1 1 auto` resolves its basis
//     from a table that sizes itself from the container. `flex: 1 1 0` fixes the width — and the
//     columns are still null, so that was necessary and not sufficient.
//   - Deferring construction until the element has a width does not fix it either, nor does pinning
//     an explicit pixel width across the build.
//   - Not the flex structure: a control using this component's exact box model — `flex: 1 1 0`,
//     `min-width: 0`, `height: 100%` inside a flex parent — produces widths.
//   - Not the formatters, the object-valued cells, the custom sorters, the placeholder, or
//     selectableRows/reactiveData/persistence. Each was added to the working control in turn and
//     none of them breaks it.
//   - Not Blazor owning the subtree. Tabulator is now mounted into a child element created in JS
//     (which is correct regardless — Blazor must not diff a subtree a library replaces), and the
//     widths are still null.
//
// The remaining difference between the working control and this is the Blazor-hosted lifecycle,
// and that is where the next look should start.
//
// The trade is that the grid renders its own cells, so a cell cannot be a Blazor component. That is
// why GridCell is a closed set: every kind of cell in the app is nameable, C# resolves the *values*
// (a container's state comes from ContainerStatusVocabulary, not from here), and this file only
// assembles markup from them using the same classes the rest of the app uses. A formatter that
// invented a colour or a word would be the bug this arrangement exists to prevent.

import { TabulatorFull } from '../lib/tabulator/tabulator.mjs';

// The base stylesheet, injected once. Same reason as the terminal's: Dray.Ui is a razor class
// library, so its assets live under /_content/Dray.Ui/ rather than at the app root, and the path
// is derived from this module's own URL instead of being written down in three host pages.
if (!document.querySelector('link[data-dray-tabulator]')) {
  const css = document.createElement('link');

  css.rel = 'stylesheet';
  css.href = new URL('../lib/tabulator/tabulator.css', import.meta.url).href;
  css.dataset.drayTabulator = '';

  document.head.appendChild(css);
}

const escapeHtml = (value) =>
  String(value ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// ---------------------------------------------------------------- cell formatters

const text = (cell) => escapeHtml(cell.getValue());

const wrap = (cls) => (cell) => `<span class="${cls}">${escapeHtml(cell.getValue())}</span>`;

/** Tint, glyph and word — the same three parts, and the same classes, as StatePill. */
function state(cell) {
  const v = cell.getValue();
  if (!v) return '';

  const detail = v.detail ? `<span class="pill__detail">· ${escapeHtml(v.detail)}</span>` : '';
  const title = v.detail ? `${v.word} · ${v.detail}` : v.word;

  return `<span class="pill pill--${escapeHtml(v.tone)}" title="${escapeHtml(title)}">`
    + `<span class="pill__glyph" aria-hidden="true">${escapeHtml(v.glyph)}</span>`
    + `<span class="pill__word">${escapeHtml(v.word)}</span>${detail}</span>`;
}

/** A name, and under it whatever identifies it a second way — a short id, an image tag. */
function link(cell) {
  const v = cell.getValue();
  if (!v) return '';

  const sub = v.sub ? `<span class="grid__sub mono">${escapeHtml(v.sub)}</span>` : '';

  return `<span class="grid__name"><a href="${escapeHtml(v.href)}">${escapeHtml(v.text)}</a>${sub}</span>`;
}

/** Short on screen, whole on the clipboard. */
function chip(cell) {
  const v = cell.getValue();
  if (!v) return '';

  return `<button type="button" class="grid__chip mono" data-copy="${escapeHtml(v.copy)}"`
    + ` title="${escapeHtml(v.tooltip || v.copy)}">${escapeHtml(v.text)}</button>`;
}

function actions(cell) {
  const row = cell.getRow().getData();

  return `<button type="button" class="btn btn--ghost btn--icon grid__more" data-row="${escapeHtml(row.__key)}"`
    + ` aria-label="More actions" title="More actions">`
    + `<svg class="icon icon--md" aria-hidden="true"><use href="#i-more"></use></svg></button>`;
}

const FORMATTERS = {
  Text: text,
  Mono: wrap('mono'),
  Muted: wrap('muted'),
  Percent: wrap('num'),
  Bytes: wrap('num'),
  Since: wrap('muted'),
  State: state,
  Link: link,
  Chip: chip,
  Actions: actions,
};

// ---------------------------------------------------------------- sorting
//
// A cell's value is an object for the rich kinds, so each needs to say what "less than" means.
// Sorting a column of state pills alphabetically by their markup is the sort of thing that looks
// like it works until someone sorts by state.

const SORTERS = {
  State: (a, b) => (a?.rank ?? 9) - (b?.rank ?? 9),
  Link: (a, b) => String(a?.text ?? '').localeCompare(String(b?.text ?? '')),
  Chip: (a, b) => String(a?.copy ?? '').localeCompare(String(b?.copy ?? '')),
};

function column(spec) {
  const col = {
    field: spec.field,
    title: spec.title,
    headerSort: spec.sortable && spec.cell !== 'Actions',
    resizable: spec.cell !== 'Actions',
    formatter: FORMATTERS[spec.cell] || text,
    responsive: spec.cell === 'Actions' ? 0 : spec.priority,
    minWidth: spec.minWidth || undefined,
    tooltip: false,
  };

  if (SORTERS[spec.cell]) col.sorter = SORTERS[spec.cell];
  else if (spec.numeric) col.sorter = 'number';

  if (spec.numeric || spec.cell === 'Actions') col.hozAlign = 'right';

  // The ⋯ column is furniture: it never sorts, never hides, and never takes more room than the
  // control inside it.
  if (spec.cell === 'Actions') {
    col.width = 44;
    col.resizable = false;
    col.headerSort = false;
  }

  return col;
}

export const grid = {
  create(el, spec, dotnet) {
    if (!el) return null;

    const state = { el, dotnet, spec, selected: new Set(), rows: spec.rows || [] };

    // Built only once the element has a width.
    //
    // UNRESOLVED — see the note at the top of this file. Waiting for a width is necessary and not
    // sufficient: the columns still come back with null widths.
    //
    // Tabulator measures its container when it builds and distributes that width across the
    // columns. Blazor calls this during OnAfterRenderAsync — before the element has been laid out —
    // so it measured nothing, every column came out with a null width, and every cell fell back to
    // its own content. Redrawing afterwards does not recover: the columns are already null.
    //
    // Proven rather than guessed: the same columns, data and options built into an element that
    // already had a width produced [180, 110, 160, …] while the live one produced nulls.
    // Tabulator gets a child of its own, never the element Blazor rendered.
    //
    // Blazor owns the children of every element in its render tree, and Tabulator replaces them
    // wholesale. The two then fight over the same subtree on every re-render — and this page
    // re-renders constantly, because containers change — which is how a grid ends up with rows
    // that were built correctly and inline widths that have been diffed away.
    state.mount = document.createElement('div');
    state.mount.className = 'grid__mount';
    el.appendChild(state.mount);

    const build = () => {
      if (state.table || el.clientWidth < 10) return false;

      state.table = table(state.mount, state, dotnet);
      return true;
    };

    if (!build() && typeof ResizeObserver !== 'undefined') {
      state.waiting = new ResizeObserver(() => {
        if (!build()) return;

        state.waiting.disconnect();
        state.waiting = null;

        watch(state);
      });

      state.waiting.observe(el);
    } else {
      watch(state);
    }

    listen(state, dotnet);
    return state;
  },

  /** Replace the data without rebuilding the grid, so sort order and column widths survive. */
  update(state, rows) {
    if (!state) return;

    state.rows = rows;

    // Held rather than dropped when the table is still waiting for a width — the first load
    // usually arrives before the element has one.
    if (!state.table) return;

    Promise.resolve(state.table.replaceData(rows))
      .catch(() => {})
      .then(() => state.table.redraw(true));
  },

  /** Paint the selection the page decided on. */
  select(state, keys) {
    if (!state) return;

    state.selected = new Set(keys || []);
    if (state.table) paint(state);
  },

  destroy(state) {
    if (!state) return;

    if (state.frame) cancelAnimationFrame(state.frame);

    state.waiting?.disconnect();
    state.observer?.disconnect();

    state.el?.removeEventListener('click', state.onClick);
    state.el?.removeEventListener('dblclick', state.onDouble);

    state.table?.destroy();
    state.mount?.remove();
  },
};

function table(el, state, dotnet) {
  const spec = state.spec;

  const built = new TabulatorFull(el, {
    data: state.rows,
    columns: spec.columns.map(column),
    index: '__key',

    // Fill the width, let the user redistribute it, and keep what they did across a data update.
    layout: 'fitColumns',
    layoutColumnsOnNewData: false,
    persistence: false,

    // Drop the least important columns first rather than growing a horizontal scrollbar — the
    // priorities come from C#, where the judgement about which column matters lives.
    responsiveLayout: 'hide',

    // Four hundred rows at sixty frames a second is the exit criterion this replaces
    // <Virtualize> to keep.
    renderVertical: 'virtual',
    height: '100%',

    placeholder: spec.placeholder || 'Nothing here',
    reactiveData: false,
    selectableRows: false,
  });

  // Selection lives in C# — click, shift-range, Escape and select-all are the page's rules, not
  // the grid's — so the grid only paints what it is told.
  built.on('renderComplete', () => paint(state));

  return built;
}

/** Keep the layout right as the window resizes or the sidebar collapses. */
function watch(state) {
  if (typeof ResizeObserver === 'undefined') return;

  state.observer = new ResizeObserver(() => {
    if (state.frame) return;

    state.frame = requestAnimationFrame(() => {
      state.frame = 0;
      if (state.table && state.el.clientWidth > 10) state.table.redraw(true);
    });
  });

  state.observer.observe(state.el);
}

function listen(state, dotnet) {
  const el = state.el;

  // One delegated listener rather than one per cell: rows come and go as the grid virtualizes,
  // and a listener per row would leak every time it scrolled.
  state.onClick = (e) => {
    const chipEl = e.target.closest('.grid__chip');
    if (chipEl) {
      e.preventDefault();
      dotnet.invokeMethodAsync('OnCopy', chipEl.dataset.copy);
      return;
    }

    const more = e.target.closest('.grid__more');
    if (more) {
      e.preventDefault();
      const box = more.getBoundingClientRect();
      dotnet.invokeMethodAsync('OnMenu', more.dataset.row, box.left, box.bottom, box.right);
      return;
    }

    // A link in a cell is a link: let it navigate rather than treating it as a click on the row.
    if (e.target.closest('a')) return;

    const row = e.target.closest('.tabulator-row');
    if (!row) return;

    const key = keyOf(state, row);
    if (key !== null) dotnet.invokeMethodAsync('OnRow', key, e.metaKey || e.ctrlKey, e.shiftKey);
  };

  state.onDouble = (e) => {
    if (e.target.closest('a') || e.target.closest('button')) return;

    const row = e.target.closest('.tabulator-row');
    if (!row) return;

    const key = keyOf(state, row);
    if (key !== null) dotnet.invokeMethodAsync('OnOpen', key);
  };

  el.addEventListener('click', state.onClick);
  el.addEventListener('dblclick', state.onDouble);
}

function keyOf(state, el) {
  if (!state.table) return null;

  const row = state.table.getRows().find((r) => r.getElement() === el);
  return row ? row.getData().__key : null;
}

function paint(state) {
  if (!state.table) return;

  for (const row of state.table.getRows()) {
    const el = row.getElement();
    if (el) el.classList.toggle('is-selected', state.selected.has(row.getData().__key));
  }
}
