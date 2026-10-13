// Vite plugin: compile UI sources as LF whatever line endings the working tree has. `.gitattributes` pins web/src and
// plugins/*/ui to LF, but an editor or agent tool can still write CRLF, and a build from that file bakes `\r` into
// any string that spans a line (a wrapped text in markup): the bundle then differs from what the committed source
// builds, its chunk hashes change, and every later build leaves web/dist "modified" in someone else's checkout.
// Listed first among the plugins so it runs before the Svelte compiler.
const SOURCE = /\.(svelte|[cm]?[jt]s|css)(\?.*)?$/;

export function lfSources() {
  return {
    name: 'netpi-lf-sources',
    enforce: 'pre',
    transform(code, id) {
      if (!code.includes('\r') || id.includes('/node_modules/') || !SOURCE.test(id)) return null;
      return { code: code.replace(/\r\n?/g, '\n'), map: null };
    },
  };
}
