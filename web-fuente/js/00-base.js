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

