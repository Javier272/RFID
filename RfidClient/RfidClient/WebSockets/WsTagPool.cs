// RfidClient/WebSockets/WsTagPool.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using WebSocketSharp;

namespace RfidClient.WebSockets
{
    public class WsTagPool
    {
        private readonly WebSocket _ws;
        private HashSet<string> _tagsAutorizados = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _ultimaLecturaTags = new Dictionary<string, DateTime>();
        private const double CooldownSegundos = 5.0; // Tiempo de espera antirrebote
        private const string ArchivoBackup = "backup_tags_flota.json";
        private Timer _timerSincronizacion;
        private readonly string _apiGetUrl = "http://localhost:3000/api/rfid/sincronizar";

        public WsTagPool(WebSocket ws)
        {
            _ws = ws;
            
            // 1. Cargar caché local de inmediato al arrancar
            CargarTagsLocales();
            
            // 2. Configurar el Timer para ejecutarse cada 15 minutos (900,000 ms)
            IniciarTimerSincronizacion();
        }

        private void CargarTagsLocales()
        {
            if (File.Exists(ArchivoBackup))
            {
                try
                {
                    string json = File.ReadAllText(ArchivoBackup);
                    var lista = JsonConvert.DeserializeObject<List<string>>(json);
                    if (lista != null)
                    {
                        _tagsAutorizados = new HashSet<string>(lista);
                        Console.WriteLine($"[C# CACHE] Cargados {_tagsAutorizados.Count} tags desde el respaldo local.");
                    }
                }
                catch { /* Falla silenciosa en producción */ }
            }
        }

        private void GuardarTagsLocales()
        {
            try
            {
                string json = JsonConvert.SerializeObject(new List<string>(_tagsAutorizados));
                File.WriteAllText(ArchivoBackup, json);
            }
            catch { }
        }

        private void IniciarTimerSincronizacion()
        {
            _timerSincronizacion = new Timer(900000); // 15 minutos
            _timerSincronizacion.Elapsed += async (sender, e) => await SincronizarDesdeBackendAsync();
            _timerSincronizacion.AutoReset = true;
            _timerSincronizacion.Start();

            // Primera sincronización asíncrona al iniciar
            Task.Run(() => SincronizarDesdeBackendAsync());
        }

        public async Task SincronizarDesdeBackendAsync()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(4);
                    HttpResponseMessage response = await client.GetAsync(_apiGetUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        var data = JsonConvert.DeserializeObject<BackendResponse>(content);
                        if (data != null && data.tags != null)
                        {
                            lock (_tagsAutorizados)
                            {
                                _tagsAutorizados = new HashSet<string>(data.tags);
                            }
                            GuardarTagsLocales();
                            Console.WriteLine($"[C# SINC] Sincronización HTTP Exitosa. Total tags vigentes: {_tagsAutorizados.Count}");
                        }
                    }
                }
            }
            catch
            {
                Console.WriteLine("[C# SINC] Servidor Node.js offline o inalcanzable. Usando caché local persistente.");
            }
        }

        /// <summary>
        /// Aplica las reglas de filtrado de base de datos y cooldown por software.
        /// </summary>
        public bool EvaluarYFiltrarTag(string epc)
        {
            epc = epc.Trim().ToUpper();

            // Filtro 1: ¿Pertenece a la Flota Autorizada (Base de Datos)?
            lock (_tagsAutorizados)
            {
                if (!_tagsAutorizados.Contains(epc))
                {
                    return false; // Ignorar tags ajenos
                }
            }

            // Filtro 2: Cooldown Antirrebote (Evita saturar la red por lecturas repetidas)
            if (_ultimaLecturaTags.ContainsKey(epc))
            {
                if ((DateTime.Now - _ultimaLecturaTags[epc]).TotalSeconds < CooldownSegundos)
                {
                    return false; // El camión sigue en el radio de la antena, ignorar ráfaga
                }
            }

            // Si pasa ambos filtros, actualizamos el tiempo de lectura y autorizamos
            _ultimaLecturaTags[epc] = DateTime.Now;
            return true;
        }

        public void Send(string epc)
        {
            if (_ws != null && _ws.IsAlive)
            {
                // Enviamos el EPC estructurado en un JSON limpio hacia Node.js
                var payload = new { epc = epc.Trim().ToUpper() };
                _ws.Send(JsonConvert.SerializeObject(payload));
            }
        }
    }

    public class BackendResponse
    {
        public bool success { get; set; }
        public List<string> tags { get; set; }
    }
}