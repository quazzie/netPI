// Auth token handling.
// The host normally authenticates the page with an HttpOnly cookie set by `/?token=…`. When the page is
// served by something else (the Vite dev server), the token comes from `?token=` in the page URL or from
// VITE_NETPI_TOKEN; we keep it in sessionStorage and send it explicitly (X-NetPI-Token / ?token=).
const KEY = 'netpi.token';
let token = null;

export function initToken() {
  try {
    const url = new URL(location.href);
    const fromUrl = url.searchParams.get('token');
    if (fromUrl) {
      token = fromUrl;
      try {
        sessionStorage.setItem(KEY, fromUrl);
      } catch {}
      // Dev server: let proxied requests that cannot carry a header (plugin module imports) authenticate
      // with the same cookie name the host uses. Ignored by the browser when the host already set its
      // HttpOnly cookie.
      try {
        document.cookie = `netpi_token=${encodeURIComponent(fromUrl)}; path=/; SameSite=Strict`;
      } catch {}
      url.searchParams.delete('token');
      history.replaceState(history.state, '', url.pathname + url.search + url.hash);
      return token;
    }
  } catch {}
  try {
    token = sessionStorage.getItem(KEY);
  } catch {}
  if (!token && import.meta.env.VITE_NETPI_TOKEN) {
    token = import.meta.env.VITE_NETPI_TOKEN;
    try {
      document.cookie = `netpi_token=${encodeURIComponent(token)}; path=/; SameSite=Strict`;
    } catch {}
  }
  return token;
}

export function getToken() {
  return token;
}

/** Headers for fetch() against the host. */
export function authHeaders(extra = {}) {
  return token ? { ...extra, 'X-NetPI-Token': token } : extra;
}
