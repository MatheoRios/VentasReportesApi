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

