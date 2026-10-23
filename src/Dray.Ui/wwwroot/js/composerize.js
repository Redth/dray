// `docker run …` to a compose service, by composerize — the library Dockge uses for the same thing.
//
// Loaded on first use: it is 275 KB and only the New stack dialog's "From a docker run line" asks
// for it. Vendored by build/vendor-composerize.mjs, so it works with the network unplugged.

let loading = null;

/**
 * Convert one command line. Returns YAML, or throws with composerize's own message — which names
 * the flag it could not map, and is the most useful thing to show.
 */
export async function convert(command) {
  loading ??= import(new URL('../lib/composerize/composerize.js', import.meta.url).href);
  const { default: composerize } = await loading;

  // Compose spec output ("latest"), two-space indent: the format the rest of Dray writes.
  return composerize(command, null, 'latest', 2);
}
