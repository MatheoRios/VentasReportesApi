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

