  // ---------------------------------------------------------------- inicio
  aplicarTema();
  if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('sw.js').catch(() => { });
  cargarSesion();
  cargarEstadoHuella();
  if (S.token) entrar(); else salir();
})();
