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

