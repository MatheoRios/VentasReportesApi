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

