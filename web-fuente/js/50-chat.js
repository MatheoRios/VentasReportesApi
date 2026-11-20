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

