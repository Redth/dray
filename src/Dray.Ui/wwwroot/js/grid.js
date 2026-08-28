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

    const state = { table, dotnet, el };

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
      }
    };

    el.addEventListener('click', state.onClick);
    return state;
  },

  /** Replace the data without rebuilding the grid, so sort order and column widths survive. */
  update(state, rows) {
    if (!state?.table) return;
    state.table.replaceData(rows);
  },

  destroy(state) {
    if (!state) return;

    state.el?.removeEventListener('click', state.onClick);
    state.table?.destroy();
  },
};
