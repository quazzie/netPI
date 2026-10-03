// While an answer streams, only the block being written is rendered again: everything before it is finished and never
// changes. splitLive(text) says where the two meet — the end of the last complete top-level block.
//
// A cut is only taken at a blank line no open block crosses: outside a code fence, and only where the next line starts a
// new block (a list item, a blockquote, an indented line or a link definition carries the block above it on, and a blank
// line inside an indented code block is part of its code). A block that could carry on stays in the live tail, so the split
// never changes what a block renders as.
//
// Every line it reads is complete (a line still being written can grow into a list item or a fence, which would move the
// cut backwards), so a cut taken from one text is still the cut when the text grows.

const BLANK = /^[ \t]*$/;
/** An opening fence: up to three spaces of indent, then three or more backticks or tildes. */
const FENCE = /^ {0,3}(`{3,}|~{3,})/;
/** A line that closes that fence: the same marker, at least as long, and nothing else on the line. */
const CLOSES = (marker) => (line) => {
  const m = /^ {0,3}(`{3,}|~{3,})[ \t]*$/.exec(line);
  return !!m && m[1][0] === marker[0] && m[1].length >= marker.length;
};
/** Lines that carry the block above them on into the next one. */
const CONTINUES = /^(?: {4,}|\t|>|[-+*]|\d{1,9}[.)]|\[[^\]]+\]:)/;

/** The index in `text` where the complete blocks end and the block still being written begins. */
export function splitLive(text) {
  let cut = 0;
  let closes = null;
  let blankAt = -1; // where the blank line that could end a block starts
  for (let at = 0; at < text.length; ) {
    const nl = text.indexOf('\n', at);
    if (nl === -1) break; // the line still being written
    const line = text.slice(at, nl);
    at = nl + 1;
    if (closes) {
      if (closes(line)) closes = null;
      continue;
    }
    if (blankAt >= 0) {
      // this line decides whether the blank line above it ended a block
      if (!CONTINUES.test(line)) cut = blankAt;
      blankAt = -1;
    }
    if (BLANK.test(line)) blankAt = at;
    else {
      const fence = FENCE.exec(line);
      if (fence) closes = CLOSES(fence[1]);
    }
  }
  return cut;
}