// Batch work into the next animation frame (with a timer fallback for hidden/background windows, where
// requestAnimationFrame is paused). Used to flush streaming buffers into reactive state at most once a frame.
const queue = new Set();
let scheduled = false;
let raf = 0;
let timer = 0;

function run() {
  cancelAnimationFrame(raf);
  clearTimeout(timer);
  scheduled = false;
  const fns = [...queue];
  queue.clear();
  for (const fn of fns) {
    try {
      fn();
    } catch (e) {
      console.error(e);
    }
  }
}

export function nextFrame(fn) {
  queue.add(fn);
  if (scheduled) return;
  scheduled = true;
  raf = requestAnimationFrame(run);
  timer = setTimeout(run, 120);
}

/** Run a queued callback now (e.g. at stream end) instead of waiting for the frame. */
export function flushNow(fn) {
  if (queue.delete(fn)) fn();
}
