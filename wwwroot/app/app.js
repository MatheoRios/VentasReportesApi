// =============================================================================
//  Reportes POS — app web (teléfono, tablet y PC)
//  Usa la misma API que el complemento de Excel: mismos usuarios, permisos y tiendas.
//  Ventas = /api/resumen · Cierres = /api/cierres · Tasa = /api/tasa · Chat = /api/chat/*
// =============================================================================
(() => {
  'use strict';
  const $ = s => document.querySelector(s);
  const $$ = s => Array.from(document.querySelectorAll(s));
  const guardar = (k, v) => { try { localStorage.setItem(k, v); } catch (e) { } };
  const leer = k => { try { return localStorage.getItem(k); } catch (e) { return null; } };
  const borrar = k => { try { localStorage.removeItem(k); } catch (e) { } };
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const n2 = new Intl.NumberFormat('es-VE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const n0 = new Intl.NumberFormat('es-VE', { maximumFractionDigits: 0 });
  const n1 = new Intl.NumberFormat('es-VE', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const usd = v => '$' + n2.format(v || 0);
  const firmado = v => (v < 0 ? '−' : v > 0 ? '+' : '') + n2.format(Math.abs(v || 0));
  const pct = v => (v >= 0 ? '+' : '−') + n1.format(Math.abs(v)) + ' %';
  const FLECHA_ARRIBA = '<svg class="w-3 h-3" viewBox="0 0 12 12" fill="currentColor" aria-hidden="true"><path d="M6 2.5l4 5H2z"/></svg>';
  const FLECHA_ABAJO = '<svg class="w-3 h-3" viewBox="0 0 12 12" fill="currentColor" aria-hidden="true"><path d="M6 9.5l-4-5h8z"/></svg>';
  // variación con flecha + color + signo (nunca solo color)
  const chipVar = (v, extra = '') => v == null ? '' : `<span class="chip ${Math.abs(v) < 0.05 ? 'chip-neutro' : v > 0 ? 'chip-ok' : 'chip-bad'} ${extra}">${Math.abs(v) < 0.05 ? '' : v > 0 ? FLECHA_ARRIBA : FLECHA_ABAJO}${pct(v)}</span>`;
  const INACTIVIDAD_MIN = 20;
  const TOLERANCIA_USD = 1;            // igual que el complemento: un cierre cuadra si la diferencia es de hasta 1 USD
  const CONSULTAS_A_LA_VEZ = 4;

  // ---------------------------------------------------------------- sesión
  const S = { token: null, usuario: '', nombre: '', rol: '', permisos: [], expira: null, sucursales: [] };
  const tiene = p => S.permisos.includes(p);
  function cargarSesion() {
    try {
      const s = JSON.parse(leer('rpos.sesion') || 'null');
      if (s && s.token && new Date(s.expira) > new Date()) Object.assign(S, s);
    } catch (e) { }
  }
  const guardarSesion = () => guardar('rpos.sesion', JSON.stringify({ token: S.token, usuario: S.usuario, nombre: S.nombre, rol: S.rol, permisos: S.permisos, expira: S.expira }));

  class ErrorApi extends Error { constructor(codigo, msg) { super(msg); this.codigo = codigo; } }
  // Toda petición tiene tiempo máximo (así nada queda "colgado"): 30 s normal, más para las consultas a tiendas.
  async function api(ruta, cuerpo, opciones = {}) {
    const ctl = new AbortController(), externo = opciones.signal;
    const corte = setTimeout(() => ctl.abort('tiempo'), opciones.timeout || 30000);
    const reenviar = () => ctl.abort(externo.reason);
    if (externo) { if (externo.aborted) ctl.abort(externo.reason); else externo.addEventListener('abort', reenviar, { once: true }); }
    let r;
    try { r = await fetch(ruta, {
      method: opciones.metodo || (cuerpo === undefined ? 'GET' : 'POST'),
      headers: Object.assign({ 'Accept': 'application/json' }, cuerpo === undefined ? {} : { 'Content-Type': 'application/json' },
        S.token ? { 'Authorization': 'Bearer ' + S.token } : {}),
      body: cuerpo === undefined ? undefined : JSON.stringify(cuerpo),
      signal: ctl.signal, cache: 'no-store'
    }); }
    catch (e) {
      if (externo && externo.aborted) throw e;                       // cancelada a propósito
      throw new ErrorApi(0, ctl.signal.aborted ? 'El servidor tardó demasiado en responder. Intente de nuevo.' : 'Sin conexión con el servidor.');
    }
    finally { clearTimeout(corte); if (externo) externo.removeEventListener('abort', reenviar); }
    let j = null;
    try { j = await r.json(); } catch (e) { }
    if (r.status === 401 && S.token && !opciones.login) { salir('Su sesión venció. Ingrese de nuevo.'); throw new ErrorApi(401, 'Sesión vencida'); }
    if (!r.ok) throw new ErrorApi(r.status, (j && j.error) || (r.status === 0 ? 'Sin conexión con el servidor' : 'Error ' + r.status));
    return j;
  }
  const rol = r => ({ ADMIN: 'Administrador', JEFE: 'Jefe', REGIONAL: 'Regional', TIENDA: 'Tienda' }[r] || r || '');
  const iniciales = n => (n || '').split(/\s+/).filter(Boolean).slice(0, 2).map(x => x[0]).join('').toUpperCase() || '··';

  // ---------------------------------------------------------------- toast y panel inferior
  let toastT;
  function toast(t) { const e = $('#toast'); e.textContent = t; e.classList.remove('opacity-0'); clearTimeout(toastT); toastT = setTimeout(() => e.classList.add('opacity-0'), 3200); }
  let ocultarT;
  function abrir(html) {
    $('#sheetBody').innerHTML = html;
    $('#velo').classList.remove('opacity-0', 'pointer-events-none');
    $('#sheet').classList.remove('translate-y-full', 'md:translate-y-[120%]', 'invisible'); clearTimeout(ocultarT);
  }
  function cerrar() {
    $('#velo').classList.add('opacity-0', 'pointer-events-none');
    $('#sheet').classList.add('translate-y-full', 'md:translate-y-[120%]');
    clearTimeout(ocultarT); ocultarT = setTimeout(() => $('#sheet').classList.add('invisible'), 300);
  }
  $('#velo').onclick = cerrar;
  document.addEventListener('keydown', e => { if (e.key === 'Escape') cerrar(); });

  // ---------------------------------------------------------------- fechas
  const ymd = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const deYmd = s => { const [a, m, d] = s.split('-').map(Number); return new Date(a, m - 1, d); };
  const sumarDias = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
  const fLarga = d => d.toLocaleDateString('es-VE', { weekday: 'long', day: 'numeric', month: 'short', year: 'numeric' });
  const fCorta = d => d.toLocaleDateString('es-VE', { day: 'numeric', month: 'short' });
  const RANGOS = [['hoy', 'Hoy'], ['ayer', 'Ayer'], ['semana', 'Esta semana'], ['mes', 'Este mes'], ['mesAnt', 'Mes pasado']];
  function calcRango(r) {
    const hoy = new Date(); hoy.setHours(0, 0, 0, 0);
    if (r.tipo === 'hoy') return { d: hoy, h: hoy };
    if (r.tipo === 'ayer') { const a = sumarDias(hoy, -1); return { d: a, h: a }; }
    if (r.tipo === 'semana') { const lun = sumarDias(hoy, -((hoy.getDay() + 6) % 7)); return { d: lun, h: hoy }; }
    if (r.tipo === 'mes') return { d: new Date(hoy.getFullYear(), hoy.getMonth(), 1), h: hoy };
    if (r.tipo === 'mesAnt') return { d: new Date(hoy.getFullYear(), hoy.getMonth() - 1, 1), h: new Date(hoy.getFullYear(), hoy.getMonth(), 0) };
    return { d: deYmd(r.desde), h: deYmd(r.hasta) };
  }
  // período con el que se compara: el mismo día de la semana pasada, la semana pasada, el mes pasado…
  function rangoAnterior(r, x) {
    if (r.tipo === 'hoy' || r.tipo === 'ayer') return { d: sumarDias(x.d, -7), h: sumarDias(x.h, -7), txt: 'vs. el ' + x.d.toLocaleDateString('es-VE', { weekday: 'long' }) + ' pasado' };
    if (r.tipo === 'semana') return { d: sumarDias(x.d, -7), h: sumarDias(x.h, -7), txt: 'vs. los mismos días de la semana pasada' };
    if (r.tipo === 'mes' || r.tipo === 'mesAnt') {
      const d = new Date(x.d.getFullYear(), x.d.getMonth() - 1, 1);
      const fin = new Date(x.d.getFullYear(), x.d.getMonth(), 0);
      const h = new Date(Math.min(new Date(d.getFullYear(), d.getMonth(), x.h.getDate()), fin));
      return { d, h, txt: r.tipo === 'mes' ? 'vs. los mismos días del mes pasado' : 'vs. el mes anterior' };
    }
    const dias = Math.round((x.h - x.d) / 864e5) + 1;
    return { d: sumarDias(x.d, -dias), h: sumarDias(x.h, -dias), txt: 'vs. el período anterior' };
  }
  const textoRango = x => +x.d === +x.h ? fLarga(x.d) : `${fCorta(x.d)} al ${fCorta(x.h)} ${x.h.getFullYear()}`;

  // ---------------------------------------------------------------- consultas por tienda (de a varias)
  async function enPool(items, n, fn) {
    let i = 0;
    await Promise.all(Array.from({ length: Math.min(n, items.length) }, async () => { while (i < items.length) { const k = i++; await fn(items[k], k); } }));
  }
  // Como máximo 3 consultas a tiendas a la vez en todo la app (ventas, tendencia, comparación y cierres juntas):
  // el navegador abre pocas conexiones por servidor y así siempre queda lugar para el chat y lo demás.
  const CARRILES = 3; let ocupados = 0; const cola = [];
  const tomarCarril = () => new Promise(res => { if (ocupados < CARRILES) { ocupados++; res(); } else cola.push(res); });
  const soltarCarril = () => { const sig = cola.shift(); if (sig) sig(); else ocupados--; };
  async function porTienda(ruta, tienda, x, signal) {
    await tomarCarril();
    try { return await porTiendaUna(ruta, tienda, x, signal); } finally { soltarCarril(); }
  }
  async function porTiendaUna(ruta, tienda, x, signal) {
    if (signal && signal.aborted) throw new ErrorApi(0, 'Cancelado');
    for (let intento = 0; ; intento++) {
      try { return await api(ruta, { sucursal: tienda, desde: ymd(x.d), hasta: ymd(x.h) }, { signal, timeout: 10 * 60e3 }); }
      catch (e) {
        if (e.codigo === 503 && intento < 2 && !(signal && signal.aborted)) { await new Promise(r => setTimeout(r, 4000)); continue; }
        throw e;
      }
    }
  }

  // ---------------------------------------------------------------- tiendas seleccionadas
  let seleccion = new Set();
  function cargarSeleccion() {
    let g = null; try { g = JSON.parse(leer('rpos.tiendas.' + S.usuario) || 'null'); } catch (e) { }
    const validas = Array.isArray(g) ? g.filter(t => S.sucursales.includes(t)) : [];
    seleccion = new Set(validas.length ? validas : S.sucursales);
  }
  const guardarSeleccion = () => guardar('rpos.tiendas.' + S.usuario, JSON.stringify([...seleccion]));
  const tiendasSel = () => S.sucursales.filter(t => seleccion.has(t));
  const textoTiendas = () => seleccion.size === S.sucursales.length ? (S.sucursales.length === 1 ? S.sucursales[0] : `Todas las tiendas (${S.sucursales.length})`) : seleccion.size === 1 ? [...seleccion][0] : `${seleccion.size} tiendas`;

  function abrirFiltros() {
    const r = vista === 'cierres' ? rC : rV, x = calcRango(r);
    const chip = t => `<button type="button" data-t="${esc(t)}" class="h-9 px-3 rounded-full text-[13px] font-medium border ${seleccion.has(t) ? 'bg-soft border-p300 text-softfg' : 'bg-surface border-line text-muted'}">${seleccion.has(t) ? '✓ ' : ''}${esc(t)}</button>`;
    abrir(`
      <div class="flex items-center justify-between py-2"><h3 class="text-[17px] font-semibold">Tiendas y fechas</h3>
        <button class="text-sm text-p700 font-semibold" id="fTodas"></button></div>
      <div class="text-xs uppercase tracking-[.1em] text-muted font-semibold mt-2 mb-2">Fechas</div>
      <div class="grid grid-cols-2 gap-2">
        <label class="text-xs text-muted">Desde<input id="fDesde" type="date" value="${ymd(x.d)}" max="${ymd(new Date())}" class="campo mt-1 !h-11 !text-sm"></label>
        <label class="text-xs text-muted">Hasta<input id="fHasta" type="date" value="${ymd(x.h)}" max="${ymd(new Date())}" class="campo mt-1 !h-11 !text-sm"></label>
      </div>
      <div class="text-xs uppercase tracking-[.1em] text-muted font-semibold mt-5 mb-2">Tiendas <span class="num normal-case tracking-normal" id="fCnt"></span></div>
      <div class="flex flex-wrap gap-2" id="fTiendas">${S.sucursales.map(chip).join('')}</div>
      <p id="fErr" class="text-sm text-bad mt-3" hidden></p>
      <button id="fAplicar" class="mt-5 w-full h-12 rounded-xl bg-p600 text-onp font-semibold"></button>`);
    const sel = new Set(seleccion);
    const act = () => {
      $('#fCnt').textContent = `(${sel.size} de ${S.sucursales.length})`;
      $('#fTodas').textContent = sel.size === S.sucursales.length ? 'Quitar todas' : 'Todas';
      $('#fAplicar').textContent = sel.size ? `Ver ${sel.size === 1 ? '1 tienda' : sel.size + ' tiendas'}` : 'Elija al menos una tienda';
    };
    act();
    $('#fTiendas').onclick = e => {
      const b = e.target.closest('[data-t]'); if (!b) return; const t = b.dataset.t;
      sel.has(t) ? sel.delete(t) : sel.add(t);
      b.outerHTML = `<button type="button" data-t="${esc(t)}" class="h-9 px-3 rounded-full text-[13px] font-medium border ${sel.has(t) ? 'bg-soft border-p300 text-softfg' : 'bg-surface border-line text-muted'}">${sel.has(t) ? '✓ ' : ''}${esc(t)}</button>`;
      act();
    };
    $('#fTodas').onclick = () => {
      if (sel.size === S.sucursales.length) sel.clear(); else S.sucursales.forEach(t => sel.add(t));
      $('#fTiendas').innerHTML = S.sucursales.map(t => `<button type="button" data-t="${esc(t)}" class="h-9 px-3 rounded-full text-[13px] font-medium border ${sel.has(t) ? 'bg-soft border-p300 text-softfg' : 'bg-surface border-line text-muted'}">${sel.has(t) ? '✓ ' : ''}${esc(t)}</button>`).join('');
      act();
    };
    $('#fAplicar').onclick = () => {
      if (!sel.size) return;
      const d = $('#fDesde').value, h = $('#fHasta').value;
      if (!d || !h || h < d) { $('#fErr').textContent = 'La fecha Desde no puede ser posterior a Hasta.'; $('#fErr').hidden = false; return; }
      if ((deYmd(h) - deYmd(d)) / 864e5 > 400) { $('#fErr').textContent = 'El rango máximo es de 400 días.'; $('#fErr').hidden = false; return; }
      seleccion = sel; guardarSeleccion();
      const x2 = calcRango(r);
      const nuevo = (d === ymd(x2.d) && h === ymd(x2.h)) ? r : { tipo: 'otro', desde: d, hasta: h };
      if (vista === 'cierres') rC = nuevo; else rV = nuevo;
      cerrar(); cargarVista(true);
    };
  }
  $('#btnFiltro').onclick = abrirFiltros;

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

  // =========================================================================== VENTAS
  let rV = { tipo: 'hoy' }, ordenV = 'usd', tiendaSel = null;
  let ventas = { clave: '', datos: new Map(), comp: '', txtComp: '' }, ctlV = null;
  $('#rangosV').addEventListener('click', e => {
    const b = e.target.closest('[data-r]'); if (!b) return;
    if (b.dataset.r === 'otro') { abrirFiltros(); return; }
    rV = { tipo: b.dataset.r }; cargarVentas(true);
  });
  $$('.ord').forEach(b => b.onclick = () => { ordenV = b.dataset.orden; pintarVentas(); });

  async function cargarVentas(forzar) {
    chipsRango('#rangosV', rV); encabezado();
    const x = calcRango(rV), xa = rangoAnterior(rV, x), tiendas = tiendasSel();
    const clave = ymd(x.d) + ymd(x.h) + tiendas.join('|');
    if (!forzar && ventas.clave === clave && ventas.datos.size) { pintarVentas(); requestAnimationFrame(pintarTendencia); return; }
    if (ctlV) ctlV.abort();
    const ctl = ctlV = new AbortController();
    const datos = new Map(tiendas.map(t => [t, { estado: 'cargando' }]));
    ventas = { clave, datos, txtComp: xa.txt };
    pintarVentas(); cargando(true);
    try {
      await enPool(tiendas, CONSULTAS_A_LA_VEZ, async t => {
        try { const r = await porTienda('/api/resumen', t, x, ctl.signal); datos.set(t, { estado: 'ok', r }); }
        catch (e) { if (ctl.signal.aborted) return; datos.set(t, { estado: 'error', error: e.message }); }
        if (ctlV === ctl) pintarVentas();
      });
      cargarTendencia(x, tiendas, ctl);
      // la comparación va después, para que lo principal salga primero
      await enPool(tiendas.filter(t => datos.get(t).estado === 'ok'), CONSULTAS_A_LA_VEZ, async t => {
        try { const r = await porTienda('/api/resumen', t, xa, ctl.signal); datos.get(t).prev = r; } catch (e) { }
        if (ctlV === ctl) pintarVentas();
      });
    } finally { cargando(false); }
    if (ctlV === ctl && rV.tipo === 'hoy') cargarTasa(false).then(() => ctlV === ctl && pintarVentas());
  }

  // ---------------------------------------------------------------- tendencia (14 días, /api/diario)
  let tend = { clave: '', dias: [], cargando: false, faltan: 0, sinPermiso: false };
  async function cargarTendencia(x, tiendas, ctl) {
    if (!tiene('diario')) { tend = { clave: '', dias: [], sinPermiso: true }; pintarTendencia(); return; }
    const hasta = x.h, desde = sumarDias(hasta, -13), clave = ymd(desde) + ymd(hasta) + tiendas.join('|');
    if (tend.clave === clave && tend.dias.length && !tend.faltan) { pintarTendencia(); return; }
    const mapa = new Map();
    for (let d = new Date(desde); d <= hasta; d = sumarDias(d, 1)) mapa.set(ymd(d), { fecha: ymd(d), neta: 0, fac: 0, tiendas: 0 });
    tend = { clave, dias: [...mapa.values()], cargando: true, faltan: tiendas.length, errores: 0 };
    pintarTendencia();
    await enPool(tiendas, CONSULTAS_A_LA_VEZ, async t => {
      try {
        const r = await porTienda('/api/diario', t, { d: desde, h: hasta }, ctl.signal);
        (r.filas || []).forEach(f => { const d = mapa.get(String(f[0]).substring(0, 10)); if (d) { d.neta += +f[5] || 0; d.fac += +f[1] || 0; d.tiendas++; } });
      } catch (e) { if (ctl.signal.aborted) return; tend.errores++; }
      tend.faltan--;
      if (ctlV === ctl) pintarTendencia();
    });
    tend.cargando = false;
    if (ctlV === ctl) pintarTendencia();
  }
  const compacto = v => Math.abs(v) >= 1e6 ? '$' + n1.format(v / 1e6) + ' M' : Math.abs(v) >= 1e3 ? '$' + n0.format(v / 1e3) + ' k' : '$' + n0.format(v);
  function pintarTendencia() {
    const cont = $('#tendencia'); if (!cont) return;
    if (tend.sinPermiso) { cont.innerHTML = '<div class="h-full grid place-items-center text-[13px] text-inkmuted">La tendencia necesita el permiso de Ventas diarias.</div>'; $('#tendNota').textContent = ''; return; }
    const dias = tend.dias || [];
    $('#tendNota').textContent = tend.faltan > 0 ? `cargando ${tend.faltan} tiendas…` : tend.errores ? `${tend.errores} tiendas sin datos` : 'todas las tiendas seleccionadas';
    if (!dias.length) { cont.innerHTML = ''; return; }
    const W = Math.max(280, cont.clientWidth), H = cont.clientHeight || 160, pl = 44, pr = 10, pt = 10, pb = 24;
    const max = Math.max(1, ...dias.map(d => d.neta));
    const paso = Math.pow(10, Math.floor(Math.log10(max))), tope = Math.ceil(max / paso / (max / paso > 5 ? 2 : 1)) * paso * (max / paso > 5 ? 2 : 1);
    const X = i => pl + (dias.length === 1 ? 0 : i * (W - pl - pr) / (dias.length - 1)), Y = v => pt + (1 - v / tope) * (H - pt - pb);
    const hoyStr = ymd(new Date()), n = dias.length, enCurso = dias[n - 1].fecha === hoyStr;
    const pts = dias.map((d, i) => [X(i), Y(d.neta)]);
    const linea = (a, b) => pts.slice(a, b).map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
    const firme = enCurso ? n - 1 : n;
    const grid = [0, .5, 1].map(f => { const v = tope * f, y = Y(v); return `<line x1="${pl}" x2="${W - pr}" y1="${y}" y2="${y}" stroke="rgb(var(--inkline))" stroke-width="1" ${f ? 'stroke-dasharray="2 4"' : ''}/><text x="${pl - 8}" y="${y + 4}" text-anchor="end">${compacto(v)}</text>`; }).join('');
    const etiquetas = dias.map((d, i) => (i % (W < 500 ? 3 : 2) === (n - 1) % (W < 500 ? 3 : 2)) ? `<text x="${X(i)}" y="${H - 6}" text-anchor="${i === n - 1 ? 'end' : i === 0 ? 'start' : 'middle'}">${deYmd(d.fecha).toLocaleDateString('es-VE', { day: 'numeric', month: 'short' }).replace('.', '')}</text>` : '').join('');
    const area = `M${pts[0][0]} ${Y(0)} ` + pts.map(p => `L${p[0].toFixed(1)} ${p[1].toFixed(1)}`).join(' ') + ` L${pts[n - 1][0]} ${Y(0)} Z`;
    const ult = pts[n - 1];
    cont.innerHTML = `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Venta neta por día, últimos 14 días">
        <defs><linearGradient id="gArea" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="rgb(var(--p500))" stop-opacity=".32"/><stop offset="1" stop-color="rgb(var(--p500))" stop-opacity="0"/></linearGradient></defs>
        ${grid}${etiquetas}
        <path d="${area}" fill="url(#gArea)"/>
        <path d="${linea(0, firme)}" fill="none" stroke="rgb(var(--p300))" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>
        ${enCurso && n > 1 ? `<path d="M${pts[n - 2][0]} ${pts[n - 2][1]} L${ult[0]} ${ult[1]}" fill="none" stroke="rgb(var(--p300))" stroke-width="2" stroke-dasharray="3 4" stroke-linecap="round"/>` : ''}
        <circle cx="${ult[0]}" cy="${ult[1]}" r="4.5" fill="${enCurso ? 'rgb(var(--ink))' : 'rgb(var(--p300))'}" stroke="rgb(var(--p300))" stroke-width="2"/>
        <line id="tCruz" y1="${pt}" y2="${H - pb}" stroke="rgb(var(--inkmuted))" stroke-width="1" visibility="hidden"/>
        <circle id="tPunto" r="4.5" fill="rgb(var(--p300))" stroke="rgb(var(--ink))" stroke-width="2" visibility="hidden"/>
        <rect x="${pl}" y="0" width="${W - pl - pr}" height="${H}" fill="transparent" id="tZona"/>
      </svg><div id="tTip" class="tip" hidden></div>`;
    const zona = $('#tZona'), tip = $('#tTip'), cruz = $('#tCruz'), punto = $('#tPunto');
    const mover = ev => {
      const caja = cont.getBoundingClientRect(), x = ev.clientX - caja.left;
      let i = Math.round((x - pl) / ((W - pl - pr) / Math.max(1, n - 1))); i = Math.max(0, Math.min(n - 1, i));
      const d = dias[i], px = X(i), py = Y(d.neta);
      cruz.setAttribute('x1', px); cruz.setAttribute('x2', px); cruz.setAttribute('visibility', 'visible');
      punto.setAttribute('cx', px); punto.setAttribute('cy', py); punto.setAttribute('visibility', 'visible');
      const ant = i > 0 ? dias[i - 1].neta : null, v = ant ? (d.neta - ant) / ant * 100 : null;
      tip.innerHTML = `<div class="text-muted mb-1">${esc(fLarga(deYmd(d.fecha)))}${d.fecha === hoyStr ? ' · en curso' : ''}</div>
        <div class="flex justify-between gap-4"><span>Venta neta</span><b class="num">${usd(d.neta)}</b></div>
        <div class="flex justify-between gap-4"><span>Facturas</span><b class="num">${n0.format(d.fac)}</b></div>
        ${v == null ? '' : `<div class="flex justify-between gap-4 items-center mt-1"><span>vs. día anterior</span>${chipVar(v)}</div>`}`;
      tip.hidden = false;
      const tw = tip.offsetWidth; tip.style.left = Math.max(0, Math.min(W - tw, px + 12 > W - tw ? px - tw - 12 : px + 12)) + 'px'; tip.style.top = '0px';
    };
    const salirT = () => { tip.hidden = true; cruz.setAttribute('visibility', 'hidden'); punto.setAttribute('visibility', 'hidden'); };
    zona.addEventListener('pointermove', mover); zona.addEventListener('pointerdown', mover); zona.addEventListener('pointerleave', salirT);
  }
  let tResize; window.addEventListener('resize', () => { clearTimeout(tResize); tResize = setTimeout(() => vista === 'ventas' && pintarTendencia(), 150); });

  const variacion = d => d.prev && d.prev.ventaNeta > 0 ? (d.r.ventaNeta - d.prev.ventaNeta) / d.prev.ventaNeta * 100 : null;

  function detalleVentas(t, d) {
    const v = variacion(d), r = d.r, p = d.prev;
    const fila = (a, b, c) => `<div class="flex justify-between py-2 border-b border-line last:border-0"><span class="text-muted">${a}</span><span class="num font-medium">${b}${c ? `<span class="text-xs text-muted ml-1.5">${c}</span>` : ''}</span></div>`;
    return `
      <div class="flex items-start justify-between gap-3">
        <div class="min-w-0"><div class="text-xs text-muted font-medium">Tienda</div><h3 class="text-xl font-semibold tracking-tight truncate">${esc(t)}</h3></div>
        ${chipVar(v, 'shrink-0')}
      </div>
      <div class="grid grid-cols-3 gap-2 mt-4 num">
        <div class="rounded-xl bg-raised p-3 min-w-0"><div class="text-[11px] text-muted">Venta neta</div><div class="font-semibold truncate">${usd(r.ventaNeta)}</div></div>
        <div class="rounded-xl bg-raised p-3"><div class="text-[11px] text-muted">Facturas</div><div class="font-semibold">${n0.format(r.facturas)}</div></div>
        <div class="rounded-xl bg-raised p-3 min-w-0"><div class="text-[11px] text-muted">Ticket</div><div class="font-semibold truncate">${usd(r.facturas ? r.ventaNeta / r.facturas : 0)}</div></div>
      </div>
      <div class="mt-4 text-sm">
        ${fila('Venta bruta', usd(r.ventaBruta))}
        ${fila('Devoluciones', usd(r.devoluciones), n0.format(r.notasCredito) + ' notas de crédito')}
        ${fila('Unidades', n2.format(r.unidades))}
        ${tasa ? fila('Venta en Bs (aprox.)', 'Bs ' + n2.format(r.ventaNeta * tasa.tasa)) : ''}
        ${p ? fila('Período anterior', usd(p.ventaNeta), esc(ventas.txtComp.replace('vs. ', ''))) : ''}
      </div>
      ${tiene('cierres') ? `<button class="mt-4 w-full h-11 rounded-xl border border-line text-sm font-semibold hover:border-p300" data-cierres-de="${esc(t)}">Ver cierres de caja de ${esc(t)}</button>` : ''}`;
  }

  function pintarVentas() {
    const todos = [...ventas.datos.entries()].map(([t, d]) => ({ t, ...d }));
    const ok = todos.filter(x => x.estado === 'ok'), errores = todos.filter(x => x.estado === 'error'), pend = todos.filter(x => x.estado === 'cargando');
    const sum = k => ok.reduce((a, x) => a + (x.r[k] || 0), 0);
    const tot = sum('ventaNeta'), fac = sum('facturas');
    const conPrev = ok.filter(x => x.prev);
    const totPrev = conPrev.reduce((a, x) => a + x.prev.ventaNeta, 0), totAct = conPrev.reduce((a, x) => a + x.r.ventaNeta, 0);
    $('#kTotal').textContent = ok.length ? n2.format(tot) : (pend.length ? '…' : '—');
    $('#kBs').textContent = ok.length && tasa ? `≈ Bs ${n2.format(tot * tasa.tasa)} a ${n2.format(tasa.tasa)} Bs/USD` : '';
    const v = conPrev.length && conPrev.length === ok.length && totPrev > 0 ? (totAct - totPrev) / totPrev * 100 : null;
    $('#kVar').hidden = v == null;
    if (v != null) { $('#kVar').className = 'chip ' + (Math.abs(v) < 0.05 ? 'chip-neutro' : v > 0 ? 'chip-ok' : 'chip-bad'); $('#kVar').innerHTML = (Math.abs(v) < 0.05 ? '' : v > 0 ? FLECHA_ARRIBA : FLECHA_ABAJO) + pct(v); }
    $('#kComp').innerHTML = v == null ? '' : `<div>${esc(ventas.txtComp.replace(/^vs\. /, 'Comparado con '))}</div><div class="text-onink font-semibold">${usd(totPrev)}</div>`;
    $('#kFac').textContent = ok.length ? n0.format(fac) : '—';
    $('#kNc').textContent = ok.length ? n0.format(sum('notasCredito')) + ' notas de crédito' : '';
    $('#kTicket').textContent = ok.length ? usd(fac ? tot / fac : 0) : '—';
    $('#kDev').textContent = ok.length ? usd(sum('devoluciones')) : '—';
    $('#kBruta').textContent = ok.length ? 'de ' + usd(sum('ventaBruta')) + ' brutos' : '';
    $('#kTiendas').textContent = ok.length;
    $('#kTiendasDe').textContent = ' /' + todos.length;
    $('#kTiendasNota').innerHTML = pend.length ? `<span class="text-p700">consultando ${pend.length}…</span>` : errores.length ? `<span class="text-bad">${errores.length} sin respuesta</span>` : '<span class="text-ok">todas respondieron</span>';

    $('#erroresV').hidden = !errores.length; $('#erroresV').innerHTML = errorTiendas(errores);

    const lista = [...ok].sort((a, b) => ordenV === 'usd' ? b.r.ventaNeta - a.r.ventaNeta : (variacion(b) ?? -1e9) - (variacion(a) ?? -1e9));
    const max = Math.max(1, ...ok.map(x => x.r.ventaNeta));
    $$('.ord').forEach(b => b.setAttribute('aria-pressed', b.dataset.orden === ordenV));
    const varChip = x => { const v = variacion(x); return v == null ? `<span class="text-xs text-muted">${x.prev ? '—' : '…'}</span>` : chipVar(v); };
    const esqueleto = pend.map(x => `<li class="tarjeta p-3.5 flex items-center gap-3"><span class="esqueleto w-8 h-8"></span><span class="flex-1"><span class="flex justify-between"><span class="font-semibold text-muted">${esc(x.t)}</span><span class="esqueleto w-20 h-4"></span></span><span class="block esqueleto h-1.5 mt-2"></span></span></li>`).join('');
    $('#listaTiendas').innerHTML = lista.map((x, i) => `
      <li><button data-tienda="${esc(x.t)}" class="w-full text-left tarjeta p-3.5 flex items-center gap-3 active:scale-[.99] transition">
        <span class="num grid place-items-center shrink-0 w-8 h-8 rounded-lg text-xs font-bold ${i < 3 && ordenV === 'usd' ? 'bg-p600 text-onp' : 'bg-raised text-muted'}">${i + 1}</span>
        <span class="min-w-0 flex-1">
          <span class="flex items-baseline justify-between gap-2"><span class="font-semibold truncate">${esc(x.t)}</span><span class="num font-semibold shrink-0">${usd(x.r.ventaNeta)}</span></span>
          <span class="mt-1.5 flex items-center gap-2">
            <span class="flex-1 h-1.5 rounded-full bg-raised overflow-hidden"><span class="block h-full rounded-full bg-p500" style="width:${(Math.max(0, x.r.ventaNeta) / max * 100).toFixed(1)}%"></span></span>
            <span class="w-[76px] text-right shrink-0">${varChip(x)}</span>
          </span>
          <span class="num text-xs text-muted">${n0.format(x.r.facturas)} facturas · ticket ${usd(x.r.facturas ? x.r.ventaNeta / x.r.facturas : 0)}</span>
        </span>
      </button></li>`).join('') + esqueleto + (todos.length === 0 ? '<li class="text-sm text-muted p-4">No tiene tiendas asignadas.</li>' : '');
    $('#tablaTiendas').innerHTML = lista.map(x => `
      <tr data-tienda="${esc(x.t)}" class="border-t border-line cursor-pointer ${tiendaSel === x.t ? 'bg-tint' : 'hover:bg-raised'}">
        <td class="px-4 py-3 font-semibold font-sans">${esc(x.t)}</td>
        <td class="px-3 text-right"><div class="font-semibold">${usd(x.r.ventaNeta)}</div><div class="h-1 rounded-full bg-raised mt-1 ml-auto w-24 overflow-hidden"><div class="h-full bg-p500 rounded-full" style="width:${(Math.max(0, x.r.ventaNeta) / max * 100).toFixed(1)}%"></div></div></td>
        <td class="px-3 text-right">${n0.format(x.r.facturas)}</td>
        <td class="px-3 text-right">${usd(x.r.facturas ? x.r.ventaNeta / x.r.facturas : 0)}</td>
        <td class="px-3 text-right text-muted">${n0.format(x.r.unidades)}</td>
        <td class="px-4 text-right">${varChip(x)}</td>
      </tr>`).join('') + pend.map(x => `<tr class="border-t border-line"><td class="px-4 py-3 text-muted font-sans">${esc(x.t)}</td><td colspan="5" class="px-4"><div class="esqueleto h-3"></div></td></tr>`).join('');
    $('#thVar').textContent = ventas.txtComp ? ventas.txtComp.replace(/^vs\. /, 'vs. ').replace('los mismos días de ', '').replace('los mismos días del ', '') : 'Variación';
    const sel = lista.find(x => x.t === tiendaSel) || lista[0];
    $('#detalleLateral').innerHTML = sel ? detalleVentas(sel.t, sel) : '<p class="text-sm text-muted">Elija una tienda para ver el detalle.</p>';
  }
  document.addEventListener('click', e => {
    const b = e.target.closest('[data-tienda]'); if (!b) return;
    tiendaSel = b.dataset.tienda;
    const d = ventas.datos.get(tiendaSel); if (!d || d.estado !== 'ok') return;
    if (window.matchMedia('(min-width: 1024px)').matches) pintarVentas(); else abrir(detalleVentas(tiendaSel, d));
  });
  document.addEventListener('click', e => {
    const b = e.target.closest('[data-cierres-de]'); if (!b) return;
    seleccion = new Set([b.dataset.cierresDe]); guardarSeleccion();
    rC = Object.assign({}, rV); filtroCaja = 'todas'; cerrar(); ir('cierres');
  });

  // =========================================================================== CIERRES
  let rC = { tipo: 'hoy' }, filtroCaja = 'todas', ctlC = null;
  let cierres = { clave: '', datos: new Map() };
  $('#rangosC').addEventListener('click', e => {
    const b = e.target.closest('[data-r]'); if (!b) return;
    if (b.dataset.r === 'otro') { abrirFiltros(); return; }
    rC = { tipo: b.dataset.r }; cargarCierres(true);
  });
  $('#fCierre').addEventListener('click', e => { const b = e.target.closest('[data-fc]'); if (b) { filtroCaja = b.dataset.fc; pintarCierres(); } });

  // fila de /api/cierres → objeto (mismo orden que ConsultaCierres en la API)
  function leerCierre(t, f, formas, signos) {
    const c = {
      tienda: t, nro: f[0], fecha: f[1], caja: f[2], apertura: f[3], codCajero: f[4], cajero: f[5],
      tasa: +f[6] || 0, total: +f[9] || 0, total1: +f[10] || 0, recUsd: +f[19] || 0, facturas: +f[20] || 0, notas: +f[21] || 0
    };
    c.dif = c.total1 - c.total;
    c.difUsd = c.tasa > 0 ? c.dif / c.tasa : 0;
    const tol = c.tasa > 0 ? TOLERANCIA_USD * c.tasa : 1;
    c.estado = Math.abs(c.dif) <= tol ? 'ok' : c.dif < 0 ? 'falt' : 'sob';
    // forma de pago con la mayor diferencia
    let peor = null;
    formas.forEach((nombre, i) => {
      if (!signos[i]) return;
      const d = +f[22 + i * 3 + 2] || 0;
      if (Math.abs(d) > 0.009 && (!peor || Math.abs(d) > Math.abs(peor.d))) peor = { nombre, d };
    });
    c.peor = peor;
    return c;
  }

  async function cargarCierres(forzar) {
    chipsRango('#rangosC', rC); encabezado();
    const x = calcRango(rC), tiendas = tiendasSel();
    const clave = ymd(x.d) + ymd(x.h) + tiendas.join('|');
    if (!forzar && cierres.clave === clave && cierres.datos.size) { pintarCierres(); return; }
    if (ctlC) ctlC.abort();
    const ctl = ctlC = new AbortController();
    const datos = new Map(tiendas.map(t => [t, { estado: 'cargando' }]));
    cierres = { clave, datos };
    pintarCierres(); cargando(true);
    try {
      await enPool(tiendas, CONSULTAS_A_LA_VEZ, async t => {
        try {
          const r = await porTienda('/api/cierres', t, x, ctl.signal);
          const lista = (r.cierres || []).map(f => leerCierre(t, f, r.formas || [], r.signos || []));
          const sin = (r.sinCierre || []).map(f => ({ tienda: t, caja: f[0], apertura: f[1], codCajero: f[2], cajero: f[3], primera: f[4], ultima: f[5], facturas: +f[6] || 0, usd: +f[7] || 0 }));
          datos.set(t, { estado: 'ok', lista, sin, sinDisponible: r.sinCierreDisponible !== false });
        } catch (e) { if (ctl.signal.aborted) return; datos.set(t, { estado: 'error', error: e.message }); }
        if (ctlC === ctl) pintarCierres();
      });
    } finally { cargando(false); }
  }

  const hora = s => (s || '').length >= 16 ? s.substring(11, 16) : '';
  const dia = s => { const d = (s || '').substring(0, 10); return d ? deYmd(d).toLocaleDateString('es-VE', { day: 'numeric', month: 'short' }) : ''; };
  function pintarCierres() {
    const todos = [...cierres.datos.entries()].map(([t, d]) => ({ t, ...d }));
    const ok = todos.filter(x => x.estado === 'ok'), errores = todos.filter(x => x.estado === 'error'), pend = todos.filter(x => x.estado === 'cargando');
    const cs = ok.flatMap(x => x.lista), sin = ok.flatMap(x => x.sin);
    const varios = +calcRango(rC).d !== +calcRango(rC).h;
    $('#cOk').textContent = ok.length ? cs.filter(c => c.estado === 'ok').length : '—';
    $('#cBad').textContent = ok.length ? cs.filter(c => c.estado !== 'ok').length : '—';
    $('#cSin').textContent = ok.length ? sin.length : '—';
    const neta = cs.reduce((a, c) => a + c.difUsd, 0);
    const falt = cs.filter(c => c.estado === 'falt').reduce((a, c) => a + c.difUsd, 0), sob = cs.filter(c => c.estado === 'sob').reduce((a, c) => a + c.difUsd, 0);
    $('#cNeta').innerHTML = ok.length ? `<span class="${neta < -TOLERANCIA_USD ? 'text-bad' : neta > TOLERANCIA_USD ? 'text-warn' : 'text-ok'}">${firmado(neta)} USD</span>` : (pend.length ? '…' : '—');
    $('#cDet').innerHTML = ok.length ? `Faltantes <b class="text-bad">${n2.format(Math.abs(falt))}</b><br>Sobrantes <b class="text-warn">${n2.format(sob)}</b><br>${n0.format(cs.length)} cierres` : '';
    $('#erroresC').hidden = !errores.length; $('#erroresC').innerHTML = errorTiendas(errores);

    const F = [['todas', 'Todas'], ['bad', 'Descuadre'], ['sin', 'Sin cierre'], ['ok', 'Cuadran']];
    $('#fCierre').innerHTML = F.map(([k, n]) => `<button data-fc="${k}" aria-pressed="${filtroCaja === k}">${n}</button>`).join('');
    // barra de estado de las cajas (cuadran / faltante / sobrante / sin cierre)
    const nOk = cs.filter(c => c.estado === 'ok').length, nF = cs.filter(c => c.estado === 'falt').length, nS = cs.filter(c => c.estado === 'sob').length, nSin = sin.length, nT = nOk + nF + nS + nSin;
    $('#cBarra').innerHTML = nT ? [[nOk, 'bg-okf', 'Cuadran'], [nS, 'bg-warnf', 'Sobrante'], [nF, 'bg-badf', 'Faltante'], [nSin, 'bg-serf', 'Sin cierre X']].filter(x => x[0]).map(x => `<span class="${x[1]} h-full" style="width:${(x[0] / nT * 100).toFixed(2)}%" title="${x[2]}: ${x[0]}"></span>`).join('') : '';
    $('#cBarraNota').innerHTML = nT ? [[nOk, 'bg-okf', 'cuadran'], [nS, 'bg-warnf', 'sobrante'], [nF, 'bg-badf', 'faltante'], [nSin, 'bg-serf', 'sin cierre']].map(x => `<span class="inline-flex items-center gap-1 ml-2.5 num"><span class="w-2 h-2 rounded-full ${x[1]}"></span>${x[0]} ${x[2]}</span>`).join('') : '';
    const items = [];
    if (filtroCaja === 'todas' || filtroCaja === 'sin') sin.forEach(s => items.push({ tipo: 'sin', s, peso: 1, abs: s.usd }));
    cs.forEach(c => {
      const grupo = c.estado === 'ok' ? 'ok' : 'bad';
      if (filtroCaja === 'todas' || filtroCaja === grupo) items.push({ tipo: 'c', c, peso: grupo === 'bad' ? 0 : 2, abs: Math.abs(c.difUsd) });
    });
    items.sort((a, b) => a.peso - b.peso || b.abs - a.abs);
    const MAX = 150;
    $('#listaCajas').innerHTML = items.slice(0, MAX).map(it => {
      if (it.tipo === 'sin') {
        const s = it.s;
        return `<li class="relative tarjeta p-3.5 pl-4 overflow-hidden"><span class="absolute left-0 inset-y-0 w-1 bg-serf"></span>
          <div class="flex items-start justify-between gap-2">
            <div class="min-w-0"><div class="font-semibold truncate">${esc(s.tienda)} · Caja ${esc(s.caja)}</div><div class="text-xs text-muted truncate">${esc(s.cajero || s.codCajero || 'Cajero sin nombre')}</div></div>
            <div class="text-right shrink-0"><div class="num font-semibold">${usd(s.usd)}</div><span class="chip chip-warn mt-1">! Sin cierre X</span></div>
          </div>
          <div class="num text-xs text-muted mt-2">${n0.format(s.facturas)} facturas · ${varios ? dia(s.primera) + ' ' : ''}${hora(s.primera)} a ${hora(s.ultima)} · apertura ${esc(s.apertura)}</div></li>`;
      }
      const c = it.c;
      const est = { ok: ['bg-okf', 'Cuadra', 'text-ok', 'chip-ok', '✓'], falt: ['bg-badf', 'Faltante', 'text-bad', 'chip-bad', '▼'], sob: ['bg-warnf', 'Sobrante', 'text-warn', 'chip-warn', '▲'] }[c.estado];
      return `<li class="relative tarjeta p-3.5 pl-4 overflow-hidden"><span class="absolute left-0 inset-y-0 w-1 ${est[0]}"></span>
        <div class="flex items-start justify-between gap-2">
          <div class="min-w-0"><div class="font-semibold truncate">${esc(c.tienda)} · Caja ${esc(c.caja)}</div><div class="text-xs text-muted truncate">${esc(c.cajero || c.codCajero)} · ${varios ? dia(c.fecha) + ' ' : ''}${hora(c.fecha)}</div></div>
          <div class="text-right shrink-0"><div class="num font-semibold ${est[2]}">${firmado(c.difUsd)} USD</div><span class="chip ${est[3]} mt-1">${est[4]} ${est[1]}</span></div>
        </div>
        <div class="num text-xs text-muted mt-2">Bs ${firmado(c.dif)} · facturado Bs ${n2.format(c.total)}${c.estado !== 'ok' && c.peor ? ` · más en ${esc(c.peor.nombre)}` : ''}</div></li>`;
    }).join('')
      + (items.length > MAX ? `<li class="text-xs text-muted p-3 sm:col-span-2">Se muestran ${MAX} de ${n0.format(items.length)}. Para el detalle completo use el reporte en Excel.</li>` : '')
      + pend.map(x => `<li class="tarjeta p-3.5"><div class="font-semibold text-muted">${esc(x.t)}</div><div class="esqueleto h-3 mt-2"></div></li>`).join('')
      + (!items.length && !pend.length ? `<li class="text-sm text-muted p-4 sm:col-span-2">${ok.length ? 'No hay cierres en este grupo.' : ''}</li>` : '');

    // ranking: cajeros con más faltante acumulado
    const g = new Map();
    cs.forEach(c => {
      const k = c.tienda + '|' + (c.codCajero || c.cajero);
      const x = g.get(k) || { cajero: c.cajero || c.codCajero || 'Sin nombre', tienda: c.tienda, neto: 0, falt: 0, n: 0, malos: 0 };
      x.neto += c.difUsd; x.n++; if (c.estado === 'falt') { x.falt += c.difUsd; x.malos++; }
      g.set(k, x);
    });
    const rk = [...g.values()].filter(x => x.malos > 0).sort((a, b) => a.falt - b.falt).slice(0, 10);
    const mx = Math.max(1, ...rk.map(r => Math.abs(r.falt)));
    $('#ranking').innerHTML = rk.length ? rk.map((r, i) => `
      <li class="flex items-center gap-3">
        <span class="num grid place-items-center shrink-0 w-7 h-7 rounded-full text-xs font-bold ${i === 0 ? 'bg-badbg text-bad' : 'bg-raised text-muted'}">${i + 1}</span>
        <div class="min-w-0 flex-1">
          <div class="flex justify-between gap-2 text-sm"><span class="font-medium truncate">${esc(r.cajero)}</span><span class="num font-semibold text-bad shrink-0">${firmado(r.falt)}</span></div>
          <div class="h-1.5 rounded-full bg-raised mt-1 overflow-hidden"><div class="h-full rounded-full bg-bad/70" style="width:${(Math.abs(r.falt) / mx * 100).toFixed(1)}%"></div></div>
          <div class="text-[11px] text-muted mt-0.5">${esc(r.tienda)} · ${r.malos} de ${r.n} cierres con faltante · neto ${firmado(r.neto)} USD</div>
        </div>
      </li>`).join('') : `<li class="text-sm text-muted">${ok.length ? '✓ Ningún cajero con faltante en el período' : pend.length ? 'Consultando…' : ''}</li>`;

    pintarRed(ok);
  }

  // =========================================================================== RED DE CIERRES
  // Grafo bipartito cajero ↔ caja por tienda: cada cierre es una arista. ¿El faltante es de la persona o de la caja?
  // Mismas reglas que el reporte de Excel (VentasExcelFunctions/RedCierres.cs): si cambia una, cambie la otra.
  const RED_PROP = 0.4, RED_MIN = 3;
  const V = {
    patron: 'Patrón del cajero', caja: 'Problema de la caja', revisar: 'Revisar', expCajero: 'Lo explica el cajero',
    expCaja: 'Lo explica la caja', ocasional: 'Ocasional', pocos: 'Pocos cierres', sin: 'Sin faltantes'
  };
  const gravedad = v => v === V.patron || v === V.caja ? 0 : v === V.revisar ? 1 : v === V.expCajero || v === V.expCaja ? 2 : v === V.ocasional ? 3 : v === V.pocos ? 4 : 5;
  const chipVer = v => gravedad(v) === 0 ? 'chip-bad' : gravedad(v) === 1 ? 'chip-warn' : v === V.sin ? 'chip-ok' : 'chip-neutro';
  const nombreCaja = c => { c = String(c ?? '').trim(); return !c ? '(sin caja)' : /^\d+$/.test(c) ? 'Caja ' + (c.replace(/^0+(?=\d)/, '')) : c; };
  let redTienda = '', redSel = '', redOk = [], redElegida = false;

  function calcularRed(lista) {
    const cajas = new Map(), cajeros = new Map(), aristas = new Map();
    const nodo = (m, k, nombre, esCaja) => m.get(k) || m.set(k, { k, nombre, esCaja, aristas: [], veredicto: '', motivo: '' }).get(k);
    lista.forEach(c => {
      const caja = nodo(cajas, 'k|' + String(c.caja ?? '').trim(), nombreCaja(c.caja), true);
      const id = String(c.codCajero || c.cajero || '').trim();
      const cajero = nodo(cajeros, 'p|' + id, c.cajero || c.codCajero || '(sin cajero)', false);
      const ka = caja.k + '#' + cajero.k;
      let a = aristas.get(ka);
      if (!a) { a = { caja, cajero, n: 0, f: 0, neto: 0, faltUsd: 0 }; aristas.set(ka, a); caja.aristas.push(a); cajero.aristas.push(a); }
      a.n++; a.neto += c.difUsd;
      if (c.estado === 'falt') { a.f++; a.faltUsd += c.difUsd; }
    });
    const todos = [...cajas.values(), ...cajeros.values()];
    todos.forEach(x => {
      x.n = x.aristas.reduce((s, a) => s + a.n, 0); x.f = x.aristas.reduce((s, a) => s + a.f, 0);
      x.neto = x.aristas.reduce((s, a) => s + a.neto, 0); x.faltUsd = x.aristas.reduce((s, a) => s + a.faltUsd, 0);
      x.vecinosF = x.aristas.filter(a => a.f > 0).length; x.prop = x.n ? x.f / x.n : 0;
    });
    // 1. cajeros con patrón propio
    cajeros.forEach(p => {
      if (p.f >= 2 && p.vecinosF >= 2 && p.prop >= RED_PROP) { p.veredicto = V.patron; p.motivo = `Falta en ${p.vecinosF} cajas distintas (${p.f} de ${p.n} cierres): el faltante lo sigue a él, no a la caja.`; }
    });
    // 2. cajas
    cajas.forEach(k => {
      const conF = k.aristas.filter(a => a.f > 0), sinPat = conF.filter(a => a.cajero.veredicto !== V.patron);
      if (!k.f) { k.veredicto = V.sin; k.motivo = 'Todos los cierres cuadran o sobran.'; }
      else if (k.n < RED_MIN) { k.veredicto = V.pocos; k.motivo = `Solo ${k.n} cierres: amplíe el período para opinar.`; }
      else if (sinPat.length >= 2 && k.prop >= RED_PROP) { k.veredicto = V.caja; k.motivo = `Faltan ${sinPat.length} cajeros distintos (${k.f} de ${k.n} cierres) y ninguno tiene un patrón propio en otras cajas: revise la caja (gaveta, punto, fiscal, turno).`; }
      else if (!sinPat.length) { k.veredicto = V.expCajero; k.motivo = 'Los faltantes son de ' + conF.map(a => a.cajero.nombre).join(', ') + ', que también falta en otras cajas.'; }
      else if (sinPat.length === 1 && sinPat[0].f >= 2 && k.prop >= RED_PROP) {
        const a = sinPat[0]; k.veredicto = V.revisar;
        k.motivo = a.cajero.aristas.length === 1
          ? `Solo falta con ${a.cajero.nombre}, que no ha trabajado en otra caja: no se puede separar persona de caja. Rótelo para saberlo.`
          : `Solo falta con ${a.cajero.nombre} (${a.f} de ${a.n}); en otras cajas él cuadra. Puede ser la combinación (turno, horario).`;
      } else { k.veredicto = V.ocasional; k.motivo = `${k.f} de ${k.n} cierres con faltante, sin patrón.`; }
    });
    // 3. resto de cajeros
    cajeros.forEach(p => {
      if (p.veredicto) return;
      const conF = p.aristas.filter(a => a.f > 0);
      if (!conF.length) { p.veredicto = V.sin; p.motivo = 'Todos sus cierres cuadran o sobran.'; }
      else if (conF.every(a => a.caja.veredicto === V.caja)) { p.veredicto = V.expCaja; p.motivo = 'Sus faltantes son en ' + conF.map(a => a.caja.nombre).join(', ') + ', donde también faltan otros cajeros.'; }
      else if (p.f >= 2 && p.prop >= RED_PROP) {
        p.veredicto = V.revisar;
        p.motivo = p.aristas.length === 1
          ? `Falta en ${p.f} de ${p.n} cierres, pero siempre en la misma caja: rótelo a otra caja para separar persona de caja.`
          : `Falta en ${p.f} de ${p.n} cierres, todos en ${conF[0].caja.nombre}; en las otras cajas cuadra.`;
      } else { p.veredicto = V.ocasional; p.motivo = `${p.f} de ${p.n} cierres con faltante, sin patrón.`; }
    });
    // orden de dibujo: cajas por nombre; cajeros por el promedio de la posición de sus cajas (menos cruces)
    const lc = [...cajas.values()].sort((a, b) => a.nombre.localeCompare(b.nombre, 'es', { numeric: true }));
    lc.forEach((k, i) => k.pos = i);
    const lp = [...cajeros.values()].map(p => { p.pos = p.aristas.reduce((s, a) => s + a.caja.pos * a.n, 0) / p.n; return p; })
      .sort((a, b) => a.pos - b.pos || a.nombre.localeCompare(b.nombre, 'es'));
    return { cajas: lc, cajeros: lp, aristas: [...aristas.values()] };
  }

  function pintarRed(ok) {
    redOk = ok;
    const conDatos = ok.filter(x => x.lista.length).map(x => ({ t: x.t, lista: x.lista, f: x.lista.filter(c => c.estado === 'falt').length }))
      .sort((a, b) => b.f - a.f || a.t.localeCompare(b.t, 'es'));
    $('#redCard').hidden = !conDatos.length;
    if (!conDatos.length) return;
    if (!redElegida || !conDatos.some(x => x.t === redTienda)) { if (redTienda !== conDatos[0].t) redSel = ''; redTienda = conDatos[0].t; redElegida = false; }
    $('#redTiendas').innerHTML = conDatos.length > 1 ? conDatos.map(x => `<button data-rt="${esc(x.t)}" aria-pressed="${x.t === redTienda}">${esc(x.t)}${x.f ? ` <span class="text-bad num">${x.f}</span>` : ''}</button>`).join('') : '';
    const r = calcularRed(conDatos.find(x => x.t === redTienda).lista);
    const dias = Math.round((calcRango(rC).h - calcRango(rC).d) / 864e5) + 1;
    $('#redNota').textContent = dias < 14 ? `Período de ${dias} día${dias > 1 ? 's' : ''}: con 2 a 4 semanas el veredicto es más confiable (los cajeros alcanzan a rotar de caja).` : '';

    // ---- grafo SVG
    const FILA = 30, W = 360, xL = 132, xR = 228, nMax = Math.max(r.cajeros.length, r.cajas.length), H = nMax * FILA + 16;
    const yDe = (i, n) => 8 + (nMax - n) * FILA / 2 + i * FILA + FILA / 2;
    r.cajeros.forEach((p, i) => p.y = yDe(i, r.cajeros.length));
    r.cajas.forEach((k, i) => k.y = yDe(i, r.cajas.length));
    const maxN = Math.max(1, ...r.aristas.map(a => a.n));
    const sel = [...r.cajas, ...r.cajeros].find(x => x.k === redSel) || null;
    const vecino = a => !sel || a.caja === sel || a.cajero === sel;
    const color = v => gravedad(v) === 0 ? 'var(--badf)' : gravedad(v) === 1 ? 'var(--warnf)' : v === V.sin ? 'var(--okf)' : 'var(--muted)';
    const corto = (s, n) => s.length > n ? s.slice(0, n - 1) + '…' : s;
    const aristas = r.aristas.slice().sort((a, b) => (a.f > 0) - (b.f > 0)).map(a => {
      const op = vecino(a) ? (a.f ? 0.35 + 0.65 * a.f / a.n : 0.9) : 0.08;
      return `<path d="M${xL} ${a.cajero.y}C${(xL + xR) / 2} ${a.cajero.y} ${(xL + xR) / 2} ${a.caja.y} ${xR} ${a.caja.y}" fill="none"
        style="stroke:rgb(${a.f ? 'var(--badf)' : 'var(--line)'});stroke-opacity:${op.toFixed(2)};stroke-width:${(1.5 + 4.5 * a.n / maxN).toFixed(1)}"><title>${esc(a.cajero.nombre)} · ${esc(a.caja.nombre)}: ${a.f} de ${a.n} cierres con faltante</title></path>`;
    }).join('');
    const nodoSvg = (x, izq) => {
      const activo = !sel || x === sel || x.aristas.some(a => a.caja === sel || a.cajero === sel);
      const cx = izq ? xL : xR, tx = izq ? cx - 10 : cx + 10;
      return `<g data-k="${esc(x.k)}" style="cursor:pointer;opacity:${activo ? 1 : 0.3}" role="button" tabindex="0" aria-label="${esc(x.nombre)}: ${esc(x.veredicto)}">
        <rect x="${izq ? 0 : cx - 8}" y="${x.y - FILA / 2}" width="${izq ? cx + 8 : W - cx + 8}" height="${FILA}" fill="transparent"/>
        ${izq ? `<circle cx="${cx}" cy="${x.y}" r="${x === sel ? 7.5 : 6}" style="fill:rgb(${color(x.veredicto)})"/>`
              : `<rect x="${cx - 6}" y="${x.y - 6}" width="12" height="12" rx="3" style="fill:rgb(${color(x.veredicto)})"/>`}
        <text x="${tx}" y="${x.y + 4}" text-anchor="${izq ? 'end' : 'start'}" style="fill:rgb(var(--fg));font-size:12px;font-weight:${x === sel ? 700 : 500}">${esc(corto(x.nombre, 18))}</text>
      </g>`;
    };
    $('#redGrafo').innerHTML = `<svg viewBox="0 0 ${W} ${H}" width="100%" style="max-width:520px;min-width:300px;display:block;margin:auto" role="img" aria-label="Red de cajeros y cajas de ${esc(redTienda)}">
      <text x="${xL}" y="0" dy="-2" text-anchor="end" style="fill:rgb(var(--muted));font-size:10px;font-weight:600">CAJEROS ●</text>
      <text x="${xR}" y="0" dy="-2" style="fill:rgb(var(--muted));font-size:10px;font-weight:600">■ CAJAS</text>
      ${aristas}${r.cajeros.map(p => nodoSvg(p, true)).join('')}${r.cajas.map(k => nodoSvg(k, false)).join('')}</svg>`;
    $('#redGrafo svg').style.overflow = 'visible';
    $('#redSel').innerHTML = sel
      ? `<div class="rounded-lg bg-raised p-3"><div class="flex items-center justify-between gap-2"><b>${esc(sel.nombre)}</b><span class="chip ${chipVer(sel.veredicto)}">${esc(sel.veredicto)}</span></div>
          <div class="text-muted mt-1">${esc(sel.motivo)}</div>
          <div class="num mt-1.5">${sel.aristas.slice().sort((a, b) => b.f - a.f || b.n - a.n).map(a => `${esc((sel.esCaja ? a.cajero : a.caja).nombre)}: <b class="${a.f ? 'text-bad' : 'text-ok'}">${a.f} de ${a.n}</b>`).join(' · ')}</div></div>`
      : '<div class="text-muted">Toque un cajero o una caja para ver sus conexiones. Línea más gruesa = más cierres; roja = hubo faltante.</div>';

    // ---- veredictos (lo que no está limpio)
    const item = x => `<li><button data-k="${esc(x.k)}" class="w-full text-left rounded-lg border border-line p-3 hover:bg-raised ${x === sel ? 'bg-raised' : ''}">
        <div class="flex items-center justify-between gap-2"><span class="font-semibold text-sm truncate">${x.esCaja ? '■' : '●'} ${esc(x.nombre)}</span><span class="chip ${chipVer(x.veredicto)} shrink-0">${esc(x.veredicto)}</span></div>
        <div class="text-xs text-muted mt-1">${esc(x.motivo)}</div>
        <div class="num text-[11px] text-muted mt-1">${x.f} de ${x.n} cierres con faltante · neto ${firmado(x.neto)} USD</div></button></li>`;
    const grupo = (titulo, l) => {
      const malos = l.filter(x => gravedad(x.veredicto) <= 2).sort((a, b) => gravedad(a.veredicto) - gravedad(b.veredicto) || a.faltUsd - b.faltUsd);
      const resto = (v, pre) => { const r = l.filter(x => x.veredicto === v); return r.length ? `${pre} ${r.length}: ${r.map(x => `<button data-k="${esc(x.k)}" class="underline decoration-dotted">${esc(x.nombre)}</button>`).join(', ')}` : ''; };
      const notas = [resto(V.ocasional, 'Faltante ocasional, sin patrón —'), resto(V.pocos, 'Pocos cierres —'), resto(V.sin, '✓ Sin faltantes —')].filter(Boolean);
      return `<div><div class="text-[13px] font-semibold mb-1.5">${titulo}</div>
        ${malos.length ? `<ul class="flex flex-col gap-2">${malos.map(item).join('')}</ul>` : '<div class="text-xs text-ok">✓ Ninguno con patrón de faltantes</div>'}
        <div class="text-[11px] text-muted mt-1.5 leading-relaxed">${notas.join('<br>')}</div></div>`;
    };
    $('#redVeredictos').innerHTML = grupo('Cajas', r.cajas) + grupo('Cajeros', r.cajeros);
  }

  $('#redTiendas').addEventListener('click', e => { const b = e.target.closest('[data-rt]'); if (b) { redTienda = b.dataset.rt; redElegida = true; redSel = ''; pintarRed(redOk); } });
  const elegirNodo = e => { const g = e.target.closest('[data-k]'); if (!g) return; redSel = redSel === g.dataset.k ? '' : g.dataset.k; redElegida = true; pintarRed(redOk); };
  $('#redGrafo').addEventListener('click', elegirNodo);
  $('#redVeredictos').addEventListener('click', elegirNodo);
  $('#redGrafo').addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); elegirNodo(e); } });

  // =========================================================================== CALCULADORA
  let tasa = null, tasaLeida = 0;
  async function cargarTasa(forzar) {
    if (!forzar && tasa && Date.now() - tasaLeida < 10 * 60e3) { pintarCalc(); return; }
    try {
      const r = await api('/api/tasa');
      tasa = { tasa: +r.tasa, fecha: r.fecha, tienda: r.tienda }; tasaLeida = Date.now();
    } catch (e) { if (!tasa) $('#tasaNota').textContent = 'No se pudo leer la tasa: ' + e.message; }
    pintarCalc();
  }
  let moneda = 'USD', expr = '', ultimo = 0;
  const eur = () => { const v = parseFloat((leer('rpos.tasaEur') || '').replace(',', '.')); return v > 0 ? v : 0; };
  const bsPor = m => m === 'Bs' ? 1 : m === 'USD' ? (tasa ? tasa.tasa : 0) : eur();
  const SIMB = { USD: '$', Bs: 'Bs', EUR: '€' };
  function evaluar(s) {
    if (!s || !/^[\d.+\-*/() ]+$/.test(s)) return null;
    // evaluador propio (sin eval): números, + − × ÷ y paréntesis
    let i = 0;
    const ver = () => s[i], comer = () => s[i++];
    const blanco = () => { while (s[i] === ' ') i++; };
    function prim() {
      blanco();
      if (ver() === '(') { comer(); const v = suma(); blanco(); if (ver() === ')') comer(); return v; }
      if (ver() === '-') { comer(); return -prim(); }
      let j = i; while (i < s.length && /[\d.]/.test(s[i])) i++;
      if (j === i) throw 0; return parseFloat(s.substring(j, i));
    }
    function prod() { let v = prim(); for (;;) { blanco(); const c = ver(); if (c === '*' || c === '/') { comer(); const b = prim(); v = c === '*' ? v * b : v / b; } else return v; } }
    function suma() { let v = prod(); for (;;) { blanco(); const c = ver(); if (c === '+' || c === '-') { comer(); const b = prod(); v = c === '+' ? v + b : v - b; } else return v; } }
    try { const v = suma(); return isFinite(v) ? v : null; } catch (e) { return null; }
  }
  function pintarCalc() {
    $('#tasaTxt').textContent = tasa ? n2.format(tasa.tasa) : '—';
    if (tasa) $('#tasaNota').textContent = `Tasa del último cierre X · ${esc(tasa.tienda)} · ${tasa.fecha}`;
    $('#monedas').innerHTML = ['USD', 'Bs', 'EUR'].map(k => `<button data-m="${k}" class="flex-1 h-9 rounded-full text-sm font-semibold ${moneda === k ? 'bg-p600 text-onp' : 'bg-raised text-muted'}">${k}</button>`).join('');
    const v = evaluar(expr), val = v == null ? ultimo : v;
    $('#calcExpr').textContent = expr.replace(/\*/g, '×').replace(/\//g, '÷').replace(/\./g, ',');
    $('#calcVal').textContent = SIMB[moneda] + ' ' + n2.format(val);
    const bs = val * bsPor(moneda);
    const otras = ['USD', 'Bs', 'EUR'].filter(k => k !== moneda);
    $('#conv').innerHTML = otras.map(k => `<div class="rounded-xl bg-raised p-2.5 min-w-0"><div class="text-[11px] text-muted">${k}</div><div class="num font-semibold truncate">${bsPor(k) && bsPor(moneda) ? SIMB[k] + ' ' + n2.format(bs / bsPor(k)) : '<span class="text-muted font-normal">sin tasa</span>'}</div></div>`).join('')
      + `<div class="rounded-xl bg-tint p-2.5 min-w-0"><div class="text-[11px] text-muted">Con IVA 16 %</div><div class="num font-semibold truncate">${n2.format(val * 1.16)}</div></div>`;
    $('#usarTasa').disabled = !tasa;
  }
  const TECLAS = ['C', '⌫', '%', '÷', '7', '8', '9', '×', '4', '5', '6', '−', '1', '2', '3', '+', '00', '0', ',', '='];
  $('#teclado').innerHTML = TECLAS.map(t => {
    const op = '÷×−+'.includes(t), eq = t === '=', fn = 'C⌫%'.includes(t);
    return `<button data-k="${t}" class="h-14 sm:h-16 rounded-xl text-xl font-semibold num active:scale-95 transition ${eq ? 'bg-p600 text-onp' : op ? 'bg-soft text-softfg' : fn ? 'bg-raised text-muted border border-line' : 'bg-surface border border-line'}" aria-label="${{ '÷': 'dividir', '×': 'multiplicar', '−': 'restar', '+': 'sumar', '⌫': 'borrar', C: 'limpiar' }[t] || t}">${t}</button>`;
  }).join('');
  function tecla(k) {
    if (k === 'C') { expr = ''; ultimo = 0; }
    else if (k === '⌫') expr = expr.slice(0, -1);
    else if (k === '=') { const v = evaluar(expr); if (v != null) { ultimo = v; expr = String(Math.round(v * 1e6) / 1e6); } }
    else if (k === '%') { const v = evaluar(expr); if (v != null) expr = String(v / 100); }
    else expr += ({ '÷': '/', '×': '*', '−': '-', ',': '.' }[k] || k);
    pintarCalc();
  }
  $('#teclado').addEventListener('click', e => { const b = e.target.closest('[data-k]'); if (b) tecla(b.dataset.k); });
  document.addEventListener('keydown', e => {
    if (vista !== 'calc' || e.target.tagName === 'INPUT') return;
    const m = { Enter: '=', '=': '=', Backspace: '⌫', Escape: 'C', '*': '×', '/': '÷', '-': '−', '+': '+', ',': ',', '.': ',', '%': '%' };
    if (/^\d$/.test(e.key)) tecla(e.key); else if (m[e.key]) { e.preventDefault(); tecla(m[e.key]); }
  });
  $('#monedas').addEventListener('click', e => {
    const b = e.target.closest('[data-m]'); if (!b || b.dataset.m === moneda) return;
    const v = evaluar(expr), val = v == null ? ultimo : v, de = bsPor(moneda), a = bsPor(b.dataset.m);
    moneda = b.dataset.m;
    if (de && a) { const nv = Math.round(val * de / a * 100) / 100; expr = nv ? String(nv) : ''; ultimo = nv; }
    pintarCalc();
  });
  $('#usarTasa').onclick = () => { if (tasa) { expr += String(tasa.tasa); pintarCalc(); } };
  $('#tasaEur').value = eur() ? n2.format(eur()) : '';
  $('#tasaEur').addEventListener('change', e => {
    const v = parseFloat(e.target.value.replace(/\./g, '').replace(',', '.'));
    if (v > 0) { guardar('rpos.tasaEur', String(v)); e.target.value = n2.format(v); } else { borrar('rpos.tasaEur'); e.target.value = ''; }
    pintarCalc();
  });

  // =========================================================================== CHAT
  let canales = [], marcaCanales = '', canalAbierto = null, mensajes = [], marcaCanal = '', ctlEspera = null;
  const fechaMsj = s => { const d = new Date(String(s).replace(' ', 'T')); return isNaN(d) ? null : d; };
  async function cargarCanales() {
    if (!tiene('chat')) return;
    try {
      const r = await api('/api/chat/canales');
      if (!r.sinCambios) { canales = r.canales || []; marcaCanales = r.version; }
      noLeidos = canales.reduce((a, c) => a + (c.noLeidos || 0), 0);
      pintarNav(); if (vista === 'chat') pintarChat();
    } catch (e) { if (e.codigo !== 401 && vista === 'chat') toast('Chat: ' + e.message); }
  }
  let ctlMsj = null; const leidoHasta = {};
  async function cargarMensajes(todo) {
    if (!canalAbierto) return;
    const id = canalAbierto.id;
    const ultimoId = !todo && mensajes.length ? mensajes[mensajes.length - 1].id : 0;
    if (todo && ctlMsj) ctlMsj.abort();                               // al cambiar de conversación se cancela la carga anterior
    const ctl = ctlMsj = todo || !ctlMsj ? new AbortController() : ctlMsj;
    if (todo && !mensajes.length) $('#mensajes').innerHTML = '<p class="text-sm text-muted text-center py-8">Cargando mensajes…</p>';
    try {
      const q = `/api/chat/mensajes?canal=${encodeURIComponent(id)}&limite=60` + (ultimoId ? `&despues=${ultimoId}` : '');
      const r = await api(q, undefined, { signal: ctl.signal, timeout: 20000 });
      if (!canalAbierto || canalAbierto.id !== id) return;
      if (r.canal) canalAbierto = Object.assign(canalAbierto, r.canal);
      const nuevos = r.mensajes || [];
      mensajes = ultimoId ? mensajes.concat(nuevos.filter(m => !mensajes.some(x => x.id === m.id))) : nuevos;
      if (r.eliminados && r.eliminados.length) mensajes.forEach(m => { if (r.eliminados.includes(m.id)) { m.eliminado = true; m.texto = ''; } });
      marcaCanal = r.version;
      pintarMensajes(todo);
      const ult = mensajes.length ? mensajes[mensajes.length - 1].id : 0;
      // "leído" solo cuando hay mensajes nuevos: cada aviso despierta al tiempo real y, si se repite, forma un ciclo sin fin
      if (ult && ult > (leidoHasta[id] || 0)) { leidoHasta[id] = ult; api('/api/chat/leido', { canal: id, hastaId: ult }).then(() => cargarCanales()).catch(() => { leidoHasta[id] = 0; }); }
    } catch (e) {
      if (ctl.signal.aborted || e.codigo === 401 || !canalAbierto || canalAbierto.id !== id) return;
      if (todo) $('#mensajes').innerHTML = `<div class="text-center py-8"><p class="text-sm text-bad">${esc(e.message)}</p><button id="reintentarMsj" class="mt-3 h-9 px-4 rounded-lg border border-line text-sm font-semibold">Reintentar</button></div>`;
      else toast('Chat: ' + e.message);
    }
  }
  // tiempo real: la API responde apenas hay un cambio o a los 25 s
  let esperando = false;
  async function esperarCambios() {
    if (esperando) return;                                             // una sola espera abierta a la vez
    esperando = true;
    try { while (S.token && tiene('chat')) {
      const ctl = ctlEspera = new AbortController();
      try {
        const r = await api(`/api/chat/esperar?v=${encodeURIComponent(marcaCanales)}`, undefined, { signal: ctl.signal, timeout: 40000 });
        if (r && r.cambio) { await cargarCanales(); if (vista === 'chat' && canalAbierto) await cargarMensajes(false); }
      } catch (e) {
        if (ctl.signal.aborted || e.codigo === 401) return;
        await new Promise(r => setTimeout(r, 8000));
      }
    } } finally { esperando = false; }
  }
  document.addEventListener('click', e => { if (e.target.closest('#reintentarMsj')) cargarMensajes(true); });
  function pintarChat() {
    const ancho = window.matchMedia('(min-width: 1024px)').matches;
    const orden = [...canales].sort((a, b) => (b.ultimo ? b.ultimo.id : 0) - (a.ultimo ? a.ultimo.id : 0));
    $('#panelCanales').hidden = !!canalAbierto && !ancho;
    const q = normal($('#buscarChat').value);
    const filtrados = q ? orden.filter(c => normal(c.nombre + ' ' + (c.detalle || '')).includes(q)) : orden;
    const yaPrivados = new Set(canales.map(c => c.id));
    const personas = q ? contactos.filter(c => !yaPrivados.has(c.canal) && normal(c.nombre + ' ' + c.login).includes(q)).slice(0, 20) : [];
    $('#listaCanales').innerHTML = filtrados.map(c => {
      const u = c.ultimo, d = u ? fechaMsj(u.fecha) : null;
      const ic = c.tipo === 'general' ? '#' : c.tipo === 'privado' ? iniciales(c.nombre) : (c.nombre || '').substring(0, 2).toUpperCase();
      return `<li><button data-canal="${esc(c.id)}" class="w-full text-left flex items-center gap-3 rounded-xl p-3 border ${canalAbierto && canalAbierto.id === c.id ? 'bg-tint border-p300' : 'bg-surface border-line hover:border-p300'}">
        <span class="grid place-items-center shrink-0 w-10 h-10 rounded-full ${c.tipo === 'general' ? 'bg-p600 text-onp' : 'bg-soft text-softfg'} text-xs font-bold">${esc(ic)}</span>
        <span class="min-w-0 flex-1">
          <span class="flex justify-between gap-2"><span class="font-semibold truncate">${esc(c.nombre)}</span><span class="text-[11px] text-muted shrink-0">${d ? (d.toDateString() === new Date().toDateString() ? d.toLocaleTimeString('es-VE', { hour: 'numeric', minute: '2-digit' }) : fCorta(d)) : ''}</span></span>
          <span class="flex justify-between gap-2 mt-0.5"><span class="text-sm text-muted truncate">${u ? esc((u.login === S.usuario ? 'Usted: ' : (c.tipo === 'privado' ? '' : (u.nombre || '').split(' ')[0] + ': ')) + (u.texto || '')) : 'Sin mensajes'}</span>
          ${c.noLeidos ? `<span class="shrink-0 min-w-[20px] h-5 px-1.5 rounded-full bg-p600 text-onp text-[11px] font-bold grid place-items-center num">${c.noLeidos}</span>` : ''}</span>
        </span></button></li>`;
    }).join('')
      + (personas.length ? `<li class="etq px-1 pt-3 pb-1">Personas</li>` + personas.map(filaContacto).join('') : '')
      + (!filtrados.length && !personas.length ? `<li class="text-sm text-muted p-4">${q ? 'No hay resultados para «' + esc($('#buscarChat').value) + '».' : 'No hay conversaciones.'}</li>` : '');
    $('#conversacion').hidden = !canalAbierto;
    $('#sinConversacion').hidden = !!canalAbierto;
    if (canalAbierto) pintarMensajes(false);
  }
  function pintarMensajes(bajar) {
    let diaAnt = '';
    const caja = $('#mensajes');
    const cerca = caja.scrollHeight - caja.scrollTop - caja.clientHeight < 80, antes = caja.scrollTop, nAntes = caja.dataset.n | 0;
    caja.innerHTML = mensajes.map(m => {
      const d = fechaMsj(m.fecha), clave = d ? d.toDateString() : '';
      let sep = '';
      if (clave !== diaAnt) { diaAnt = clave; sep = `<div class="text-center"><span class="text-[11px] font-medium text-muted bg-raised rounded-full px-3 py-1">${d ? (clave === new Date().toDateString() ? 'Hoy' : fLarga(d)) : ''}</span></div>`; }
      const h = d ? d.toLocaleTimeString('es-VE', { hour: 'numeric', minute: '2-digit' }) : '';
      const mio = m.login === S.usuario;
      const extra = m.eliminado ? '<i class="opacity-70">Mensaje eliminado</i>'
        : esc(m.texto).replace(/\n/g, '<br>')
          + (m.adjunto ? `<div class="mt-1.5 text-xs rounded-lg px-2 py-1 ${mio ? 'bg-white/15' : 'bg-raised'}">📊 Reporte compartido: ${esc((m.adjunto.sucursales || []).join(', '))} · ${esc(m.adjunto.desde)} a ${esc(m.adjunto.hasta)} (se abre en Excel)</div>` : '')
          + (m.archivo ? `<button data-archivo="${esc(m.archivo.id)}" data-nombre="${esc(m.archivo.nombre)}" class="mt-1.5 text-xs rounded-lg px-2 py-1 underline ${mio ? 'bg-white/15' : 'bg-raised'}">📎 ${esc(m.archivo.nombre)}</button>` : '');
      return sep + (mio ? `
        <div class="flex justify-end"><div class="max-w-[82%] rounded-xl rounded-br-md bg-p600 text-onp px-3.5 py-2.5 text-[15px] break-words">${extra}<div class="text-[11px] text-onp/70 text-right mt-1">${h}</div></div></div>` : `
        <div class="flex gap-2.5 items-end"><div class="grid place-items-center shrink-0 w-8 h-8 rounded-full ${m.login === 'sistema' ? 'bg-warnbg text-warn' : 'bg-soft text-softfg'} text-[11px] font-bold">${esc(iniciales(m.nombre))}</div>
          <div class="max-w-[82%] min-w-0 rounded-xl rounded-bl-md bg-surface border border-line px-3.5 py-2.5 text-[15px] break-words"><div class="text-xs font-semibold text-p700 mb-0.5">${esc(m.nombre)}</div>${extra}<div class="text-[11px] text-muted mt-1">${h}</div></div></div>`);
    }).join('') || '<p class="text-sm text-muted text-center py-8">Todavía no hay mensajes. Escriba el primero.</p>';
    const puede = !canalAbierto || canalAbierto.puedeEscribir !== false;
    $('#chatTxt').disabled = !puede; $('#chatEnviar').disabled = !puede;
    $('#chatTxt').placeholder = puede ? 'Escriba un mensaje…' : 'En este canal solo escriben jefes y administradores';
    // solo baja al final si se pidió (abrir / enviar) o si ya estaba abajo; si está leyendo arriba, no lo mueve
    caja.dataset.n = mensajes.length;
    if (bajar || cerca) { caja.scrollTop = caja.scrollHeight; $('#nuevosAbajo').hidden = true; }
    else { caja.scrollTop = antes; if (mensajes.length > nAntes) $('#nuevosAbajo').hidden = false; }
  }
  $('#mensajes').addEventListener('scroll', () => { const c = $('#mensajes'); if (c.scrollHeight - c.scrollTop - c.clientHeight < 80) $('#nuevosAbajo').hidden = true; }, { passive: true });
  $('#btnNuevosAbajo').onclick = () => { const c = $('#mensajes'); c.scrollTop = c.scrollHeight; $('#nuevosAbajo').hidden = true; };
  const normal = t => String(t || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();
  // personas para conversar en privado (/api/chat/contactos)
  let contactos = [], contactosLeidos = 0;
  async function cargarContactos() {
    if (Date.now() - contactosLeidos < 5 * 60e3) return;
    try { const r = await api('/api/chat/contactos'); contactos = r.contactos || []; contactosLeidos = Date.now(); } catch (e) { }
  }
  const filaContacto = c => `<li><button data-contacto="${esc(c.canal)}" class="w-full text-left flex items-center gap-3 rounded-xl p-3 border bg-surface border-line hover:border-p300">
      <span class="grid place-items-center shrink-0 w-10 h-10 rounded-full bg-raised text-muted border border-line text-xs font-bold">${esc(iniciales(c.nombre))}</span>
      <span class="min-w-0 flex-1"><span class="block font-semibold truncate">${esc(c.nombre)}</span><span class="block text-sm text-muted truncate">${esc(c.rolTexto || c.rol || '')} · ${esc(c.login)}</span></span>
      <span class="chip chip-neutro shrink-0">Escribir</span></button></li>`;
  function abrirContacto(canal) {
    const c = contactos.find(x => x.canal === canal); if (!c) return;
    const existente = canales.find(x => x.id === canal);
    canalAbierto = Object.assign({}, existente || { id: c.canal, nombre: c.nombre, tipo: 'privado', detalle: c.rolTexto || c.rol, puedeEscribir: true });
    mensajes = []; $('#mensajes').dataset.n = 0; cerrar(); $('#buscarChat').value = '';
    pintarChat(); encabezado(); cargarMensajes(true); setTimeout(() => $('#chatTxt').focus(), 50);
  }
  $('#buscarChat').addEventListener('input', () => { cargarContactos().then(pintarChat); pintarChat(); });
  $('#nuevoChat').onclick = async () => {
    abrir(`<div class="py-2"><h3 class="text-[17px] font-semibold">Escribir a una persona</h3><p class="text-xs text-muted mt-0.5">Conversación privada</p></div>
      <input id="buscarPersona" type="search" class="campo !h-11 mt-2" placeholder="Buscar por nombre o usuario…" autocomplete="off">
      <ul id="listaPersonas" class="flex flex-col gap-1.5 mt-3 max-h-[55vh] overflow-y-auto"><li class="text-sm text-muted p-3">Cargando…</li></ul>`);
    await cargarContactos();
    const pintar = () => {
      const q = normal($('#buscarPersona').value);
      const l = contactos.filter(c => !q || normal(c.nombre + ' ' + c.login + ' ' + (c.rolTexto || '')).includes(q));
      $('#listaPersonas').innerHTML = l.map(filaContacto).join('') || '<li class="text-sm text-muted p-3">No se encontró a nadie con ese nombre.</li>';
    };
    pintar(); $('#buscarPersona').addEventListener('input', pintar); $('#buscarPersona').focus();
  };
  document.addEventListener('click', e => { const b = e.target.closest('[data-contacto]'); if (b) abrirContacto(b.dataset.contacto); });
  document.addEventListener('click', e => {
    const b = e.target.closest('[data-canal]'); if (!b) return;
    canalAbierto = Object.assign({}, canales.find(c => c.id === b.dataset.canal));
    mensajes = []; $('#mensajes').dataset.n = 0; $('#mensajes').innerHTML = ''; pintarChat(); encabezado(); cargarMensajes(true);
  });
  document.addEventListener('click', async e => {
    const b = e.target.closest('[data-archivo]'); if (!b) return;
    try {
      const r = await fetch('/api/chat/archivos/' + encodeURIComponent(b.dataset.archivo), { headers: { Authorization: 'Bearer ' + S.token } });
      if (!r.ok) throw new Error('No se pudo descargar el archivo');
      const url = URL.createObjectURL(await r.blob());
      const a = document.createElement('a'); a.href = url; a.download = b.dataset.nombre || 'archivo'; document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 30000);
    } catch (err) { toast(err.message); }
  });
  $('#chatForm').addEventListener('submit', async e => {
    e.preventDefault();
    const t = $('#chatTxt').value.trim(); if (!t || !canalAbierto) return;
    $('#chatEnviar').disabled = true;
    try { await api('/api/chat/mensajes', { canal: canalAbierto.id, texto: t }); $('#chatTxt').value = ''; await cargarMensajes(false); pintarMensajes(true); cargarCanales(); }
    catch (err) { toast(err.message); }
    finally { $('#chatEnviar').disabled = false; $('#chatTxt').focus(); }
  });

  // =========================================================================== MÁS
  const OTROS = [
    ['ventas', 'Reporte Ventas POS', 'Detalle de facturas por tienda'], ['diario', 'Ventas diarias', 'Día por día con gráfico'],
    ['categorias', 'Ventas por categoría', 'Participación y margen'], ['articulos', 'Resumen por artículo', 'Los más vendidos'],
    ['productos', 'Análisis de productos', 'Migración a Odoo'], ['listado', 'Listado de ventas', 'Facturas con artículos y pagos'], ['compras', 'Compras', 'Costos y proveedores'],
    ['consolidar', 'Consolidar', 'Todas las tiendas en un libro']
  ];
  const TEMAS = [['verde', 'Verde', '#16A34A'], ['azul', 'Azul', '#2563EB'], ['morado', 'Morado', '#7C3AED'], ['turquesa', 'Turquesa', '#0D9488'], ['naranja', 'Naranja', '#EA580C'], ['grafito', 'Grafito', '#475569']];
  let marca = leer('rpos.tema') || 'verde', modo = leer('rpos.modo') || 'auto';
  // =========================================================================== ADMINISTRACIÓN
  let adminTab = 'usuarios', usuarios = [], filtroU = 'todos', relojTiendas = null, estadoTiendas = [], estadoLeido = null;
  const ROLES = { ADMIN: 'Administrador', JEFE: 'Jefe', REGIONAL: 'Regional', TIENDA: 'Tienda' };
  $('#adminTabs').addEventListener('click', e => {
    const b = e.target.closest('[data-at]'); if (!b) return;
    adminTab = b.dataset.at;
    $$('#adminTabs [data-at]').forEach(x => x.setAttribute('aria-pressed', x.dataset.at === adminTab));
    $('#adminUsuarios').hidden = adminTab !== 'usuarios'; $('#adminTiendas').hidden = adminTab !== 'tiendas';
    encabezado();
  });
  async function cargarUsuarios() {
    cargando(true);
    try { const r = await api('/api/admin/usuarios'); usuarios = r.usuarios || []; pintarUsuarios(); }
    catch (e) { if (e.codigo !== 401) $('#listaUsuarios').innerHTML = `<li class="text-sm text-bad p-3">${esc(e.message)}</li>`; }
    finally { cargando(false); }
  }
  function pintarUsuarios() {
    const q = normal($('#buscarUsuario').value);
    const nAct = usuarios.filter(u => u.activo).length, nBloq = usuarios.filter(u => u.bloqueado).length;
    $('#uActivos').textContent = nAct; $('#uInactivos').textContent = usuarios.length - nAct; $('#uBloqueados').textContent = nBloq;
    const F = [['todos', 'Todos'], ['activos', 'Activos'], ['inactivos', 'Inactivos'], ['bloqueados', 'Bloqueados']];
    $('#filtroUsuarios').innerHTML = F.map(([k, n]) => `<button data-fu="${k}" aria-pressed="${filtroU === k}">${n}</button>`).join('');
    const lista = usuarios.filter(u => (filtroU === 'todos' || (filtroU === 'activos' && u.activo) || (filtroU === 'inactivos' && !u.activo) || (filtroU === 'bloqueados' && u.bloqueado))
        && (!q || normal(u.nombre + ' ' + u.login + ' ' + (u.tiendas || []).join(' ') + ' ' + (ROLES[u.rol] || u.rol)).includes(q)))
      .sort((a, b) => (b.bloqueado - a.bloqueado) || (b.activo - a.activo) || a.nombre.localeCompare(b.nombre, 'es'));
    $('#listaUsuarios').innerHTML = lista.map(u => {
      const yo = u.login.toLowerCase() === S.usuario.toLowerCase();
      const estado = !u.activo ? '<span class="chip chip-neutro">Inactivo</span>' : u.bloqueado ? '<span class="chip chip-bad">! Bloqueado</span>' : '<span class="chip chip-ok">✓ Activo</span>';
      const tiendas = u.rol === 'ADMIN' || u.rol === 'JEFE' ? 'Todas las tiendas' : (u.tiendas || []).length === 1 ? u.tiendas[0] : (u.tiendas || []).length + ' tiendas';
      const acciones = !u.editable ? '<span class="text-[11px] text-muted">Solo el administrador puede cambiarlo</span>' : [
        u.bloqueado ? `<button data-ua="desbloquear" data-login="${esc(u.login)}" class="h-9 px-3 rounded-lg bg-p600 text-onp text-sm font-semibold">Desbloquear</button>` : '',
        !yo ? `<button data-ua="${u.activo ? 'desactivar' : 'activar'}" data-login="${esc(u.login)}" class="h-9 px-3 rounded-lg text-sm font-semibold ${u.activo ? 'border border-line text-bad hover:bg-badbg' : 'bg-p600 text-onp'}">${u.activo ? 'Desactivar' : 'Activar'}</button>` : '',
        `<button data-ua="clave" data-login="${esc(u.login)}" class="h-9 px-3 rounded-lg border border-line text-sm font-semibold hover:border-p300">Clave temporal</button>`
      ].join('');
      return `<li class="tarjeta p-3.5 ${u.activo ? '' : 'opacity-75'}">
        <div class="flex items-start gap-3">
          <span class="grid place-items-center shrink-0 w-10 h-10 rounded-full ${u.activo ? 'bg-soft text-softfg' : 'bg-raised text-muted border border-line'} text-xs font-bold">${esc(iniciales(u.nombre))}</span>
          <div class="min-w-0 flex-1">
            <div class="flex items-center justify-between gap-2"><span class="font-semibold truncate">${esc(u.nombre)}${yo ? ' <span class="text-xs text-muted font-normal">(usted)</span>' : ''}</span>${estado}</div>
            <div class="text-xs text-muted truncate mt-0.5">${esc(u.login)} · ${esc(ROLES[u.rol] || u.rol)} · ${esc(tiendas)}</div>
            <div class="text-[11px] text-muted mt-0.5">${u.ultimoAcceso ? 'Último acceso ' + esc(u.ultimoAcceso) : 'Nunca ha ingresado'}</div>
          </div>
        </div>
        <div class="flex flex-wrap items-center justify-end gap-2 mt-3">${acciones}</div>
      </li>`;
    }).join('') || `<li class="text-sm text-muted p-4">${usuarios.length ? 'Ningún usuario coincide con la búsqueda.' : 'Cargando…'}</li>`;
  }
  $('#buscarUsuario').addEventListener('input', pintarUsuarios);
  $('#filtroUsuarios').addEventListener('click', e => { const b = e.target.closest('[data-fu]'); if (b) { filtroU = b.dataset.fu; pintarUsuarios(); } });
  function reemplazarUsuario(nuevo) { const i = usuarios.findIndex(x => x.login === nuevo.login); if (i >= 0) usuarios[i] = nuevo; pintarUsuarios(); }
  async function accionUsuario(accion, login) {
    const u = usuarios.find(x => x.login === login); if (!u) return;
    try {
      let r;
      if (accion === 'desbloquear') { r = await api(`/api/admin/usuarios/${encodeURIComponent(login)}/desbloquear`, {}); toast(`${u.nombre} ya puede ingresar`); }
      else if (accion === 'activar' || accion === 'desactivar') { r = await api(`/api/admin/usuarios/${encodeURIComponent(login)}/activo`, { activo: accion === 'activar' }); toast(accion === 'activar' ? `${u.nombre} está activo` : `${u.nombre} ya no puede ingresar`); }
      if (r && r.usuario) reemplazarUsuario(r.usuario);
    } catch (e) { toast(e.message); }
  }
  document.addEventListener('click', e => {
    const b = e.target.closest('[data-ua]'); if (!b) return;
    const accion = b.dataset.ua, login = b.dataset.login, u = usuarios.find(x => x.login === login); if (!u) return;
    if (accion === 'desactivar') {
      abrir(`<div class="py-2"><h3 class="text-[17px] font-semibold">¿Desactivar a ${esc(u.nombre)}?</h3>
        <p class="text-sm text-muted mt-1">No podrá entrar a la app ni al complemento de Excel, y su sesión abierta se corta de inmediato. Puede volver a activarlo cuando quiera.</p></div>
        <div class="grid grid-cols-2 gap-2 mt-4"><button data-cancelar class="h-11 rounded-xl border border-line font-semibold">Cancelar</button>
        <button id="confDesactivar" class="h-11 rounded-xl bg-bad text-white font-semibold">Desactivar</button></div>`);
      $('#confDesactivar').onclick = () => { cerrar(); accionUsuario('desactivar', login); };
    } else if (accion === 'clave') abrirClaveTemporal(u);
    else accionUsuario(accion, login);
  });
  document.addEventListener('click', e => { if (e.target.closest('[data-cancelar]')) cerrar(); });
  function claveAleatoria() {
    const L = 'abcdefghjkmnpqrstuvwxyz', N = '23456789', a = new Uint32Array(8); crypto.getRandomValues(a);
    return L[a[0] % 23].toUpperCase() + L[a[1] % 23] + L[a[2] % 23] + L[a[3] % 23] + '-' + N[a[4] % 8] + N[a[5] % 8] + N[a[6] % 8] + N[a[7] % 8];
  }
  function abrirClaveTemporal(u) {
    const sugerida = claveAleatoria();
    abrir(`<div class="py-2"><h3 class="text-[17px] font-semibold">Clave temporal para ${esc(u.nombre)}</h3>
      <p class="text-sm text-muted mt-1">Al ingresar, el sistema le pedirá cambiarla. Sus huellas registradas se borran.</p></div>
      <label class="text-sm font-medium flex flex-col gap-1.5 mt-3">Clave temporal (mínimo 8 caracteres)
        <span class="flex gap-2"><input id="claveTemp" class="campo num" value="${sugerida}" autocomplete="off" spellcheck="false">
        <button id="otraClave" type="button" class="shrink-0 h-12 px-3 rounded-xl border border-line text-sm font-semibold">Otra</button></span></label>
      <p id="claveErr" class="text-sm text-bad mt-2" hidden></p>
      <div class="grid grid-cols-2 gap-2 mt-4"><button data-cancelar class="h-11 rounded-xl border border-line font-semibold">Cancelar</button>
      <button id="guardarClave" class="h-11 rounded-xl bg-p600 text-onp font-semibold">Guardar</button></div>`);
    $('#otraClave').onclick = () => { $('#claveTemp').value = claveAleatoria(); };
    $('#guardarClave').onclick = async () => {
      const clave = $('#claveTemp').value.trim();
      if (clave.length < 8) { $('#claveErr').textContent = 'La clave debe tener al menos 8 caracteres.'; $('#claveErr').hidden = false; return; }
      $('#guardarClave').disabled = true;
      try {
        const r = await api(`/api/admin/usuarios/${encodeURIComponent(u.login)}`, { login: u.login, nombre: u.nombre, rol: u.rol, activo: u.activo, clave, tiendas: u.tiendas || [], permisos: null }, { metodo: 'PUT' });
        if (r && r.usuario) reemplazarUsuario(r.usuario);
        abrir(`<div class="py-2"><h3 class="text-[17px] font-semibold">Clave guardada</h3>
          <p class="text-sm text-muted mt-1">Désele esta clave a ${esc(u.nombre)} (usuario <b class="text-fg">${esc(u.login)}</b>). Al entrar tendrá que cambiarla.</p></div>
          <div class="tarjeta p-4 mt-3 flex items-center justify-between gap-3"><span id="claveMostrar" class="font-display font-semibold text-xl num select-all">${esc(clave)}</span>
          <button id="copiarClave" class="h-9 px-3 rounded-lg border border-line text-sm font-semibold">Copiar</button></div>
          <button data-cancelar class="mt-4 w-full h-11 rounded-xl bg-p600 text-onp font-semibold">Listo</button>`);
        $('#copiarClave').onclick = () => { navigator.clipboard.writeText(clave).then(() => toast('Clave copiada'), () => { const rg = document.createRange(); rg.selectNodeContents($('#claveMostrar')); getSelection().removeAllRanges(); getSelection().addRange(rg); }); };
      } catch (e) { $('#claveErr').textContent = e.message; $('#claveErr').hidden = false; $('#guardarClave').disabled = false; }
    };
  }
  // ---- tiendas en línea (la API prueba cada tienda con un límite de 3 s y guarda el resultado 60 s) ----
  async function cargarEstadoTiendas(forzar) {
    try {
      const r = await api('/api/estado-tiendas' + (forzar ? '?refrescar=true' : ''));
      estadoTiendas = r.tiendas || []; estadoLeido = new Date(); pintarEstadoTiendas();
    } catch (e) { if (e.codigo !== 401) $('#tNota').textContent = 'No se pudo revisar: ' + e.message; }
  }
  function pintarEstadoTiendas() {
    const malas = estadoTiendas.filter(t => !t.ok);
    $('#tOk').textContent = estadoTiendas.length - malas.length; $('#tMal').textContent = malas.length;
    $('#irAdminNota').textContent = estadoTiendas.length ? `${malas.length ? malas.length + ' tiendas sin respuesta' : 'Todas las tiendas en línea'} · usuarios` : 'Usuarios y tiendas en línea';
    const lista = [...estadoTiendas].sort((a, b) => (a.ok - b.ok) || a.nombre.localeCompare(b.nombre, 'es'));
    $('#listaEstadoTiendas').innerHTML = lista.map(t => `
      <li class="tarjeta p-3.5 flex items-center gap-3 ${t.ok ? '' : 'border-badline'}">
        <span class="w-2.5 h-2.5 rounded-full shrink-0 ${t.ok ? 'bg-okf' : 'bg-badf'}"></span>
        <div class="min-w-0 flex-1"><div class="font-semibold truncate">${esc(t.nombre)}</div><div class="text-xs ${t.ok ? 'text-muted' : 'text-bad'} truncate">${esc(t.ok ? 'En línea' : t.mensaje || 'Sin respuesta')}</div></div>
        ${t.ok ? `<span class="chip ${t.ms > 1500 ? 'chip-warn' : 'chip-neutro'}">${n0.format(t.ms)} ms</span>` : '<span class="chip chip-bad">Sin respuesta</span>'}
      </li>`).join('') || '<li class="text-sm text-muted p-3">Revisando tiendas…</li>';
    $('#tNota').textContent = estadoLeido ? `Revisado a las ${estadoLeido.toLocaleTimeString('es-VE', { hour: 'numeric', minute: '2-digit' })} · se actualiza solo cada minuto mientras esta pantalla está abierta. Más de 1.500 ms = conexión lenta.` : '';
  }

  function pintarMas() {
    $('#irAdmin').hidden = !esGestor();
    $('#masIniciales').textContent = iniciales(S.nombre);
    $('#masNombre').textContent = S.nombre;
    $('#masRol').textContent = `${rol(S.rol)} · ${S.usuario} · ${S.sucursales.length === 1 ? S.sucursales[0] : S.sucursales.length + ' tiendas'}`;
    const lista = OTROS.filter(o => tiene(o[0]));
    $('#masReportes').innerHTML = lista.map(([, n, d]) => `
      <li class="flex items-center gap-3 px-4 py-3.5">
        <div class="grid place-items-center shrink-0 w-9 h-9 rounded-xl bg-tint text-p700">${svg(ICON.excel, 'w-[18px] h-[18px]')}</div>
        <div class="min-w-0 flex-1"><div class="font-medium text-sm">${n}</div><div class="text-xs text-muted">${d}</div></div>
        <span class="text-[11px] font-semibold rounded-full bg-raised border border-line text-muted px-2 py-0.5 shrink-0">En Excel</span>
      </li>`).join('') || '<li class="px-4 py-3.5 text-sm text-muted">No tiene otros reportes asignados.</li>';
    $('#temas').innerHTML = TEMAS.map(([k, n, c]) => `
      <button data-tema="${k}" class="rounded-xl border p-2.5 flex flex-col items-center gap-1.5 text-xs font-medium ${marca === k ? 'border-p500 bg-tint' : 'border-line bg-surface'}">
        <span class="w-8 h-8 rounded-full grid place-items-center text-white text-sm" style="background:${c}">${marca === k ? '✓' : ''}</span>${n}
      </button>`).join('');
    $('#modos').innerHTML = [['auto', 'Auto'], ['light', 'Claro'], ['dark', 'Oscuro']].map(([k, n]) => `<button data-modo="${k}" class="px-3 h-8 rounded-full ${modo === k ? 'bg-surface shadow-sm text-fg' : 'text-muted'}">${n}</button>`).join('');
  }
  function aplicarTema() {
    const r = document.documentElement;
    if (marca === 'verde') r.removeAttribute('data-brand'); else r.setAttribute('data-brand', marca);
    if (modo === 'auto') r.removeAttribute('data-theme'); else r.setAttribute('data-theme', modo);
    requestAnimationFrame(() => {
      const sup = getComputedStyle(r).getPropertyValue('--surface').trim().split(/\s+/).map(Number);
      if (sup.length === 3) document.querySelector('meta[name="theme-color"]').setAttribute('content', `rgb(${sup.join(',')})`);
      if (vista === 'ventas') pintarTendencia();
    });
  }
  $('#temas').addEventListener('click', e => { const b = e.target.closest('[data-tema]'); if (!b) return; marca = b.dataset.tema; guardar('rpos.tema', marca); aplicarTema(); pintarMas(); });
  $('#modos').addEventListener('click', e => { const b = e.target.closest('[data-modo]'); if (!b) return; modo = b.dataset.modo; guardar('rpos.modo', modo); aplicarTema(); pintarMas(); });

  async function cambiarClave(actual, nueva, nueva2) {
    if (!actual || !nueva) throw new Error('Escriba la clave actual y la nueva.');
    if (nueva !== nueva2) throw new Error('Las claves nuevas no coinciden.');
    const r = await api('/api/mi-clave', { claveActual: actual, claveNueva: nueva });
    if (r && r.token) { S.token = r.token; if (r.expira) S.expira = r.expira; guardarSesion(); }
  }
  $('#fClave2').addEventListener('submit', async e => {
    e.preventDefault(); const m = $('#c2Msg');
    try {
      await cambiarClave($('#c2Actual').value, $('#c2Nueva').value, $('#c2Nueva2').value);
      m.className = 'text-sm rounded-xl px-3 py-2 bg-okbg text-ok'; m.textContent = 'Clave cambiada.'; e.target.reset();
    } catch (err) { m.className = 'text-sm rounded-xl px-3 py-2 bg-badbg text-bad'; m.textContent = err.message; }
    m.hidden = false;
  });

  // =========================================================================== INGRESO / SALIDA
  function mostrar(cual) { ['vLogin', 'vClave', 'vApp'].forEach(v => { $('#' + v).hidden = v !== cual; }); }
  $('#fLogin').addEventListener('submit', async e => {
    e.preventDefault();
    const u = $('#lUsuario').value.trim(), c = $('#lClave').value;
    if (!u || !c) { $('#lError').textContent = 'Escriba su usuario y su clave.'; $('#lError').hidden = false; return; }
    $('#lBoton').disabled = true; $('#lBoton').textContent = 'Ingresando…'; $('#lError').hidden = true;
    try {
      const r = await api('/api/login', { usuario: u, clave: c }, { login: true });
      $('#lClave').value = '';
      await sesionIniciada(r);
    } catch (err) { $('#lError').textContent = err.message === 'Failed to fetch' ? 'No hay conexión con el servidor. Revise que esté en la red de la empresa.' : err.message; $('#lError').hidden = false; }
    finally { $('#lBoton').disabled = false; $('#lBoton').textContent = 'Ingresar'; }
  });
  $('#fClave').addEventListener('submit', async e => {
    e.preventDefault();
    try { await cambiarClave($('#cActual').value, $('#cNueva').value, $('#cNueva2').value); e.target.reset(); toast('Clave cambiada'); await entrar(); }
    catch (err) { $('#cError').textContent = err.message; $('#cError').hidden = false; }
  });
  document.addEventListener('click', e => { if (e.target.closest('[data-salir]')) salir(); });

  async function sesionIniciada(r) {
    Object.assign(S, { token: r.token, usuario: r.usuario, nombre: r.nombre, rol: r.rol, permisos: r.permisos || [], expira: r.expira });
    guardarSesion(); guardar('rpos.ultimoUsuario', r.usuario);
    if (r.debeCambiarClave) { mostrar('vClave'); $('#cActual').focus(); } else await entrar();
  }

  // =========================================================================== HUELLA / FACE ID (WebAuthn)
  // La llave privada queda en el teléfono; la API guarda solo la pública. Solo por HTTPS en la dirección configurada.
  const b64u = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  const deB64u = s => { s = s.replace(/-/g, '+').replace(/_/g, '/'); const b = atob(s + '==='.slice((s.length + 3) % 4)); return Uint8Array.from(b, c => c.charCodeAt(0)).buffer; };
  let huella = { disponible: false, origen: null };
  const huellaLocal = () => { try { return JSON.parse(leer('rpos.huella') || 'null'); } catch (e) { return null; } };
  const huellaPosible = () => huella.disponible && window.isSecureContext && !!window.PublicKeyCredential && location.origin === huella.origen;
  function nombreEquipo() {
    const ua = navigator.userAgent;
    const so = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? ((ua.match(/Android [\d.]+; ([^;)]+)/) || [])[1] || 'Android') : /Windows/.test(ua) ? 'PC Windows' : /Mac/.test(ua) ? 'Mac' : 'Equipo';
    const nav = /Edg\//.test(ua) ? 'Edge' : /CriOS|Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : /Firefox\//.test(ua) ? 'Firefox' : '';
    return (so + (nav ? ' · ' + nav : '')).substring(0, 60);
  }
  async function cargarEstadoHuella() {
    try { const r = await api('/api/huella/estado'); huella = { disponible: !!r.disponible, origen: r.origen }; } catch (e) { huella = { disponible: false }; }
    pintarLoginHuella();
  }
  function pintarLoginHuella() {
    const h = huellaLocal(), ok = huellaPosible() && h && h.usuario;
    $('#lHuellaCaja').hidden = !ok;
    if (ok) $('#lHuellaUsuario').textContent = 'Usuario ' + h.usuario + (h.nombre ? ' · ' + h.nombre : '');
  }
  const mensajeWebAuthn = e => e && e.name === 'NotAllowedError' ? 'Se canceló o no se reconoció la huella.' : e && e.name === 'InvalidStateError' ? 'Este teléfono ya tiene la huella activa para este usuario.' : (e && e.message) || 'No se pudo usar la huella.';
  $('#lHuella').onclick = async () => {
    const h = huellaLocal(); if (!h) return;
    $('#lError').hidden = true;
    try {
      const o = await api('/api/huella/ingreso/opciones', { usuario: h.usuario }, { login: true });
      const cred = await navigator.credentials.get({ publicKey: {
        challenge: deB64u(o.challenge), rpId: o.rpId, timeout: o.timeout, userVerification: 'required',
        allowCredentials: (o.allowCredentials || []).map(c => ({ type: 'public-key', id: deB64u(c.id), transports: c.transports }))
      } });
      const res = cred.response;
      const r = await api('/api/huella/ingreso', { usuario: h.usuario, id: cred.id, clientDataJSON: b64u(res.clientDataJSON), authenticatorData: b64u(res.authenticatorData), signature: b64u(res.signature) }, { login: true });
      await sesionIniciada(r);
    } catch (e) {
      if (e.codigo === 401 && /ya no tiene la huella/.test(e.message)) { borrar('rpos.huella'); pintarLoginHuella(); }
      $('#lError').textContent = mensajeWebAuthn(e); $('#lError').hidden = false;
    }
  };
  async function pintarHuellaMas() {
    const h = huellaLocal(), aqui = h && h.usuario === S.usuario;
    const boton = $('#huellaBoton'), texto = $('#huellaTexto'), lista = $('#huellaEquipos');
    boton.hidden = true; lista.hidden = true;
    if (!huella.disponible) { texto.textContent = 'El administrador todavía no la activó en el servidor (necesita HTTPS).'; return; }
    if (!huellaPosible()) { texto.innerHTML = `Disponible al abrir la app en <b class="text-fg">${esc(huella.origen || 'la dirección HTTPS')}</b>.`; return; }
    texto.textContent = aqui ? `Activa en este teléfono (${h.nombre || 'este equipo'}).` : 'Entre sin escribir la clave en este teléfono.';
    boton.hidden = false; boton.textContent = aqui ? 'Desactivar' : 'Activar';
    boton.className = 'shrink-0 h-9 px-3 rounded-lg text-sm font-semibold ' + (aqui ? 'border border-line text-bad' : 'bg-p600 text-onp');
    try {
      const r = await api('/api/huella/equipos');
      const eq = r.equipos || [];
      lista.hidden = !eq.length;
      lista.innerHTML = eq.map(x => `<li class="flex items-center gap-3 py-2.5"><div class="min-w-0 flex-1"><div class="font-medium truncate">${esc(x.nombre)}${h && h.id === x.id ? ' <span class="chip chip-ok ml-1">este</span>' : ''}</div>
        <div class="text-xs text-muted">Activada ${esc((x.creado || '').substring(0, 10))}${x.ultimoUso ? ' · último uso ' + esc(x.ultimoUso.substring(0, 16)) : ''}</div></div>
        <button data-quitar-huella="${esc(x.id)}" class="text-xs font-semibold text-bad px-2 py-1 rounded-lg hover:bg-badbg">Quitar</button></li>`).join('');
    } catch (e) { }
  }
  async function quitarHuella(id) {
    await api('/api/huella/quitar', { id });
    const h = huellaLocal(); if (h && h.id === id) borrar('rpos.huella');
    toast('Huella quitada'); pintarHuellaMas(); pintarLoginHuella();
  }
  document.addEventListener('click', e => { const b = e.target.closest('[data-quitar-huella]'); if (b) quitarHuella(b.dataset.quitarHuella).catch(err => toast(err.message)); });
  $('#huellaBoton').onclick = async () => {
    const h = huellaLocal();
    if (h && h.usuario === S.usuario) { try { await quitarHuella(h.id); } catch (e) { borrar('rpos.huella'); pintarHuellaMas(); } return; }
    try {
      const o = await api('/api/huella/registro/opciones', {});
      const cred = await navigator.credentials.create({ publicKey: {
        challenge: deB64u(o.challenge), rp: o.rp, user: { id: deB64u(o.user.id), name: o.user.name, displayName: o.user.displayName },
        pubKeyCredParams: o.pubKeyCredParams, authenticatorSelection: o.authenticatorSelection, timeout: o.timeout, attestation: 'none',
        excludeCredentials: (o.excludeCredentials || []).map(c => ({ type: 'public-key', id: deB64u(c.id) }))
      } });
      const res = cred.response;
      const llave = res.getPublicKey ? res.getPublicKey() : null;
      if (!llave || !res.getAuthenticatorData) throw new Error('Este navegador no permite activar la huella. Actualice el navegador o use Chrome / Safari.');
      const nombre = nombreEquipo();
      await api('/api/huella/registro', { id: cred.id, clientDataJSON: b64u(res.clientDataJSON), authenticatorData: b64u(res.getAuthenticatorData()), publicKey: b64u(llave), alg: res.getPublicKeyAlgorithm(), nombre });
      guardar('rpos.huella', JSON.stringify({ usuario: S.usuario, id: cred.id, nombre }));
      toast('Listo: la próxima vez entre con su huella o Face ID'); pintarHuellaMas(); pintarLoginHuella();
    } catch (e) { toast(mensajeWebAuthn(e)); }
  };

  function salir(motivo) {
    S.token = null; borrar('rpos.sesion');
    if (ctlEspera) ctlEspera.abort(); if (ctlV) ctlV.abort(); if (ctlC) ctlC.abort();
    ventas = { clave: '', datos: new Map() }; cierres = { clave: '', datos: new Map() }; canales = []; mensajes = []; canalAbierto = null; noLeidos = 0;
    marcaCanales = ''; for (const k in leidoHasta) delete leidoHasta[k]; contactos = []; contactosLeidos = 0; usuarios = []; if (ctlMsj) ctlMsj.abort();
    cerrar(); mostrar('vLogin');
    $('#lUsuario').value = leer('rpos.ultimoUsuario') || '';
    $('#lAviso').hidden = !motivo; $('#lAviso').textContent = motivo || '';
    pintarLoginHuella();
    if (!huellaLocal()) ($('#lUsuario').value ? $('#lClave') : $('#lUsuario')).focus();
  }

  async function entrar() {
    let r;
    try { r = await api('/api/sucursales'); }
    catch (e) {
      if (e.codigo === 403) { mostrar('vClave'); return; }
      if (e.codigo !== 401) { mostrar('vLogin'); $('#lError').textContent = e.message; $('#lError').hidden = false; }
      return;
    }
    S.sucursales = r.sucursales || []; S.permisos = r.permisos || S.permisos; S.nombre = r.nombre || S.nombre; S.rol = r.rol || S.rol;
    guardarSesion();
    cargarSeleccion();
    $('#sideNombre').textContent = S.nombre; $('#sideRol').textContent = rol(S.rol); $('#sideIniciales').textContent = iniciales(S.nombre);
    $('#sideTiendas').textContent = S.sucursales.length === 1 ? S.sucursales[0] : S.sucursales.length + ' tiendas';
    pintarMas(); mostrar('vApp');
    api('/api/salud').then(s => { $('#masVersion').textContent = `Reportes POS · API ${s.version}`; }).catch(() => { });
    const v0 = leer('rpos.vista');
    ir(VISTAS.some(v => v.id === v0) ? v0 : 'ventas');
    if (tiene('chat')) { cargarCanales().then(esperarCambios); }
    cargarTasa(false);
  }

  // cierre por inactividad
  let ultimoUso = Date.now();
  ['pointerdown', 'keydown', 'scroll', 'touchstart'].forEach(ev => document.addEventListener(ev, () => { ultimoUso = Date.now(); }, { passive: true, capture: true }));
  setInterval(() => {
    if (!S.token) return;
    if (Date.now() - ultimoUso > INACTIVIDAD_MIN * 60e3) salir(`Se cerró la sesión por ${INACTIVIDAD_MIN} minutos sin uso.`);
    else if (S.expira && new Date(S.expira) < new Date()) salir('Su sesión venció. Ingrese de nuevo.');
  }, 30e3);

  // actualizar solo las ventas y cierres de hoy cada 2 minutos (si la app está a la vista)
  setInterval(() => {
    if (!S.token || document.hidden) return;
    if (vista === 'ventas' && rV.tipo === 'hoy') cargarVentas(true);
    else if (vista === 'cierres' && rC.tipo === 'hoy') cargarCierres(true);
  }, 120e3);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden || !S.token) return;
    if (Date.now() - ultimoUso > INACTIVIDAD_MIN * 60e3) { salir(`Se cerró la sesión por ${INACTIVIDAD_MIN} minutos sin uso.`); return; }
    ultimoUso = Date.now();
  });
  let chatResize; window.addEventListener('resize', () => { clearTimeout(chatResize); chatResize = setTimeout(() => { if (vista === 'chat') { pintarChat(); encabezado(); } }, 200); });

  // ---------------------------------------------------------------- inicio
  aplicarTema();
  if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('sw.js').catch(() => { });
  cargarSesion();
  cargarEstadoHuella();
  if (S.token) entrar(); else salir();
})();
