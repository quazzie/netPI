// Svelte actions the components share.

/** `use:scrollIntoView={active}`: keep the active row of a list in view (a keyboard-driven popup). */
export function scrollIntoView(node, active) {
  if (active) node.scrollIntoView({ block: 'nearest' });
  return { update: (a) => a && node.scrollIntoView({ block: 'nearest' }) };
}
