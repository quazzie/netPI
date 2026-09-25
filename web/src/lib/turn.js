// The numbers of one model turn (an assistant message): how long its first token took (meta.ttftMs, recorded by the
// runner), how big the prompt was and how much of it the backend reused from its cache, and the output with its speed.
import { duration } from './format.js';

/** { ttft, prompt, cached, cachedPct, out, tps } of an assistant message; null without usage or a first-token time. */
export function turnStats(m) {
  const u = m?.usage;
  const ttft = Number.isFinite(m?.meta?.ttftMs) ? m.meta.ttftMs : null;
  if (!u && ttft == null) return null;
  const prompt = u ? (u.inputTokens ?? 0) + (u.cacheReadTokens ?? 0) + (u.cacheWriteTokens ?? 0) : 0;
  const cached = u?.cacheReadTokens ?? 0;
  const out = u?.outputTokens ?? null;
  // the output speed counts from the first token: the wait before it is the prompt's (prefill)
  const genMs = m.durationMs != null && ttft != null ? m.durationMs - ttft : null;
  const tps = out >= 10 && genMs >= 100 ? out / (genMs / 1000) : null;
  return { ttft, prompt, cached, cachedPct: prompt ? cached / prompt : null, out, tps };
}

/** "97%": floored, so a prompt that was not all reused never reads 100%. */
export const cachedText = (s) => `${Math.floor((s.cachedPct ?? 0) * 100)}%`;
export const speedText = (tps) => (tps < 10 ? tps.toFixed(1) : String(Math.round(tps)));

/** The exact numbers, for a tooltip. */
export function turnTitle(s, durationMs) {
  const n = (v) => Number(v).toLocaleString('en-US');
  const lines = [];
  if (s.ttft != null) lines.push(`First token after ${duration(s.ttft)}`);
  if (s.prompt) lines.push(`Prompt ${n(s.prompt)} tokens: ${n(s.cached)} reused from the cache (${cachedText(s)}), ${n(s.prompt - s.cached)} new`);
  if (s.out != null) lines.push(`Output ${n(s.out)} tokens${durationMs != null ? ` in ${duration(durationMs)}` : ''}${s.tps ? ` (${speedText(s.tps)} tokens/s after the first)` : ''}`);
  return lines.join('\n');
}
