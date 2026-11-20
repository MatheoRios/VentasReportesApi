// Aplica el tema y el modo guardados antes de pintar la página (evita el parpadeo)
(function () {
  try {
    var r = document.documentElement, t = localStorage.getItem('rpos.tema'), m = localStorage.getItem('rpos.modo');
    if (t && t !== 'verde') r.setAttribute('data-brand', t);
    if (m === 'dark' || m === 'light') r.setAttribute('data-theme', m);
  } catch (e) { }
})();
