// The data grid, over Tabulator.
//
// Dray's tables are the app's densest surface and the one people spend the most time in front of,
// and the behaviour they want — sort by a column, drag a column wider, keep the important columns
// as the window narrows, scroll four hundred rows at sixty frames a second — is a lot of table
// behaviour to write twice, let alone once per page.
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

    const table = new TabulatorFull(el, {
      data: spec.rows,
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

    const state = { table, dotnet, el, selected: new Set() };

    // Selection lives in C# — click, shift-range, Escape and select-all are the page's rules, not
    // the grid's — so the grid only paints what it is told. rowFormatter runs for every row the
    // grid renders, which is what keeps the class right as rows scroll in and out.
    table.on('renderComplete', () => paint(state));

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

    // A virtualized grid works out how many rows fit from the height it had when it was built, and
    // Blazor builds this one during the render that gives the page its height — so at construction
    // it measures nothing and draws nothing. Redrawing when the element's size settles is what
    // makes the first paint appear, and it is the same thing that keeps it right when the window
    // is resized or the sidebar collapses.
    if (typeof ResizeObserver !== 'undefined') {
      state.observer = new ResizeObserver(() => {
        if (state.frame) return;

        state.frame = requestAnimationFrame(() => {
          state.frame = 0;
          if (el.clientHeight > 0) state.table.redraw(true);
        });
      });

      state.observer.observe(el);
    }

    return state;
  },

  /** Paint the selection the page decided on. */
  select(state, keys) {
    if (!state) return;

    state.selected = new Set(keys || []);
    paint(state);
  },

  /** Replace the data without rebuilding the grid, so sort order and column widths survive. */
  update(state, rows) {
    if (!state?.table) return;

    // redraw(true) rather than plain replaceData, and unconditionally: the row count changing is
    // exactly when the virtual viewport and the column layout need recomputing, and the first load
    // goes from nothing to everything. Swallowing the promise's rejection without redrawing left
    // the grid showing rows whose cells had never been given a width.
    Promise.resolve(state.table.replaceData(rows))
      .catch(() => {})
      .then(() => state.table.redraw(true));
  },

  destroy(state) {
    if (!state) return;

    if (state.frame) cancelAnimationFrame(state.frame);
    state.observer?.disconnect();

    state.el?.removeEventListener('click', state.onClick);
    state.el?.removeEventListener('dblclick', state.onDouble);
    state.table?.destroy();
  },
};

function keyOf(state, el) {
  const row = state.table.getRows().find((r) => r.getElement() === el);
  return row ? row.getData().__key : null;
}

function paint(state) {
  for (const row of state.table.getRows()) {
    const el = row.getElement();
    if (el) el.classList.toggle('is-selected', state.selected.has(row.getData().__key));
  }
}
