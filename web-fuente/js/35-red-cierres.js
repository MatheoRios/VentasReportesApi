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

