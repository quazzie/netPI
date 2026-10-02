// Apply the saved theme before the first paint (no flash). A classic script in <head>, so it runs while the page is
// still parsing; a CSP without 'unsafe-inline' for scripts is why it is a file and not an inline <script>.
try {
  var p = JSON.parse(localStorage.getItem('netpi.prefs') || '{}');
  var t = p.theme || 'dark';
  if (t === 'system') t = matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  document.documentElement.dataset.theme = t;
} catch (e) {}
