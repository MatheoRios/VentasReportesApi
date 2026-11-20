// Guarda solo la "cáscara" de la app (HTML, estilos, fuentes, íconos) para que abra rápido.
// Los datos (/api) NUNCA se guardan: siempre se piden al servidor.
const VERSION = 'rpos-v11';
const ARCHIVOS = ['./', 'index.html', 'app.css?v=7', 'app.js?v=11', 'tema.js?v=1', 'manifest.webmanifest', 'icono-192.png',
  'fonts/figtree-latin-400-normal.woff2', 'fonts/figtree-latin-500-normal.woff2', 'fonts/figtree-latin-600-normal.woff2',
  'fonts/figtree-latin-700-normal.woff2', 'fonts/sora-latin-600-normal.woff2', 'fonts/sora-latin-700-normal.woff2'];
self.addEventListener('install', e => { e.waitUntil(caches.open(VERSION).then(c => c.addAll(ARCHIVOS)).then(() => self.skipWaiting())); });
self.addEventListener('activate', e => {
  e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== VERSION).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});
self.addEventListener('fetch', e => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET' || u.origin !== location.origin || !u.pathname.startsWith('/app/')) return;
  // primero la red (para recibir actualizaciones); sin red, lo guardado
  e.respondWith(fetch(e.request).then(r => {
    if (r.ok) { const copia = r.clone(); caches.open(VERSION).then(c => c.put(e.request, copia)); }
    return r;
  }).catch(() => caches.match(e.request).then(r => r || caches.match('index.html'))));
});
