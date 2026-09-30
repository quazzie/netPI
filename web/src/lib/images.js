// Images travel as base64 inside one JSON envelope on the WebSocket, so what fits is the host's message limit
// (app.info.maxMessageBytes, WsHub.MaxMessageBytes), not the file size: a 1.5 MB screenshot becomes ~2 MB of base64
// and used to be cut off mid-send, which the user saw as "could not send".
const FALLBACK_LIMIT = 2 * 1024 * 1024; // the host's WsHub.MaxMessageBytes, until app.info arrives
const ENVELOPE_SLACK = 8 * 1024; // the JSON around the images: ids, text, escaping
const RAW_FILE_LIMIT = 24 * 1024 * 1024; // a bigger file is refused outright rather than decoded
const MAX_SIDE = 1600; // what a pasted screenshot or a photo needs to stay readable
const MAX_PIXELS = 2.5e6;

/** Bytes of one send envelope available to base64 image data. */
export function sendBudget(limit) {
  return Math.max(64 * 1024, (limit || FALLBACK_LIMIT) - ENVELOPE_SLACK);
}

/** What one image may cost, so two of them still fit in one message. */
export function imageBudget(limit) {
  return Math.max(96 * 1024, Math.floor(sendBudget(limit) / 2));
}

export function payloadBytes(images) {
  return (images ?? []).reduce((n, i) => n + (i?.data?.length ?? 0), 0);
}

export const formatBytes = (n) =>
  n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`;

function readAsDataUrl(file) {
  return new Promise((resolve) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => resolve(null);
    r.readAsDataURL(file);
  });
}

async function decode(file) {
  if (typeof createImageBitmap === 'function') {
    try {
      return await createImageBitmap(file);
    } catch { /* fall through to the <img> path */ }
  }
  const url = URL.createObjectURL(file);
  try {
    const img = new Image();
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error('decode failed'));
      img.src = url;
    });
    return img;
  } finally {
    // the bitmap is painted before this resolves, so the object URL can go now
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

function toBlob(canvas, type, quality) {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality));
}

const blobToDataUrl = (blob) =>
  new Promise((resolve) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => resolve(null);
    r.readAsDataURL(blob);
  });

async function paint(source, scale) {
  const w = Math.max(1, Math.round(source.width * scale));
  const h = Math.max(1, Math.round(source.height * scale));
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(source, 0, 0, w, h);
  return canvas;
}

/**
 * An image ready to send: kept as it is when it fits, shrunk and re-encoded when it does not.
 * Returns { image, note } — image is null when the file cannot be made to fit (note says why).
 */
export async function prepareImage(file, limit) {
  if (!file?.type?.startsWith('image/')) return { image: null, note: null };
  const name = file.name || 'pasted image';
  if (file.size > RAW_FILE_LIMIT) {
    return { image: null, note: `${name} is ${formatBytes(file.size)} — too large to attach` };
  }

  const budget = imageBudget(limit);
  const raw = await readAsDataUrl(file);
  if (!raw) return { image: null, note: null };
  const data = raw.slice(raw.indexOf(',') + 1);
  if (data.length <= budget) return { image: { mediaType: file.type, data, url: raw, name }, note: null };

  let source;
  try {
    source = await decode(file);
  } catch {
    return { image: null, note: `${name} could not be read as an image` };
  }

  const longest = Math.max(source.width, source.height);
  let scale = Math.min(1, MAX_SIDE / longest, Math.sqrt(MAX_PIXELS / (source.width * source.height)));
  // PNG keeps what the file had (sharp edges, transparency); JPEG is the fallback that actually gets small.
  const attempts = [
    { type: 'image/png' },
    { type: 'image/jpeg', quality: 0.85 },
    { type: 'image/jpeg', quality: 0.7 },
    { type: 'image/jpeg', quality: 0.55 },
  ];
  try {
    for (let pass = 0; pass < 2; pass++) {
      const canvas = await paint(source, scale);
      if (!canvas) break;
      for (const { type, quality } of attempts) {
        const url = await blobToDataUrl(await toBlob(canvas, type, quality));
        if (!url) continue;
        const encoded = url.slice(url.indexOf(',') + 1);
        if (encoded.length > budget) continue;
        return {
          image: { mediaType: type, data: encoded, url, name },
          note: `${name} was ${formatBytes(file.size)} — shrunk to ${formatBytes(encoded.length)} to fit`,
        };
      }
      scale *= 0.7; // still too big at this size: try a smaller one before giving up
    }
    return { image: null, note: `${name} (${formatBytes(file.size)}) does not fit in a message — attach a smaller image` };
  } finally {
    source.close?.();
  }
}
