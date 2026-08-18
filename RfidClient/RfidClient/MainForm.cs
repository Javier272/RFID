using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using UHFReader.Readers;
using WebSocketSharp.Server;

namespace RfidClient
{
    public partial class MainForm : Form
    {
        /** Members Begin **/
        public TagPool tagPool;
        public UHFReader.Reader reader;
        public WebSocketServer webSocket;
        /** Members End **/

        /** Constants Begin **/
        public const string wsServer = "ws://0.0.0.0";
        public const string wsTagPool = "/tag-pool";
        public const string wsTagConnect = "/tag-connect";
        public const string wsTagDisconnect = "/tag-disconnect";
        public const int poolInterval = 1000;
        public const int readInterval = 100;
        public const int connectTicks = 0;
        public const int disconnectTicks = 2000;
        /** Constants End **/

        /** Lógica de Borde (Filtro Flota) Begin **/
        private HashSet<string> _tagsAutorizados = new HashSet<string>();
        private Dictionary<string, DateTime> _ultimaLecturaTags = new Dictionary<string, DateTime>();
        private const double CooldownSegundos = 5.0;
        private const string ArchivoBackup = "backup_tags_flota.json";
        private System.Timers.Timer _timerSincronizacion;
        private static readonly HttpClient httpClient = new HttpClient();
        
        // URLs del servidor Node.js
        private const string ApiGetUrl = "http://localhost:3000/api/rfid/sincronizar";
        private const string ApiPostUrl = "http://localhost:3000/api/rfid/lectura";
        /** Lógica de Borde (Filtro Flota) End **/

        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // Inicializar Caché y Sincronización
            CargarTagsLocales();
            IniciarTimerSincronizacion();

            //启动Websocket服务器 (Inicia WS Original del proyecto)
            try
            {
                this.webSocket = new WebSocketServer(wsServer);
                webSocket.AddWebSocketService<WsTagConnect>(wsTagConnect);
                webSocket.AddWebSocketService<WsTagDisconnect>(wsTagDisconnect);
                webSocket.AddWebSocketService<WsTagPool>(wsTagPool);
                webSocket.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error - Start Websocket Server");
                Application.Exit();
                return;
            }

            //获取ServiceHosts
            WebSocketServiceHost hostTagConnect;
            WebSocketServiceHost hostTagDisconnect;
            if (!webSocket.WebSocketServices.TryGetServiceHost(wsTagConnect, out hostTagConnect) ||
                !webSocket.WebSocketServices.TryGetServiceHost(wsTagDisconnect, out hostTagDisconnect))
            {
                MessageBox.Show("Please restart and try again.", "Error - Get Websocket ServiceHost");
                Application.Exit();
                return;
            }

            //挂载Tag Connect、Disconnect
            this.tagPool = new TagPool();
            this.tagPool.OnConnected += (tp, te) => wsBroadcast(te.Tags, hostTagConnect);
            this.tagPool.OnDisconnected += (tp, te) => wsBroadcast(te.Tags, hostTagDisconnect);

            //尝试连接读卡设备 (Auto Conectar)
            this.notifyIcon.Icon = Properties.Resources.IconWarning;
            btnConnect_Click(sender, e);
        }

        private void wsBroadcast(object obj, WebSocketServiceHost host)
        {
            var json = JToken.FromObject(obj);
            var str = json.ToString(Newtonsoft.Json.Formatting.None);
            host.Sessions.BroadcastAsync(str, null);
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                // CONEXIÓN POR RED MODIFICADA PARA IP 192.168.1.190
                this.reader = new NetReader(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.190"), 6000));
                
                this.btnConnect.Enabled = false;
                this.notifyIcon.Icon = Properties.Resources.IconOK;
                this.Hide();
                timerRead.Interval = readInterval;
                timerRead.Enabled = true;
                timerPool.Interval = poolInterval;
                timerPool.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error - Connect to Reader");
            }
        }

        private void timerRead_Tick(object sender, EventArgs e)
        {
            List<byte[]> epcList;
            try
            {
                // El SDK lee los tags físicos disponibles en la antena
                epcList = reader.Inventory_G2(0, 0, 0);
            }
            catch
            {
                return;
            }

            // Procesamiento y Filtrado hacia Node.js
            if (epcList != null && epcList.Count > 0)
            {
                foreach (byte[] epcBytes in epcList)
                {
                    // Convertir el arreglo de bytes a String Hexadecimal limpio
                    string epcHex = BitConverter.ToString(epcBytes).Replace("-", "").ToUpper();

                    if (EvaluarYFiltrarTag(epcHex))
                    {
                        Console.WriteLine($"[C#] ✅ UNIDAD AUTORIZADA DETECTADA: {epcHex}");
                        
                        // Disparar la petición HTTP al servidor Node en un hilo secundario
                        Task.Run(() => NotificarWebappHttp(epcHex));
                    }
                }
            }

            // Lógica original de la UI
            tagPool.Throw(epcList);
            tagPool.Check(connectTicks, disconnectTicks);
        }

        private void timerPool_Tick(object sender, EventArgs e)
        {
            WebSocketServiceHost hostTagPool;
            if (webSocket.WebSocketServices.TryGetServiceHost(wsTagPool, out hostTagPool))
            {
                wsBroadcast(tagPool.Values, hostTagPool);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void btnOpen_Click(object sender, EventArgs e)
        {
            this.Show();
        }

        private void btnExit_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.btnOpen_Click(sender, e);
        }

        #region MÉTODOS DE FILTRADO Y RED (NUEVO)

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
                        Console.WriteLine($"[CACHE] Cargados {_tagsAutorizados.Count} tags desde respaldo local.");
                    }
                }
                catch { }
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
            _timerSincronizacion = new System.Timers.Timer(900000); // 15 Minutos
            _timerSincronizacion.Elapsed += async (s, ev) => await SincronizarDesdeBackendAsync();
            _timerSincronizacion.AutoReset = true;
            _timerSincronizacion.Start();

            // Carga inicial
            Task.Run(() => SincronizarDesdeBackendAsync());
        }

        private async Task SincronizarDesdeBackendAsync()
        {
            try
            {
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                HttpResponseMessage response = await httpClient.GetAsync(ApiGetUrl);

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
                        Console.WriteLine($"[SINC] Sincronización exitosa. Tags activos: {_tagsAutorizados.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SINC] Servidor offline. Usando base local. Error: {ex.Message}");
            }
        }

        private bool EvaluarYFiltrarTag(string epc)
        {
            // Filtro 1: Base de Datos
            lock (_tagsAutorizados)
            {
                if (!_tagsAutorizados.Contains(epc)) return false;
            }

            // Filtro 2: Antirrebote (Cooldown)
            if (_ultimaLecturaTags.ContainsKey(epc))
            {
                if ((DateTime.Now - _ultimaLecturaTags[epc]).TotalSeconds < CooldownSegundos)
                {
                    return false;
                }
            }

            _ultimaLecturaTags[epc] = DateTime.Now;
            return true;
        }

        private async Task NotificarWebappHttp(string epc)
        {
            try
            {
                var payload = new { epc = epc, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                var jsonContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                HttpResponseMessage response = await httpClient.PostAsync(ApiPostUrl, jsonContent);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[HTTP] Evento POST enviado con éxito a Node.js: {epc}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Error enviando a Node.js: {ex.Message}");
            }
        }

        #endregion
    }

    public class BackendResponse
    {
        public bool success { get; set; }
        public List<string> tags { get; set; }
    }
}