// catchUpStream holds the scoped events that arrive while it waits for the server (the answer to agent.stream, which can take a
// retry). A long wait must not build an unbounded array: past `limit` entries a stream delta joins the delta before it (same
// kind) and a tool.output chunk joins the chunk before it (same call). The merged entry keeps the later seq, so at worst a
// seam repeats a few characters rather than losing any.

/** Adds [type, d, seq] to `held`, merged into the last entry when `held` is past `limit` and the two are the same stream. */
export function holdEvent(held, type, d, seq, limit) {
  const last = held[held.length - 1];
  if (held.length > limit && last?.[0] === type) {
    if (type === 'stream.delta' && last[1].kind === d.kind) {
      last[1] = { ...last[1], text: (last[1].text ?? '') + (d.text ?? '') };
      last[2] = seq;
      return;
    }
    if (type === 'tool.output' && last[1].callId === d.callId) {
      last[1] = { ...last[1], chunk: (last[1].chunk ?? '') + (d.chunk ?? '') };
      last[2] = seq;
      return;
    }
  }
  held.push([type, d, seq]);
}
