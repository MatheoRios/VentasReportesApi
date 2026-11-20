  // ---------------------------------------------------------------- navegación
  const ICON = {
    ventas: '<path d="M3 3v18h18"/><path d="M7 15l4-4 3 3 5-6"/>',
    cierres: '<rect x="3" y="6" width="18" height="13" rx="2"/><path d="M3 10h18M7 15h3"/>',
    calc: '<rect x="5" y="2.5" width="14" height="19" rx="2.5"/><path d="M8.5 7h7M8.5 12h.01M12 12h.01M15.5 12h.01M8.5 16h.01M12 16h.01M15.5 16h.01"/>',
    chat: '<path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12z"/>',
    mas: '<circle cx="5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="19" cy="12" r="1.3"/>',
    excel: '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M9 8l6 8M15 8l-6 8"/>'
  };
  const svg = (p, c = 'w-5 h-5') => `<svg class="${c}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${p}</svg>`;
  const VISTAS = [
    { id: 'ventas', nombre: 'Ventas', permiso: 'resumen' },
    { id: 'cierres', nombre: 'Cierres', permiso: 'cierres' },
    { id: 'calc', nombre: 'Calculadora', corto: 'Calcular' },
    { id: 'chat', nombre: 'Chat', permiso: 'chat' },
    { id: 'mas', nombre: 'Más' },
    { id: 'admin', nombre: 'Administración', gestor: true, oculto: true }
  ];
  const esGestor = () => S.rol === 'ADMIN' || S.rol === 'JEFE';
  const visibles = () => VISTAS.filter(v => (!v.permiso || tiene(v.permiso)) && (!v.gestor || esGestor()));
  let vista = 'ventas', noLeidos = 0;
  function pintarNav() {
    const vs = visibles().filter(v => !v.oculto);
    $('#navBottom').style.gridTemplateColumns = `repeat(${vs.length}, minmax(0, 1fr))`;
    const badge = v => v.id === 'chat' && noLeidos > 0 ? `<span class="absolute -top-0.5 right-2 min-w-[18px] h-[18px] px-1 rounded-full bg-bad text-white text-[10px] font-bold grid place-items-center num">${noLeidos > 99 ? '99+' : noLeidos}</span>` : '';
    $('#navBottom').innerHTML = vs.map(v => `
      <button data-v="${v.id}" class="relative flex flex-col items-center gap-0.5 py-1 text-[11px] font-medium ${vista === v.id ? 'text-p700' : 'text-muted'}" aria-current="${vista === v.id ? 'page' : 'false'}">
        <span class="relative grid place-items-center h-8 w-14 rounded-full ${vista === v.id ? 'bg-soft text-softfg' : ''}">${svg(ICON[v.id])}${badge(v)}</span>${v.corto || v.nombre}
      </button>`).join('');
    $('#navSide').innerHTML = vs.filter(v => v.id !== 'mas' || true).map(v => `
      <button data-v="${v.id}" class="relative flex items-center gap-3 h-11 rounded-xl px-3 justify-center lg:justify-start text-sm font-medium ${vista === v.id ? 'bg-soft text-softfg' : 'text-muted hover:bg-raised'}" title="${v.nombre}">
        ${svg(ICON[v.id])}<span class="hidden lg:inline flex-1 text-left">${v.nombre}</span>
        ${v.id === 'chat' && noLeidos > 0 ? `<span class="absolute lg:static top-1 right-1 min-w-[18px] h-[18px] px-1 rounded-full bg-bad text-white text-[10px] font-bold grid place-items-center num">${noLeidos}</span>` : ''}
      </button>`).join('');
  }
  function ir(v, sinCargar) {
    if (!visibles().some(x => x.id === v)) v = visibles()[0].id;
    vista = v;
    VISTAS.forEach(x => { $('#v-' + x.id).hidden = x.id !== v; });
    $('#btnFiltro').hidden = !(v === 'ventas' || v === 'cierres');
    $('#btnRefrescar').hidden = v === 'mas' || v === 'calc';
    if (v !== 'admin') clearInterval(relojTiendas);
    if (v !== 'chat') canalAbierto = null;
    pintarNav(); encabezado();
    $('#main').scrollTop = 0;
    guardar('rpos.vista', v);
    if (!sinCargar) cargarVista(false);
  }
  document.addEventListener('click', e => { const b = e.target.closest('[data-v]'); if (b) { cerrar(); ir(b.dataset.v); } });

  function encabezado() {
    const t = { ventas: 'Ventas', cierres: 'Cierres de caja', calc: 'Calculadora', chat: 'Chat de tiendas', mas: 'Más opciones', admin: 'Administración' }[vista];
    $('#titulo').textContent = vista === 'chat' && canalAbierto ? canalAbierto.nombre : t;
    let sub = '';
    if (vista === 'ventas') sub = textoTiendas() + ' · ' + textoRango(calcRango(rV));
    else if (vista === 'cierres') sub = textoTiendas() + ' · ' + textoRango(calcRango(rC));
    else if (vista === 'calc') sub = 'Bs · USD · EUR';
    else if (vista === 'chat') sub = canalAbierto ? (canalAbierto.detalle || '') : 'Mensajes con las tiendas';
    else if (vista === 'admin') sub = adminTab === 'usuarios' ? 'Usuarios: activar, desactivar y desbloquear' : 'Estado de la conexión con cada tienda';
    else sub = S.nombre;
    $('#subtitulo').textContent = sub;
    $('#filtroCount').textContent = seleccion.size;
    const atras = vista === 'admin' || (vista === 'chat' && canalAbierto && !window.matchMedia('(min-width: 1024px)').matches);
    $('#btnVolver').hidden = !atras; $('#logoMovil').hidden = atras;
  }
  $('#btnVolver').onclick = () => { if (vista === 'admin') { ir('mas'); return; } canalAbierto = null; pintarChat(); encabezado(); };

  let girando = 0;
  function cargando(si) { girando += si ? 1 : -1; $('#icoRefrescar').classList.toggle('girando', girando > 0); }
  $('#btnRefrescar').onclick = () => cargarVista(true);
  function cargarVista(forzar) {
    if (vista === 'ventas') cargarVentas(forzar);
    else if (vista === 'cierres') cargarCierres(forzar);
    else if (vista === 'chat') { cargarContactos(); cargarCanales().then(() => canalAbierto && cargarMensajes(!mensajes.length)); }
    else if (vista === 'calc') cargarTasa(forzar);
    else if (vista === 'mas') pintarHuellaMas();
    else if (vista === 'admin') { cargarUsuarios(); cargarEstadoTiendas(forzar); clearInterval(relojTiendas); relojTiendas = setInterval(() => { if (!document.hidden && vista === 'admin') cargarEstadoTiendas(false); }, 60e3); }
  }

  const chipsRango = (cont, actual) => {
    $(cont).innerHTML = RANGOS.map(([k, n]) => `<button data-r="${k}" aria-pressed="${actual.tipo === k}">${n}</button>`).join('')
      + `<button data-r="otro" aria-pressed="${actual.tipo === 'otro'}" class="inline-flex items-center gap-1.5">${svg('<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>', 'w-3.5 h-3.5')}${actual.tipo === 'otro' ? textoRango(calcRango(actual)) : 'Fechas'}</button>`;
  };

  const errorTiendas = (lista) => !lista.length ? '' : `
    <div class="rounded-xl border border-bad/30 bg-badbg/60 p-3.5 text-sm">
      <div class="font-semibold text-bad mb-1">${lista.length === 1 ? '1 tienda no respondió' : lista.length + ' tiendas no respondieron'} — no están en los totales</div>
      <ul class="text-xs text-fg/80 flex flex-col gap-0.5">${lista.map(x => `<li><b>${esc(x.t)}</b>: ${esc(x.error)}</li>`).join('')}</ul>
    </div>`;

