// The run statuses (agent.status) that count as busy: the run holds or waits for a slot. Plain module, so a test can read it:
// the Work tab's bundle keeps its own copy (plugins/NetPI.Work/ui/util.js ACTIVE; a plugin tab does not import host modules).
export const BUSY = new Set(['running', 'queued', 'yielded']);
