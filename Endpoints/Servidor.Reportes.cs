// Generado al dividir Program.cs por módulos: el código de cada parte es el mismo que estaba en Program.cs.
namespace VentasReportesApi;

public sealed partial class Servidor
{
    private void MapearEstadoTiendas()
    {
        app.MapGet("/api/estado-tiendas", async (HttpContext ctx, bool? refrescar) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            var permitidas = new HashSet<string>(Permitidas(u), StringComparer.CurrentCultureIgnoreCase);
            var tiendas = almacen.ListarTiendas().Where(t => t.Activa && permitidas.Contains(t.Nombre)).ToList();
            if (refrescar == true) foreach (var t in tiendas) cacheEstado.TryRemove(t.Nombre, out _);
            var tareas = tiendas.Select(async t =>
            {
                var e = await EstadoTiendaAsync(t, ctx.RequestAborted);
                return new { nombre = t.Nombre, ok = e.ok, ms = e.ms, mensaje = e.msg };
            });
            return Results.Ok(new { tiendas = await Task.WhenAll(tareas) });
        });

    }

    private IResult? ValidarConsultaTienda(HttpContext ctx, string sucursal, DateTime desde, DateTime hasta, int maxDias,
                                           string mensajeMaxDias, string permiso, string nombrePermiso, string mensajeSinPermiso,
                                           out Usuario u, out string permitida)
    {
        permitida = "";
        u = Autenticar(ctx)!;
        if (u == null) return Error(401, "Sesión vencida o inválida.");
        if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
        if (hasta < desde) return Error(400, "La fecha Desde no puede ser posterior a Hasta.");
        if ((hasta - desde).TotalDays > maxDias) return Error(400, mensajeMaxDias);
        if (!u.Tiene(permiso))
        {
            almacen.Auditar(u.Login, "DENEGADO", sucursal, desde, hasta, null, "Sin permiso " + nombrePermiso, Ip(ctx));
            return Error(403, mensajeSinPermiso);
        }
        var p = Permitidas(u).FirstOrDefault(s => s.Equals(sucursal, StringComparison.CurrentCultureIgnoreCase));
        if (p == null)
        {
            almacen.Auditar(u.Login, "DENEGADO", sucursal, desde, hasta, null, "Sin permiso", Ip(ctx));
            return Error(403, $"No tiene acceso a la sucursal {sucursal}.");
        }
        permitida = p;
        return null;
    }

    private string CadenaObligatoria(string tienda) =>
        CadenaDeTienda(tienda) ?? throw new InvalidOperationException($"La tienda {tienda} no está activa o fue eliminada.");

    private void MapearReportes()
    {
        app.MapPost("/api/reporte", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "ventas", "al reporte de ventas", "No tiene permiso para el reporte de ventas. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            // la misma consulta pedida por varios usuarios se ejecuta una sola vez (y se guarda unos minutos)
            return await ConsultarTienda(ctx, u, "ventas", permitida, p.Desde, p.Hasta, "", "",
                tk => ConsultaSucursal.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Filas.Count,
                r => new { sucursal = permitida, columnas = r.Columnas, filas = r.Filas });
        });

        // ------------------------------------------------------------------ cierres de caja (conciliación y arqueos)
        // Permiso aparte y NO se asigna solo: muestra faltantes y sobrantes de los cajeros (auditoría).
        // ------------------------------------------------------------------ análisis de productos por mes (migración)
        // Permiso propio: "productos".
        app.MapPost("/api/productos-mes", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "productos", "al análisis de productos", "No tiene permiso para el análisis de productos. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "productos-mes", permitida, p.Desde, p.Hasta, "", " (análisis de productos)",
                tk => ConsultaProductosMes.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Meses.Count + r.Articulos.Count + r.Bloqueados.Count + r.Inactivos.Count,
                r => new { sucursal = permitida, meses = r.Meses, articulos = r.Articulos, camposFicha = r.CamposFicha, bloqueados = r.Bloqueados, inactivos = r.Inactivos });
        });

        // ------------------------------------------------------------------ listado de ventas (documento + renglones + pagos)
        // Permiso propio: "listado". Hasta 93 días por consulta: es el reporte más pesado (una fila por renglón).
        app.MapPost("/api/listado", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 93, "El listado de ventas admite hasta 3 meses por consulta.",
                "listado", "al listado de ventas", "No tiene permiso para el listado de ventas. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "listado", permitida, p.Desde, p.Hasta, "", " (listado de ventas)",
                tk => ConsultaListado.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Lineas.Count,
                r => new { sucursal = permitida, documentos = r.Documentos, lineas = r.Lineas, pagos = r.Pagos, pagosDisponibles = r.PagosDisponibles });
        });

        // ------------------------------------------------------------------ entradas de mercancía (KLK_ENTRADAHDR, permiso "entradas")
        app.MapPost("/api/entradas", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "entradas", "a entradas de mercancía", "No tiene permiso para el reporte de entradas de mercancía. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "entradas", permitida, p.Desde, p.Hasta, "", " (entradas de mercancía)",
                tk => ConsultaEntradas.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Count,
                r => new { sucursal = permitida, filas = r });
        });

        // ------------------------------------------------------------------ libro de ventas I.V.A. (permiso "impuestos")
        // Una tienda por petición: una fila por reporte Z (impresora fiscal + Z) y una por nota de crédito.
        app.MapPost("/api/libroiva", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 93, "El libro de ventas admite hasta 3 meses por consulta.",
                "impuestos", "al libro de ventas I.V.A.", "No tiene permiso para el libro de ventas I.V.A. (permiso «Impuestos»). Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "libroiva", permitida, p.Desde, p.Hasta, "", " (libro de ventas IVA)",
                tk => ConsultaLibroIva.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Filas.Count,
                r => new { sucursal = permitida, filas = r.Filas, avisos = r.Avisos });
        });

        // ------------------------------------------------------------------ compras de productos (base central Stellar, VAD10)
        // Permiso propio: "compras". No es por tienda: una sola base, cuya conexión va en Reportes:ConexionCompras
        // (en Linux: /etc/ventasreportesapi/compras.env, lo escribe linux/configurar-compras.sh; nunca en el código).
        // Localidades que se pueden elegir: Reportes:LocalidadesCompras = "14=CEDIS SECO;17=CEDIS FRIO;18=CEDIS VERDE"
        List<(string Codigo, string Nombre)> LocalidadesCompras() =>
            (cfg.GetValue<string>("LocalidadesCompras") ?? "14=CEDIS SECO;17=CEDIS FRIO;18=CEDIS VERDE")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => { var p = x.Split('=', 2, StringSplitOptions.TrimEntries); return (p[0], p.Length > 1 ? p[1] : p[0]); })
                .Where(x => x.Item1.Length > 0).ToList();

        app.MapGet("/api/compras/localidades", (HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            if (!u.Tiene("compras")) return Error(403, "No tiene permiso para el reporte de compras.");
            return Results.Ok(new { localidades = LocalidadesCompras().Select(l => new { codigo = l.Codigo, nombre = l.Nombre }) });
        });

        // Últimos costos de compra: una fila por producto con su última compra (misma base, permiso y localidades que Compras)
        app.MapPost("/api/compras/ultimos-costos", async (ComprasPeticion p, HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            if (p.Hasta < p.Desde) return Error(400, "La fecha Desde no puede ser posterior a Hasta.");
            if ((p.Hasta - p.Desde).TotalDays > 800) return Error(400, "El rango máximo es de 2 años.");
            if (!u.Tiene("compras"))
            {
                almacen.Auditar(u.Login, "DENEGADO", "COMPRAS", p.Desde, p.Hasta, null, "Sin permiso a últimos costos de compra", Ip(ctx));
                return Error(403, "No tiene permiso para ver costos de compra (permiso «Compras»). Solicítelo al administrador.");
            }
            string? cadena = cfg.GetValue<string>("ConexionCompras");
            if (string.IsNullOrWhiteSpace(cadena))
                return Error(503, "La base de compras todavía no está configurada en el servidor.");
            var validas = LocalidadesCompras().Select(l => l.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var locs = (p.Localidades ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
            var fuera = locs.Where(x => !validas.Contains(x)).ToList();
            if (fuera.Count > 0) return Error(400, "Localidad no permitida: " + string.Join(", ", fuera));
            string etiqueta = (locs.Count == 0 ? " (últimos costos: todas" : " (últimos costos: " + string.Join(",", locs)) + (p.SoloConRecepcion ? ", solo con recepción)" : ")");
            return await ConsultarTienda(ctx, u, "ucostos", "COMPRAS", p.Desde, p.Hasta, string.Join(",", locs) + (p.SoloConRecepcion ? "|REC" : ""), etiqueta,
                tk => ConsultaUltimosCostos.EjecutarAsync(cadena, p.Desde, p.Hasta, locs, timeoutSeg, tk, p.SoloConRecepcion,
                                                          cfg.GetValue<string>("LocalidadOrigenOdc") ?? "00"),
                r => r.Count,
                r => new { filas = r });
        });

        app.MapPost("/api/compras", async (ComprasPeticion p, HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            if (p.Hasta < p.Desde) return Error(400, "La fecha Desde no puede ser posterior a Hasta.");
            if ((p.Hasta - p.Desde).TotalDays > 400) return Error(400, "El rango máximo es de 400 días.");
            if (!u.Tiene("compras"))
            {
                almacen.Auditar(u.Login, "DENEGADO", "COMPRAS", p.Desde, p.Hasta, null, "Sin permiso al reporte de compras", Ip(ctx));
                return Error(403, "No tiene permiso para el reporte de compras. Solicítelo al administrador.");
            }
            string? cadena = cfg.GetValue<string>("ConexionCompras");
            if (string.IsNullOrWhiteSpace(cadena))
                return Error(503, "El reporte de compras todavía no está configurado en el servidor (falta la conexión a la base de compras).");
            // localidades pedidas: solo las de la lista configurada; vacío = todas (sin filtro)
            var validas = LocalidadesCompras().Select(l => l.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var locs = (p.Localidades ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
            var fuera = locs.Where(x => !validas.Contains(x)).ToList();
            if (fuera.Count > 0) return Error(400, "Localidad no permitida: " + string.Join(", ", fuera));
            string etiqueta = locs.Count == 0 ? " (compras: todas)" : " (compras: " + string.Join(",", locs) + ")";
            return await ConsultarTienda(ctx, u, "compras", "COMPRAS", p.Desde, p.Hasta, string.Join(",", locs), etiqueta,
                tk => ConsultaCompras.EjecutarAsync(cadena, p.Desde, p.Hasta, locs, timeoutSeg, tk, cfg.GetValue<string>("LocalidadOrigenOdc") ?? "00"),
                r => r.Lineas.Count + r.LineasOdc.Count,
                r => new { documentos = r.Documentos, lineas = r.Lineas, columnaFecha = r.ColumnaFecha,
                           ordenes = r.Ordenes, lineasOdc = r.LineasOdc, avisoOdc = r.AvisoOdc,
                           recepcionesOdc = r.RecepcionesOdc });
        });

        // ------------------------------------------------------------------ tasa del día (para la calculadora)
        // Última TasaDeCambio de los cierres X de las tiendas del usuario (la primera que responda). Caché de 10 min.
        (decimal Tasa, DateTime Fecha, string Tienda, DateTime Leida)? tasaCache = null;
        var tasaCandado = new SemaphoreSlim(1, 1);
        app.MapGet("/api/tasa", async (HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            var c = tasaCache;
            if (c.HasValue && c.Value.Leida > DateTime.UtcNow.AddMinutes(-10))
                return Results.Ok(new { tasa = c.Value.Tasa, fecha = c.Value.Fecha.ToString("yyyy-MM-dd HH:mm"), tienda = c.Value.Tienda });
            await tasaCandado.WaitAsync(ctx.RequestAborted);
            try
            {
                c = tasaCache;
                if (c.HasValue && c.Value.Leida > DateTime.UtcNow.AddMinutes(-10))
                    return Results.Ok(new { tasa = c.Value.Tasa, fecha = c.Value.Fecha.ToString("yyyy-MM-dd HH:mm"), tienda = c.Value.Tienda });
                string ultimoError = "ninguna tienda disponible";
                foreach (var suc in Permitidas(u).Take(4))
                {
                    string? cadenaSuc = CadenaDeTienda(suc);
                    if (cadenaSuc == null) continue;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    cts.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        var leida = await ConsultaTasa.UltimaAsync(cadenaSuc, cts.Token);
                        if (leida.HasValue)
                        {
                            var t = (leida.Value.Tasa, leida.Value.Fecha, suc, DateTime.UtcNow);
                            tasaCache = t;
                            return Results.Ok(new { tasa = t.Item1, fecha = t.Item2.ToString("yyyy-MM-dd HH:mm"), tienda = suc });
                        }
                        ultimoError = $"{suc} no tiene cierres con tasa";
                    }
                    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested) { ultimoError = $"{suc}: {ex.Message}"; }
                }
                return Error(502, "No se pudo leer la tasa (" + ultimoError + ").");
            }
            finally { tasaCandado.Release(); }
        });

        app.MapPost("/api/cierres", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "cierres", "a cierres de caja", "No tiene permiso para el reporte de cierres de caja. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "cierres", permitida, p.Desde, p.Hasta, "", " (cierres)",
                tk => ConsultaCierres.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Cierres.Count + r.Arqueos.Count + r.SinCierre.Count,
                r => new { sucursal = permitida, formas = r.Formas, signos = r.Signos, cierres = r.Cierres, arqueos = r.Arqueos, sinCierre = r.SinCierre, sinCierreDisponible = r.SinCierreDisponible });
        });

        // ------------------------------------------------------------------ ventas diarias (una fila por día)
        app.MapPost("/api/diario", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "diario", "a ventas diarias", "No tiene permiso para el reporte de ventas diarias. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "diario", permitida, p.Desde, p.Hasta, "", " (diario)",
                tk => ConsultaDiaria.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => r.Count,
                r => new { sucursal = permitida, filas = r });
        });

        // ------------------------------------------------------------------ ventas por categoría (una fila por artículo)
        // Ventas por categoría y Resumen por artículo usan la MISMA consulta (y la misma caché): cada uno con su permiso.
        foreach (var (ruta, permiso, nombre, etiqueta) in new[]
        {
            ("/api/categorias", "categorias", "ventas por categoría", " (categorías)"),
            ("/api/articulos",  "articulos",  "resumen por artículo", " (artículos)")
        })
        {
            app.MapPost(ruta, async (CategoriasPeticion p, HttpContext ctx) =>
            {
                var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                    permiso, "a " + nombre, $"No tiene permiso para el reporte {nombre}. Solicítelo al administrador.",
                    out var u, out var permitida);
                if (err != null) return err;

                string caja = (p.Caja ?? "").Trim();
                if (caja.Length > 30) return Error(400, "El código de caja es demasiado largo.");
                string Tri(string? v) => v is "0" or "1" ? v : "";

                string nf = Tri(p.NoFiscal), cr = Tri(p.Credito);
                return await ConsultarTienda(ctx, u, "categorias", permitida, p.Desde, p.Hasta, caja + "|" + nf + "|" + cr, etiqueta,
                    tk => ConsultaCategorias.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, caja, nf, cr, timeoutSeg, tk),
                    r => r.Count,
                    r => new { sucursal = permitida, filas = r });
            });
        }

        // ------------------------------------------------------------------ resumen de ventas por tienda
        app.MapPost("/api/resumen", async (ReportePeticion p, HttpContext ctx) =>
        {
            var err = ValidarConsultaTienda(ctx, p.Sucursal, p.Desde, p.Hasta, 400, "El rango máximo es de 400 días.",
                "resumen", "al resumen por tienda", "No tiene permiso para el resumen de ventas por tienda. Solicítelo al administrador.",
                out var u, out var permitida);
            if (err != null) return err;

            return await ConsultarTienda(ctx, u, "resumen", permitida, p.Desde, p.Hasta, "", " (resumen)",
                tk => ConsultaResumen.EjecutarAsync(CadenaObligatoria(permitida), p.Desde, p.Hasta, timeoutSeg, tk),
                r => 1,
                r => new
                {
                    sucursal = permitida, facturas = r.Facturas, notasCredito = r.NotasCredito,
                    ventaBruta = r.VentaBruta, devoluciones = r.Devoluciones, ventaNeta = r.VentaNeta, unidades = r.Unidades
                });
        });
    }
}
