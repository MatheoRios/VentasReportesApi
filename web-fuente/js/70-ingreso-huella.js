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

