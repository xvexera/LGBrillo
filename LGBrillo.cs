// LGBrillo: controls the brightness of an LG webOS monitor over the network (SSAP).
// Tray icon with a slider, plus Ctrl+Alt+Up / Ctrl+Alt+Down shortcuts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("LGBrillo")]
[assembly: System.Reflection.AssemblyProduct("LGBrillo")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace LGBrillo {

static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args) {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Log.Write("UNHANDLED " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("FATAL " + e.ExceptionObject);
        SetProcessDPIAware();
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.S = g.DpiX / 96f;
        if (args.Length == 2 && args[0] == "--make-icon") { Ui.MakeIcon(args[1]); return; }

        bool nuevo;
        using (var mx = new Mutex(true, "LGBrillo-instancia-unica", out nuevo)) {
            if (!nuevo) return;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.Write("start " + Application.ProductVersion + " on Windows build " + Environment.OSVersion.Version.Build);
            Application.Run(new App());
        }
    }
}

// Small rolling log in %APPDATA%\LGBrillo\log.txt, to diagnose "it sometimes doesn't work". Never contains IP addresses.
static class Log {
    static readonly object gate = new object();
    static readonly Regex ip = new Regex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b");

    public static string FilePath {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LGBrillo", "log.txt"); }
    }

    public static void Write(string s) {
        try {
            lock (gate) {
                var fi = new FileInfo(FilePath);
                Directory.CreateDirectory(fi.DirectoryName);
                if (fi.Exists && fi.Length > 256 * 1024) {
                    File.Delete(FilePath + ".old");
                    File.Move(FilePath, FilePath + ".old");
                }
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + ip.Replace(s, "<ip>") + "\r\n");
            }
        } catch { }
    }
}

class Config {
    public string Ip { get; set; }
    public string Key { get; set; }
    public int Paso { get; set; }
    public bool EyeCare { get; set; }
    public bool PixelRefresh { get; set; }
    public string Name { get; set; }
    public double OsdSegundos { get; set; }
    public string OsdPos { get; set; }   // "abajo" (bottom), "arriba" (top) or "arriba-der" (top right)
    public string Color { get; set; }    // bar color, "#RRGGBB"
    public bool HideScreenOff { get; set; }
    public bool HideSleep { get; set; }
    public bool HidePlan { get; set; }
    public int UpMods { get; set; }      // shortcuts: Alt=1, Ctrl=2, Shift=4, Win=8
    public int UpKey { get; set; }
    public int DownMods { get; set; }
    public int DownKey { get; set; }
    public string Design { get; set; }   // "win11" or "glass"
    public double GBlur { get; set; }
    public double GDarken { get; set; }
    public bool GRefract { get; set; }
    public double GBezel { get; set; }
    public bool GRgbOn { get; set; }
    public double GRgb { get; set; }
    public double GRgbHue { get; set; }
    public bool GRgbAnim { get; set; }
    public double GRgbSpeed { get; set; }
    public bool GLight { get; set; }
    public string GWave { get; set; }    // "lenta", "normal" or "rapida"

    public Config() { Ip = ""; Key = ""; Paso = 5; Name = ""; OsdSegundos = 1.5; OsdPos = "abajo"; Color = "#0A84FF";
        UpMods = 3; UpKey = (int)Keys.Up; DownMods = 3; DownKey = (int)Keys.Down;
        Design = "win11"; GBlur = 0.45; GDarken = 0.3; GRefract = true; GBezel = 0.45;
        GRgbOn = true; GRgb = 0.35; GRgbHue = 0; GRgbAnim = false; GRgbSpeed = 0.4; GLight = true; GWave = "normal";
 }

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LGBrillo"); } }
    static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

    public static Config Load() {
        try {
            var c = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(FilePath));
            if (c.Ip == null) c.Ip = "";
            if (c.Key == null) c.Key = "";
            if (c.Paso < 1 || c.Paso > 50) c.Paso = 5;
            if (c.Name == null) c.Name = "";
            if (c.OsdSegundos < 0.5 || c.OsdSegundos > 10) c.OsdSegundos = 1.5;
            if (c.OsdPos != "arriba" && c.OsdPos != "arriba-der") c.OsdPos = "abajo";
            if (string.IsNullOrEmpty(c.Color)) c.Color = "#0A84FF";
            if (c.Design != "glass") c.Design = "win11";
            if (c.GWave != "lenta" && c.GWave != "rapida") c.GWave = "normal";
            if (c.UpKey == 0 || c.DownKey == 0) { c.UpMods = 3; c.UpKey = (int)Keys.Up; c.DownMods = 3; c.DownKey = (int)Keys.Down; }
            return c;
        } catch { return new Config(); }
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
        } catch { }
    }
}

// SSAP connection (wss://ip:3001) to the monitor.
class Tv {
    const string LunaSet = "luna://com.webos.settingsservice/setSystemSettings";

    readonly JavaScriptSerializer js = new JavaScriptSerializer();
    readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> pend =
        new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
    readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    ClientWebSocket ws;
    TaskCompletionSource<string> regTcs;
    TaskCompletionSource<bool> firstValue;
    int nextId;
    bool prompted;

    public string Ip = "";
    public string Key = "";
    public event Action<int> BacklightChanged;
    public event Action Disconnected;
    public event Action PairingPrompt;

    public bool Connected { get { return ws != null && ws.State == WebSocketState.Open && regTcs != null && regTcs.Task.Status == TaskStatus.RanToCompletion; } }

    public async Task ConnectAsync() {
        Close();
        var s = new ClientWebSocket();
        s.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var cts = new CancellationTokenSource(4000))
            await s.ConnectAsync(new Uri("wss://" + Ip + ":3001/"), cts.Token);
        ws = s;
        regTcs = new TaskCompletionSource<string>();
        firstValue = new TaskCompletionSource<bool>();
        prompted = false;
        var loop = ReceiveLoop(s);

        var p = new Dictionary<string, object> {
            { "forcePairing", false }, { "pairingType", "PROMPT" }, { "manifest", Manifest.Get() }
        };
        if (Key != "") p["client-key"] = Key;
        await Send(new Dictionary<string, object> { { "type", "register" }, { "id", "reg0" }, { "payload", p } });

        // A paired monitor answers at once. Only wait a full minute while it is showing the pairing prompt;
        // a monitor that is still booting accepts the socket but stays silent, and must not block us that long.
        var reg = regTcs.Task;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!reg.IsCompleted) {
            bool pairing = Key == "" || prompted;
            if (waited.ElapsedMilliseconds > (pairing ? 60000 : 6000)) {
                Close();
                throw new TimeoutException(pairing
                    ? L.T("no se aceptó el aviso en el monitor", "the prompt on the monitor was not accepted")
                    : L.T("el monitor no respondió", "the monitor did not respond"));
            }
            await Task.WhenAny(reg, Task.Delay(200));
        }
        Key = await reg;

        await Send(new Dictionary<string, object> {
            { "type", "subscribe" }, { "id", "sub0" }, { "uri", "ssap://settings/getSystemSettings" },
            { "payload", new Dictionary<string, object> { { "category", "picture" }, { "keys", new[] { "backlight" } } } }
        });
        await Task.WhenAny(firstValue.Task, Task.Delay(3000));
    }

    public void Close() {
        var s = ws;
        ws = null;
        FailAll();
        if (s != null) { try { s.Abort(); } catch { } s.Dispose(); }
    }

    // Drops the connection and reports it, so the app reconnects.
    void Drop() {
        var s = ws;
        if (s != null) { try { s.Abort(); } catch { } }
    }

    void FailAll() {
        foreach (var t in new List<TaskCompletionSource<Dictionary<string, object>>>(pend.Values))
            t.TrySetException(new Exception(L.T("conexión cerrada", "connection closed")));
        pend.Clear();
        if (regTcs != null) regTcs.TrySetException(new Exception(L.T("conexión cerrada", "connection closed")));
    }

    async Task ReceiveLoop(ClientWebSocket s) {
        var buf = new byte[65536];
        var ms = new MemoryStream();
        try {
            while (s.State == WebSocketState.Open) {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do {
                    r = await s.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                Handle(Encoding.UTF8.GetString(ms.ToArray()));
            }
        } catch { }
        finally {
            if (ws == s) {
                ws = null;
                FailAll();
                if (Disconnected != null) Disconnected();
            }
        }
    }

    static string Str(Dictionary<string, object> d, string k) {
        object v;
        return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
    }

    void Handle(string text) {
        Dictionary<string, object> m;
        try { m = js.Deserialize<Dictionary<string, object>>(text); } catch { return; }
        string type = Str(m, "type"), id = Str(m, "id");
        object po;
        var payload = m.TryGetValue("payload", out po) ? po as Dictionary<string, object> : null;

        if (id == "reg0") {
            if (type == "registered") regTcs.TrySetResult(Str(payload, "client-key"));
            else if (type == "error") regTcs.TrySetException(new Exception(L.T("el monitor rechazó la conexión", "the monitor rejected the connection")));
            else if (Str(payload, "pairingType") == "PROMPT") { prompted = true; PromptLater(); }
            return;
        }
        if (id == "sub0") {
            object so, bo;
            var st = payload != null && payload.TryGetValue("settings", out so) ? so as Dictionary<string, object> : null;
            if (st != null && st.TryGetValue("backlight", out bo)) {
                int v;
                if (int.TryParse(bo.ToString(), out v)) {
                    firstValue.TrySetResult(true);
                    if (BacklightChanged != null) BacklightChanged(v);
                }
            }
            return;
        }
        // An alert that answered after we stopped waiting: close it, or it would stay open on the monitor and block the next ones
        if (id != null && lateAlerts.Remove(id)) {
            string late = Str(payload, "alertId");
            if (late != null) {
                Log.Write("closing an alert that answered late");
                var unused = Send(new Dictionary<string, object> { { "type", "request" }, { "id", "close-" + id }, { "uri", "ssap://system.notifications/closeAlert" },
                    { "payload", new Dictionary<string, object> { { "alertId", late } } } });
            }
            return;
        }
        TaskCompletionSource<Dictionary<string, object>> t;
        if (id != null && pend.TryGetValue(id, out t)) {
            pend.Remove(id);
            if (type == "error") {
                string why = Str(payload, "errorText");
                t.TrySetException(new Exception((Str(m, "error") ?? "error") + (why != null ? " (" + why + ")" : "")));
            }
            else t.TrySetResult(payload ?? new Dictionary<string, object>());
        }
    }

    // The "PROMPT" response also arrives with a valid key; only warn if registration takes a while.
    async void PromptLater() {
        var reg = regTcs;
        await Task.Delay(1500);
        if (reg == regTcs && !reg.Task.IsCompleted && PairingPrompt != null) PairingPrompt();
    }

    async Task Send(object o) {
        var s = ws;
        if (s == null) throw new InvalidOperationException(L.T("sin conexión", "not connected"));
        var bytes = Encoding.UTF8.GetBytes(js.Serialize(o));
        await sendLock.WaitAsync();
        try { await s.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }

    // drop: a request that never answers usually means a dead socket, so it is closed to reconnect. Not for the alert
    // trick, which newer firmware just leaves unanswered while the connection is fine.
    async Task<Dictionary<string, object>> Request(string uri, object payload, int timeoutMs = 5000, bool drop = true) {
        string id = "r" + (++nextId);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        pend[id] = tcs;
        try { await Send(new Dictionary<string, object> { { "type", "request" }, { "id", id }, { "uri", uri }, { "payload", payload } }); }
        catch { pend.Remove(id); Drop(); throw; }
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task) {
            pend.Remove(id);
            if (uri.EndsWith("/createAlert")) lateAlerts.Add(id);
            if (drop) Drop();
            throw new TimeoutException(L.T("el monitor no respondió", "the monitor did not respond"));
        }
        return await tcs.Task;
    }

    // Asks for the brightness once, for when the subscription hasn't reported it. Returns -1 if it can't be read.
    public async Task<int> GetBacklight() {
        try {
            var r = await Request("ssap://settings/getSystemSettings", new Dictionary<string, object> {
                { "category", "picture" }, { "keys", new[] { "backlight" } } }, 3000);
            object so, bo;
            var st = r.TryGetValue("settings", out so) ? so as Dictionary<string, object> : null;
            int v;
            return st != null && st.TryGetValue("backlight", out bo) && int.TryParse(bo.ToString(), out v) ? v : -1;
        } catch { return -1; }
    }

    // Quick round trip to find out whether the socket is still alive (it often isn't after the display sleeps).
    public async Task<bool> Alive() {
        try {
            await Request("ssap://com.webos.applicationManager/getForegroundAppInfo", new Dictionary<string, object>(), 2500);
            return true;
        } catch { return false; }
    }

    public class Input { public string Id, AppId, Name; public bool Connected; }

    public async Task<List<Input>> GetInputs() {
        var r = await Request("ssap://tv/getExternalInputList", new Dictionary<string, object>());
        var list = new List<Input>();
        object devs;
        var en = r.TryGetValue("devices", out devs) ? devs as System.Collections.IEnumerable : null;
        if (en == null) return list;
        foreach (var o in en) {
            var d = o as Dictionary<string, object>;
            if (d == null) continue;
            object c;
            string id = Str(d, "id") ?? "";
            list.Add(new Input {
                Id = id, AppId = Str(d, "appId") ?? "", Name = InputName(id),
                Connected = d.TryGetValue("connected", out c) && c is bool && (bool)c
            });
        }
        return list;
    }

    // "HDMI_2" -> "HDMI 2", "DisplayPort_1" -> "DisplayPort", "USB-C_1" -> "USB-C". (The monitor labels them all "PC".)
    static string InputName(string id) {
        int u = id.LastIndexOf('_');
        if (u < 0) return id;
        string b = id.Substring(0, u), n = id.Substring(u + 1);
        return b == "HDMI" || n != "1" ? b + " " + n : b;
    }

    public async Task<string> GetForegroundApp() {
        var r = await Request("ssap://com.webos.applicationManager/getForegroundAppInfo", new Dictionary<string, object>());
        return Str(r, "appId") ?? "";
    }

    public Task SwitchInput(string id) {
        return Request("ssap://tv/switchInput", new Dictionary<string, object> { { "inputId", id } });
    }

    public Task SetBacklight(int v) { return SetSetting("picture", "backlight", v); }

    // Eye Care (low blue light). It can't be read over SSAP, only written.
    public Task SetEyeCare(bool on) { return SetSetting("picture", "eyeComfortMode", on ? "on" : "off"); }

    // Pixel Refresh (OLED pixel cleaning) the next time it turns off. It can't be read either.
    public Task SetPixelRefresh(bool on) { return SetSetting("picture", "scheduledPixelCleaning", on ? "on" : "off"); }

    readonly HashSet<string> lateAlerts = new HashSet<string>(); // createAlert requests we stopped waiting for

    // First the official setSystemSettings (the monitor accepts it for brightness, in a few ms and without any alert).
    // Keys it refuses go through luna with the alert trick, which older firmware answers at once.
    async Task SetSetting(string category, string key, object value) {
        var prm = new Dictionary<string, object> {
            { "category", category }, { "settings", new Dictionary<string, object> { { key, value } } }
        };
        Exception official;
        try { await Request("ssap://settings/setSystemSettings", prm, 4000); return; }
        catch (Exception ex) { official = ex; }
        if (!Connected) throw official;
        var act = new Dictionary<string, object> { { "uri", LunaSet }, { "params", prm } };
        Dictionary<string, object> r;
        try {
            r = await Request("ssap://system.notifications/createAlert", new Dictionary<string, object> {
                { "message", " " },
                { "buttons", new object[] { new Dictionary<string, object> { { "label", "" }, { "onClick", LunaSet }, { "params", prm } } } },
                { "onclose", act }, { "onfail", act }
            }, 3000, false);
        } catch (TimeoutException) {
            Log.Write("luna alert trick unanswered for " + key + " (an alert may be stuck on the monitor: restarting it clears it)");
            throw new TimeoutException(L.T("el monitor no respondió", "the monitor did not respond"));
        }
        object aid;
        if (r.TryGetValue("alertId", out aid))
            await Request("ssap://system.notifications/closeAlert", new Dictionary<string, object> { { "alertId", aid } });
    }

    // Finds a webOS device on the network via SSDP; returns its IP or null.
    public static Task<string> Discover() {
        return Task.Run(() => {
            byte[] msg = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                "ST: urn:lge-com:service:webos-second-screen:1\r\n\r\n");
            var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            var clients = new List<UdpClient>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses) {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    try {
                        var u = new UdpClient(new IPEndPoint(ua.Address, 0));
                        u.Send(msg, msg.Length, group);
                        clients.Add(u);
                    } catch { }
                }
            }
            try {
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until) {
                    foreach (var u in clients) {
                        if (u.Available <= 0) continue;
                        IPEndPoint from = null;
                        string resp = Encoding.ASCII.GetString(u.Receive(ref from));
                        if (resp.IndexOf("webos", StringComparison.OrdinalIgnoreCase) >= 0) return from.Address.ToString();
                    }
                    Thread.Sleep(50);
                }
            } finally { foreach (var u in clients) u.Close(); }
            return (string)null;
        });
    }
}

// Public manifest of the "LG Remote App" that webOS devices accept.
static class Manifest {
    public static Dictionary<string, object> Get() {
        return new Dictionary<string, object> {
            { "appVersion", "1.1" },
            { "manifestVersion", 1 },
            { "deviceName", "LGBrillo (PC)" },
            { "permissions", new[] {
                "LAUNCH", "LAUNCH_WEBAPP", "APP_TO_APP", "CLOSE", "TEST_OPEN", "TEST_PROTECTED", "CONTROL_AUDIO",
                "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK", "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_MEDIA_PLAYBACK",
                "CONTROL_INPUT_TV", "CONTROL_POWER", "CONTROL_TV_SCREEN", "READ_APP_STATUS", "READ_CURRENT_CHANNEL",
                "READ_INPUT_DEVICE_LIST", "READ_NETWORK_STATE", "READ_RUNNING_APPS", "READ_TV_CHANNEL_LIST",
                "WRITE_NOTIFICATION_TOAST", "READ_POWER_STATE", "READ_COUNTRY_INFO", "CONTROL_INPUT_TEXT",
                "CONTROL_MOUSE_AND_KEYBOARD", "READ_INSTALLED_APPS", "READ_SETTINGS", "READ_STORAGE_DEVICE_LIST" } },
            { "signatures", new object[] { new Dictionary<string, object> {
                { "signature",
                    "eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbmctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR" +
                    "+59aFNwYDyjQgKk3auukd7pcegmE2CzPCa0bJ0ZsRAcKkCTJrWo5iDzNhMBWRyaMOv5zWSrthlf7G128qvIlpMT0YNY+n/FaOHE73uLrS/g7swl3/qH/BGFG2Hu4" +
                    "RlL48eb3lLKqTt2xKHdCs6Cd4RMfJPYnzgvI4BNrFUKsjkcu+WD4OO2A27Pq1n50cMchmcaXadJhGrOqH5YmHdOCj5NSHzJYrsW0HPlpuAx/ECMeIZYDh6RMqaFM" +
                    "2DXzdKX9NmmyqzJ3o/0lkk/N97gfVRLW5hA29yeAwaCViZNCP8iC9aO0q9fQojoa7NQnAtw==" },
                { "signatureVersion", 1 } } } },
            { "signed", new Dictionary<string, object> {
                { "appId", "com.lge.test" },
                { "created", "20140509" },
                { "localizedAppNames", new Dictionary<string, object> {
                    { "", "LG Remote App" }, { "ko-KR", "리모컨 앱" }, { "zxx-XX", "ЛГ Rэмotэ AПП" } } },
                { "localizedVendorNames", new Dictionary<string, object> { { "", "LG Electronics" } } },
                { "permissions", new[] {
                    "TEST_SECURE", "CONTROL_INPUT_TEXT", "CONTROL_MOUSE_AND_KEYBOARD", "READ_INSTALLED_APPS", "READ_LGE_SDX",
                    "READ_NOTIFICATIONS", "SEARCH", "WRITE_SETTINGS", "WRITE_NOTIFICATION_ALERT", "CONTROL_POWER",
                    "READ_CURRENT_CHANNEL", "READ_RUNNING_APPS", "READ_UPDATE_INFO", "UPDATE_FROM_REMOTE_APP",
                    "READ_LGE_TV_INPUT_EVENTS", "READ_TV_CURRENT_TIME" } },
                { "serial", "2f930e2d2cfe083771f68e4fe7bb07" },
                { "vendorId", "com.lge" } } }
        };
    }
}

// Spanish text when Windows is in Spanish; English otherwise.
static class L {
    public static bool Es = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    public static string T(string es, string en) { return Es ? es : en; }
}

static class Ui {
    public static float S = 1;
    public static readonly Color Amber = Color.FromArgb(255, 184, 32);
    public static readonly Color PanelTint = Color.FromArgb(226, 30, 30, 32);
    public static readonly Color Text = Color.FromArgb(245, 245, 247);
    public static readonly Color Text2 = Color.FromArgb(152, 152, 158);
    public static readonly Color Line = Color.FromArgb(26, 255, 255, 255);
    public static Color Accent = Color.FromArgb(10, 132, 255);

    public static int P(float v) { return (int)Math.Round(v * S); }

    public static Color ParseColor(string hex) {
        try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(10, 132, 255); }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    // Windows 11 rounded corners (square on Windows 10).
    public static void Round(Form f) {
        try { int pref = 2; DwmSetWindowAttribute(f.Handle, 33, ref pref, 4); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);

    // Native Windows 11 backdrop (2 = Mica, 3 = Acrylic). Returns false if Windows doesn't support it.
    public static bool Backdrop(Form f, int type, bool extend) {
        try {
            int on = 1;
            DwmSetWindowAttribute(f.Handle, 20, ref on, 4); // dark mode
            if (extend) { var m = new Margins { L = -1, R = -1, T = -1, B = -1 }; DwmExtendFrameIntoClientArea(f.Handle, ref m); }
            return Environment.OSVersion.Version.Build >= 22621 && DwmSetWindowAttribute(f.Handle, 38, ref type, 4) == 0;
        } catch { return false; }
    }

    static readonly System.Collections.Generic.HashSet<string> families = new System.Collections.Generic.HashSet<string>();

    // Uses Segoe UI Variable (the Windows 11 font) when available; Segoe UI otherwise.
    public static Font F(string win11, string fallback, float size, FontStyle st) {
        if (families.Count == 0)
            using (var fc = new InstalledFontCollection())
                foreach (var fam in fc.Families) families.Add(fam.Name);
        return new Font(families.Contains(win11) ? win11 : fallback, size, st);
    }

    public static void DarkTitle(Form f) {
        try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { }
    }

    static readonly Dictionary<long, Bitmap> suns = new Dictionary<long, Bitmap>();

    // Shared bitmap for painting (don't dispose it): the bar repaints on every animation frame.
    public static Bitmap SunCached(int n, Color color) {
        long key = ((long)n << 32) | (uint)color.ToArgb();
        Bitmap b;
        if (!suns.TryGetValue(key, out b)) suns[key] = b = Sun(n, color);
        return b;
    }

    public static Bitmap Sun(int n, Color color) {
        var b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float c = n / 2f, r = n * 0.2f;
            using (var br = new SolidBrush(color)) g.FillEllipse(br, c - r, c - r, 2 * r, 2 * r);
            using (var p = new Pen(color, Math.Max(1.5f, n * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                float r1 = n * 0.31f, r2 = n * 0.44f;
                for (int i = 0; i < 8; i++) {
                    double a = i * Math.PI / 4;
                    float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                    g.DrawLine(p, c + cs * r1, c + sn * r1, c + cs * r2, c + sn * r2);
                }
            }
        }
        return b;
    }

    // Writes a .ico with PNG frames for the .exe icon.
    public static void MakeIcon(string path) {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var pngs = new List<byte[]>();
        foreach (int n in sizes)
            using (var b = Sun(n, Amber))
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var w = new BinaryWriter(File.Create(path))) {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int off = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) {
                byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(off);
                off += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    // Dark frosted glass: a picture of what's behind the window, blurred and darkened.
    public static Bitmap Frost(Rectangle r) {
        var outB = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var shot = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb)) {
            try { using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(r.Location, Point.Empty, r.Size); } catch { }
            int sw = Math.Max(1, r.Width / 14), sh = Math.Max(1, r.Height / 14);
            using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            using (var ia = new ImageAttributes()) {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                using (var g = Graphics.FromImage(small)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, new Rectangle(0, 0, sw, sh), 0, 0, r.Width, r.Height, GraphicsUnit.Pixel, ia);
                }
                using (var g = Graphics.FromImage(outB)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
                    using (var b = new SolidBrush(PanelTint)) g.FillRectangle(b, 0, 0, r.Width, r.Height);
                }
            }
        }
        return outB;
    }

    public static GraphicsPath Capsule(RectangleF r) {
        var p = new GraphicsPath();
        float d = Math.Min(r.Width, r.Height);
        if (r.Width <= r.Height) { p.AddEllipse(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath RoundRect(RectangleF r, float rad) {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Brightness row: small sun, thin bar with a colored fill, white knob and big sun.
    const float KnobW = 26, KnobH = 17;

    public static RectangleF TrackOf(RectangleF row) {
        float s1 = P(13), s2 = P(18);
        return new RectangleF(row.X + s1 + P(12), row.Y + row.Height / 2 - P(3), row.Width - s1 - s2 - P(24), P(6));
    }

    public static int ValueAt(RectangleF row, float x) {
        var t = TrackOf(row);
        float kw = P(KnobW);
        return (int)Math.Round((x - t.X - kw / 2) * 100 / Math.Max(1, t.Width - kw));
    }

    public static void SliderRow(Graphics g, RectangleF row, int val, float grow) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int s1 = P(13), s2 = P(18);
        g.DrawImage(SunCached(s1, Color.FromArgb(150, 150, 156)), row.X, row.Y + (row.Height - s1) / 2f);
        g.DrawImage(SunCached(s2, Color.FromArgb(205, 205, 210)), row.Right - s2, row.Y + (row.Height - s2) / 2f);
        var t = TrackOf(row);
        using (var path = Capsule(t))
        using (var b = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
            g.FillPath(b, path);
        if (val < 0) return;
        float kw = P(KnobW), kh = P(KnobH) * (1 + 0.12f * grow);
        float kx = t.X + kw / 2 + (t.Width - kw) * val / 100f;
        using (var path = Capsule(new RectangleF(t.X, t.Y, kx - t.X, t.Height)))
        using (var b = new SolidBrush(Accent))
            g.FillPath(b, path);
        var k = new RectangleF(kx - kw / 2, row.Y + (row.Height - kh) / 2, kw, kh);
        for (int i = 3; i >= 1; i--) {
            var sh = k;
            sh.Inflate(i * S * 0.8f, i * S * 0.6f);
            sh.Offset(0, S);
            using (var path = Capsule(sh)) using (var b = new SolidBrush(Color.FromArgb(22, 0, 0, 0))) g.FillPath(b, path);
        }
        using (var path = Capsule(k)) using (var b = new SolidBrush(Color.FromArgb(242, 242, 245))) g.FillPath(b, path);
    }

    // Icons for the round buttons and the rows.
    public static void IconEye(Graphics g, PointF c, float s, Color col) {
        var r = new RectangleF(c.X - s / 2, c.Y - s * 0.3f, s, s * 0.6f);
        float cy = c.Y;
        using (var p = new Pen(col, Math.Max(1.4f, s * 0.09f)) { LineJoin = LineJoin.Round }) {
            g.DrawBezier(p, r.X, cy, r.X + r.Width * 0.3f, r.Y - r.Height * 0.15f, r.Right - r.Width * 0.3f, r.Y - r.Height * 0.15f, r.Right, cy);
            g.DrawBezier(p, r.X, cy, r.X + r.Width * 0.3f, r.Bottom + r.Height * 0.15f, r.Right - r.Width * 0.3f, r.Bottom + r.Height * 0.15f, r.Right, cy);
        }
        float pr = r.Height * 0.27f;
        using (var b = new SolidBrush(col)) g.FillEllipse(b, c.X - pr, cy - pr, 2 * pr, 2 * pr);
    }

    public static void IconPixels(Graphics g, PointF c, float s, Color col) {
        float q = s * 0.2f, step = s * 0.34f;
        using (var b = new SolidBrush(col))
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++) {
                    float a = (x + y) % 2 == 0 ? 1f : 0.55f;
                    using (var bb = new SolidBrush(Color.FromArgb((int)(col.A * a), col)))
                    using (var path = RoundRect(new RectangleF(c.X + x * step - q / 2, c.Y + y * step - q / 2, q, q), q * 0.25f))
                        g.FillPath(bb, path);
                }
    }

    // A screen with an incoming arrow: for the video input button.
    public static void IconInput(Graphics g, PointF c, float s, Color col) {
        float w = s, h = s * 0.68f, lw = Math.Max(1.4f, s * 0.09f);
        var r = new RectangleF(c.X - w / 2, c.Y - h / 2 - s * 0.06f, w, h);
        using (var p = new Pen(col, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
            using (var path = RoundRect(r, s * 0.1f)) g.DrawPath(p, path);
            g.DrawLine(p, c.X - s * 0.18f, r.Bottom + s * 0.16f, c.X + s * 0.18f, r.Bottom + s * 0.16f);
            float ay = r.Y + h / 2;
            g.DrawLine(p, r.X + w * 0.24f, ay, r.Right - w * 0.3f, ay);
            g.DrawLines(p, new[] { new PointF(r.Right - w * 0.44f, ay - h * 0.2f), new PointF(r.Right - w * 0.3f, ay), new PointF(r.Right - w * 0.44f, ay + h * 0.2f) });
        }
    }

    public static void IconGear(Graphics g, PointF c, float s, Color col) {
        float r = s * 0.3f;
        using (var p = new Pen(col, s * 0.15f)) g.DrawEllipse(p, c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var p = new Pen(col, s * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (int i = 0; i < 8; i++) {
                double a = i * Math.PI / 4;
                float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                g.DrawLine(p, c.X + cs * r * 1.25f, c.Y + sn * r * 1.25f, c.X + cs * r * 1.55f, c.Y + sn * r * 1.55f);
            }
    }

    public static void Chevron(Graphics g, PointF c, float s, Color col) {
        using (var p = new Pen(col, Math.Max(1.4f, 1.6f * S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(p, new[] { new PointF(c.X - s * 0.25f, c.Y - s * 0.5f), new PointF(c.X + s * 0.25f, c.Y), new PointF(c.X - s * 0.25f, c.Y + s * 0.5f) });
    }

    // Smooth text (GDI+ antialiasing), vertically centered, with "…" when it doesn't fit.
    public static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
        using (var sf = new StringFormat(StringFormat.GenericTypographic) {
            Alignment = h, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var b = new SolidBrush(c))
            g.DrawString(s, f, b, r, sf);
    }

    public static void ChevronDown(Graphics g, PointF c, float s, Color col) {
        using (var p = new Pen(col, Math.Max(1.4f, 1.6f * S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(p, new[] { new PointF(c.X - s * 0.5f, c.Y - s * 0.22f), new PointF(c.X, c.Y + s * 0.25f), new PointF(c.X + s * 0.5f, c.Y - s * 0.22f) });
    }

    // Sample wallpaper for previews and screenshots: deep blue to purple with soft colored blobs.
    public static Bitmap SampleWallpaper(int w, int h) {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) {
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(18, 32, 84), Color.FromArgb(92, 38, 120), 35f)) g.FillRectangle(lg, 0, 0, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var blobs = new[] { new { X = 0.18f, Y = 0.30f, R = 0.9f, C = Color.FromArgb(170, 40, 140, 255) },
                                new { X = 0.80f, Y = 0.20f, R = 0.8f, C = Color.FromArgb(160, 255, 90, 160) },
                                new { X = 0.55f, Y = 0.95f, R = 0.9f, C = Color.FromArgb(140, 255, 170, 60) } };
            foreach (var o in blobs) {
                float r = h * o.R, cx = w * o.X, cy = h * o.Y;
                using (var p = new GraphicsPath()) {
                    p.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                    using (var pg = new PathGradientBrush(p) { CenterColor = o.C, SurroundColors = new[] { Color.FromArgb(0, o.C) } }) g.FillPath(pg, p);
                }
            }
        }
        return b;
    }

    // White page with dark text: the hardest background for glass, where blur, refraction and darkening are easy to judge.
    public static Bitmap SampleText(int w, int h) {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        using (var big = F("Segoe UI Variable Display Semib", "Segoe UI Semibold", 20f, FontStyle.Regular))
        using (var body = F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular))
        using (var bold = F("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 10f, FontStyle.Regular)) {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string text = L.T(
                "El vidrio líquido dobla la luz en el borde y desenfoca lo que hay detrás. Sobre una página blanca con texto se ven bien el desenfoque, la refracción del bisel y el oscurecido: ajustalos hasta que la barra se lea cómoda también en los casos extremos. ",
                "Liquid glass bends the light at its edge and blurs whatever is behind it. On a white page full of text the blur, the bezel refraction and the darkening are easy to see: tune them until the bar is comfortable to read in the extreme cases too. ");
            float y = P(10);
            using (var dark = new SolidBrush(Color.FromArgb(20, 20, 24)))
            using (var grey = new SolidBrush(Color.FromArgb(70, 70, 78)))
            using (var blue = new SolidBrush(Color.FromArgb(10, 100, 220))) {
                g.DrawString("Liquid Glass", big, dark, P(14), y);
                g.DrawString(L.T("Vista previa sobre texto", "Preview over text"), bold, blue, P(14) + g.MeasureString("Liquid Glass  ", big).Width, y + P(10));
                y += P(36);
                var rect = new RectangleF(P(14), y, w - P(28), h - y);
                g.DrawString(text + text + text + text, body, grey, rect);
            }
        }
        return b;
    }

    public static float Toward(float cur, float goal, float k) {
        float n = cur + (goal - cur) * k;
        return Math.Abs(goal - n) < 0.002f ? goal : n;
    }
}

// Liquid Glass look, ported from Liquid Launch (Glass.kt / GlassControls.kt). Settings shared by the tray panel,
// the on-screen bar and the preview in the glass settings window.
static class GlassCfg {
    public static bool On;
    public static float Blur = 0.45f, Darken = 0.3f, Bezel = 0.45f;
    public static float Rgb = 0.35f, RgbHue = 0f, RgbSpeed = 0.4f;
    public static bool Refract = true, RgbOn = true, RgbAnim = false, Light = true;
    public static int WaveMs = 850;
    public static int Rev; // bumps when something baked into the lens (blur, bezel, refraction) changes

    public static void Reset() {
        Blur = 0.45f; Darken = 0.3f; Bezel = 0.45f;
        Rgb = 0.35f; RgbHue = 0f; RgbSpeed = 0.4f;
        Refract = true; RgbOn = true; RgbAnim = false; Light = true; WaveMs = 850;
        Rev++;
    }

    public static void From(Config c) {
        On = c.Design == "glass";
        Blur = (float)c.GBlur; Darken = (float)c.GDarken; Bezel = (float)c.GBezel;
        Rgb = (float)c.GRgb; RgbHue = (float)c.GRgbHue; RgbSpeed = (float)c.GRgbSpeed;
        Refract = c.GRefract; RgbOn = c.GRgbOn; RgbAnim = c.GRgbAnim; Light = c.GLight;
        WaveMs = c.GWave == "lenta" ? 1300 : c.GWave == "rapida" ? 480 : 850;
        Rev++;
    }

    public static void Into(Config c) {
        c.GBlur = Blur; c.GDarken = Darken; c.GBezel = Bezel;
        c.GRgb = Rgb; c.GRgbHue = RgbHue; c.GRgbSpeed = RgbSpeed;
        c.GRefract = Refract; c.GRgbOn = RgbOn; c.GRgbAnim = RgbAnim; c.GLight = Light;
        c.GWave = WaveMs >= 1300 ? "lenta" : WaveMs <= 480 ? "rapida" : "normal";
    }

    public static float BezelPx { get { return (4f + 16f * Bezel) * Ui.S; } }
    public static float RgbAmount { get { return RgbOn ? Rgb : 0f; } }
    public static bool Spinning { get { return RgbOn && RgbAnim; } }

    // Slow turn of the RGB edge: speed 0 = one turn every 16 s, 1 = every 2 s.
    public static float Spin {
        get {
            if (!Spinning) return 0f;
            int period = (int)(16000 - 14000 * Math.Max(0f, Math.Min(1f, RgbSpeed)));
            return (Environment.TickCount & int.MaxValue) % period * 360f / period;
        }
    }
}

static class Glass {
    static readonly int[] Rainbow = {
        unchecked((int)0xFFFF3B6B), unchecked((int)0xFFFFB13B), unchecked((int)0xFFE8FF3B), unchecked((int)0xFF3BFF8A),
        unchecked((int)0xFF3BE6FF), unchecked((int)0xFF5B6BFF), unchecked((int)0xFFC23BFF), unchecked((int)0xFFFF3B6B) };

    public static Color RainbowAt(float t) {
        t -= (float)Math.Floor(t);
        float f = t * (Rainbow.Length - 1);
        int i = Math.Min(Rainbow.Length - 2, (int)f);
        float k = f - i;
        Color a = Color.FromArgb(Rainbow[i]), b = Color.FromArgb(Rainbow[i + 1]);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    public static Color[] RainbowColors() {
        var c = new Color[Rainbow.Length];
        for (int i = 0; i < c.Length; i++) c[i] = Color.FromArgb(Rainbow[i]);
        return c;
    }

    // What is on screen behind r. Layered windows (our own glass) aren't captured, so this also works while it is visible.
    public static Bitmap Capture(Rectangle r) {
        var b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        try { using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size); } catch { }
        return b;
    }

    // Blurs by shrinking and enlarging again (blur 0..1, like the "Blur" slider of Liquid Launch).
    public static Bitmap Blurred(Bitmap shot, float blur) {
        int w = shot.Width, h = shot.Height;
        float f = 3f + 22f * Math.Max(0f, Math.Min(1f, blur));
        int sw = Math.Max(1, (int)(w / f)), sh = Math.Max(1, (int)(h / f));
        var outB = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
        using (var ia = new ImageAttributes()) {
            ia.SetWrapMode(WrapMode.TileFlipXY);
            using (var g = Graphics.FromImage(small)) {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(shot, new Rectangle(0, 0, sw, sh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            using (var g = Graphics.FromImage(outB)) {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
            }
        }
        return outB;
    }

    public static int[] Pixels(Bitmap b) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[b.Width * b.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        return px;
    }

    // Premultiplied ARGB is GDI+'s native format: it draws several times faster than plain ARGB.
    public static Bitmap ToBitmap(int[] px, int w, int h) {
        var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        Write(b, px);
        return b;
    }

    // px must already be premultiplied when b is premultiplied (opaque pixels are the same either way).
    public static void Write(Bitmap b, int[] px) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, b.PixelFormat);
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        b.UnlockBits(d);
    }

    // Signed distance to a rounded rectangle (negative inside).
    public static float Sdf(float px, float py, float cx, float cy, float hx, float hy, float r) {
        float qx = Math.Abs(px - cx) - hx + r, qy = Math.Abs(py - cy) - hy + r;
        float mx = Math.Max(qx, 0f), my = Math.Max(qy, 0f);
        return (float)Math.Sqrt(mx * mx + my * my) + Math.Min(Math.Max(qx, qy), 0f) - r;
    }

    static int Channel(int[] p, int w, int h, float x, float y, int shift) {
        x -= 0.5f; y -= 0.5f;
        if (x < 0) x = 0; if (y < 0) y = 0;
        if (x > w - 1) x = w - 1; if (y > h - 1) y = h - 1;
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        float fx = x - x0, fy = y - y0;
        int a = (p[y0 * w + x0] >> shift) & 255, b = (p[y0 * w + x1] >> shift) & 255;
        int c = (p[y1 * w + x0] >> shift) & 255, d = (p[y1 * w + x1] >> shift) & 255;
        float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        return (int)(top + (bottom - top) * fy + 0.5f);
    }

    // Edge refraction (the REFRACTION shader): near the edge the backdrop is sampled further inside, like thick glass
    // bending the light, with red, green and blue at slightly different depths.
    public static void Refract(int[] src, int[] dst, int w, int h, RectangleF rect, float radius, float bezel) {
        float hx = rect.Width / 2f, hy = rect.Height / 2f, cx = rect.X + hx, cy = rect.Y + hy;
        float r = Math.Min(radius, Math.Min(hx, hy));
        bezel = Math.Min(bezel, Math.Min(hx, hy) * 0.35f); // the center of the glass stays clean
        if (bezel < 0.5f) return;
        float strength = bezel * 1.3f, chroma = 0.12f;
        int xa = Math.Max(0, (int)rect.Left), xb = Math.Min(w, (int)Math.Ceiling(rect.Right));
        int ya = Math.Max(0, (int)rect.Top), yb = Math.Min(h, (int)Math.Ceiling(rect.Bottom));
        for (int y = ya; y < yb; y++) {
            float py = y + 0.5f;
            for (int x = xa; x < xb; x++) {
                float px = x + 0.5f;
                float d = Sdf(px, py, cx, cy, hx, hy, r);
                if (d > 0f) continue;
                float edge = 1f + d / bezel;
                if (edge <= 0f) continue;
                float k = edge * edge;
                float gx = Sdf(px + 1, py, cx, cy, hx, hy, r) - Sdf(px - 1, py, cx, cy, hx, hy, r);
                float gy = Sdf(px, py + 1, cx, cy, hx, hy, r) - Sdf(px, py - 1, cx, cy, hx, hy, r);
                float len = (float)Math.Sqrt(gx * gx + gy * gy);
                if (len < 0.0001f) continue;
                float ox = -gx / len * strength * k, oy = -gy / len * strength * k;
                int rr = Channel(src, w, h, px + ox * (1 + chroma), py + oy * (1 + chroma), 16);
                int gg = Channel(src, w, h, px + ox, py + oy, 8);
                int bb = Channel(src, w, h, px + ox * (1 - chroma), py + oy * (1 - chroma), 0);
                dst[y * w + x] = unchecked((int)0xFF000000) | (rr << 16) | (gg << 8) | bb;
            }
        }
    }

    // Vertical gradient that doesn't repeat its first row at the bottom (GDI+ tiles linear gradients).
    public static LinearGradientBrush Vertical(RectangleF r, Color top, Color bottom) {
        return new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, Math.Max(1, r.Width), r.Height + 2), top, bottom, 90f);
    }

    // Radial gradient; stops go from the center (0) to the edge (1), like Android's RadialGradient.
    public static PathGradientBrush Radial(PointF c, float radius, Color[] colors, float[] stops, GraphicsPath holder) {
        holder.Reset();
        holder.AddEllipse(c.X - radius, c.Y - radius, 2 * radius, 2 * radius);
        var b = new PathGradientBrush(holder) { CenterPoint = c };
        int n = colors.Length;
        var blend = new ColorBlend(n);
        for (int i = 0; i < n; i++) {
            blend.Colors[i] = colors[n - 1 - i];
            blend.Positions[i] = 1f - stops[n - 1 - i];
        }
        blend.Positions[0] = 0f;
        blend.Positions[n - 1] = 1f;
        b.InterpolationColors = blend;
        return b;
    }

    public static Color White(float a) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(a * 255))), 255, 255, 255); }

    // iOS 26 level bar (GlassSlider): a thick glass rail that fills with white up to the value, with a small sun and
    // the percentage inside (both turn dark when the white passes under them). It glows while it's being dragged.
    public static void Slider(Graphics g, RectangleF row, int val, float grow, Font font) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = row.Height, w = row.Width;
        if (grow > 0f)
            for (int i = 1; i <= 3; i++) {
                float o = i * Ui.S;
                var rr = RectangleF.Inflate(row, o, o);
                using (var path = Ui.Capsule(rr))
                using (var p = new Pen(White(0.25f / i * grow), i * 1.6f * Ui.S)) g.DrawPath(p, path);
            }
        using (var path = Ui.Capsule(row)) {
            using (var b = Vertical(row, White(0.18f + 0.14f * grow), White(0.08f + 0.10f * grow))) g.FillPath(b, path);
            float shown = Math.Max(0, Math.Min(100, val)) / 100f;
            if (val > 0) {
                float right = Math.Max(row.X + w * shown, row.X + h * Math.Min(1f, shown * 8f));
                var st = g.Save();
                g.SetClip(new RectangleF(row.X, row.Y, right - row.X, h), CombineMode.Intersect);
                using (var b = Vertical(row, Color.FromArgb(0xF2, 255, 255, 255), Color.FromArgb(0xD9, 0xDD, 0xE1, 0xE6))) g.FillPath(b, path);
                g.Restore(st);
            }
            using (var p = new Pen(White(0.25f + 0.6f * grow), (1f + grow) * Ui.S)) g.DrawPath(p, path);
            int s = (int)(h * 0.5f);
            bool sunCovered = val >= 0 && w * shown > h * 0.8f;
            g.DrawImage(Ui.SunCached(s, sunCovered ? Color.FromArgb(200, 28, 28, 30) : Color.FromArgb(220, 255, 255, 255)), row.X + h * 0.5f - s / 2f, row.Y + (h - s) / 2f);
            if (val >= 0) {
                bool dark = shown > 0.86f;
                Ui.DrawText(g, val + "%", font, new RectangleF(row.X, row.Y, w - h * 0.55f, h),
                    dark ? Color.FromArgb(28, 28, 30) : White(0.9f), StringAlignment.Far);
            }
        }
    }
}

// Light that follows the pointer and a wave of light on release (iOS 26/27 style), with spring-like easing.
class GlassLight {
    public PointF Pos, Target, WavePos;
    public float Level, Goal, Wave = 1f;
    int waveStart, last;

    public void Move(Point p) { Target = p; if (Level <= 0.01f) Pos = p; }

    public void FireWave(Point p) { WavePos = p; waveStart = Environment.TickCount; Wave = 0f; }

    public bool Active { get { return Level > 0.001f || Wave < 0.999f; } }

    // Advances the animation; returns false when nothing is moving any more.
    public bool Step() {
        int now = Environment.TickCount;
        float dt = last == 0 ? 15 : Math.Min(50, Math.Max(1, now - last));
        last = now;
        float k = 1f - (float)Math.Pow(0.68, dt / 15f);
        Pos = new PointF(Pos.X + (Target.X - Pos.X) * k, Pos.Y + (Target.Y - Pos.Y) * k);
        if (Level < Goal) Level = Math.Min(Goal, Level + dt / 140f);
        else if (Level > Goal) Level = Math.Max(Goal, Level - dt / 320f);
        if (Wave < 1f) {
            float t = (now - waveStart) / (float)Math.Max(100, GlassCfg.WaveMs);
            Wave = t >= 1f ? 1f : 1f - (float)Math.Pow(1 - t, 2.2);
        }
        float dx = Target.X - Pos.X, dy = Target.Y - Pos.Y;
        return Level != Goal || Wave < 1f || dx * dx + dy * dy > 0.25f;
    }
}

// Ticks once per frame of the monitor: a background thread waits for each vertical blank of the display (60, 144,
// 240 Hz...) and asks the UI thread for a frame. WinForms timers stop at about 64 Hz.
class FrameClock {
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    // The graphics card's vertical blank of the primary display: one per refresh, whatever is happening on screen
    // (DwmFlush only returns when Windows composes something, which can be less often than the monitor refreshes).
    [StructLayout(LayoutKind.Sequential)] struct OpenAdapter { public IntPtr Dc; public uint Adapter; public uint LuidLow; public int LuidHigh; public uint Source; }
    [StructLayout(LayoutKind.Sequential)] struct WaitVBlank { public uint Adapter, Device, Source; }
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenAdapter p);
    [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitVBlank p);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    static WaitVBlank vblank;
    static bool vblankReady, vblankTried;

    static bool OpenVBlank() {
        vblankTried = true;
        try {
            IntPtr dc = CreateDC(null, Screen.PrimaryScreen.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            var oa = new OpenAdapter { Dc = dc };
            int r = D3DKMTOpenAdapterFromHdc(ref oa);
            DeleteDC(dc);
            if (r != 0) return false;
            vblank = new WaitVBlank { Adapter = oa.Adapter, Device = 0, Source = oa.Source };
            return true;
        } catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    // The shortcut bar animates while another app has the focus: Windows 11 slows down background processes
    // (EcoQoS), which cut the wave to about 90 fps. Opt out of that throttling.
    static void NoThrottling() {
        try {
            var s = new PowerThrottling { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 }; // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, off
            SetProcessInformation(GetCurrentProcess(), 4, ref s, Marshal.SizeOf(typeof(PowerThrottling))); // ProcessPowerThrottling
        } catch { }
    }

    static void WaitFrame() {
        if (!vblankTried) vblankReady = OpenVBlank();
        if (vblankReady && D3DKMTWaitForVerticalBlankEvent(ref vblank) == 0) return;
        vblankReady = false;
        if (DwmFlush() != 0) Thread.Sleep(4);
    }
    static readonly List<FrameClock> running = new List<FrameClock>();
    static Thread thread;
    static SynchronizationContext ui;
    static int pending;
    public event EventHandler Tick;
    public bool Enabled { get; private set; }

    public void Start() {
        if (Enabled) return;
        // Started on the UI thread: post the frames back to it (creating the WinForms context if it isn't there yet)
        if (ui == null) ui = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
        Enabled = true;
        lock (running) {
            running.Add(this);
            if (thread == null) {
                NoThrottling();
                thread = new Thread(Loop) { IsBackground = true, Name = "frames", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
            Monitor.PulseAll(running);
        }
    }

    public void Stop() {
        if (!Enabled) return;
        Enabled = false;
        lock (running) running.Remove(this);
    }

    static void Loop() {
        while (true) {
            lock (running) while (running.Count == 0) Monitor.Wait(running);
            WaitFrame();
            if (Interlocked.Exchange(ref pending, 1) == 1) continue; // the last frame isn't drawn yet: skip this one
            ui.Post(delegate { Interlocked.Exchange(ref pending, 0); FireAll(); }, null);
        }
    }

    static void FireAll() {
        FrameClock[] list;
        lock (running) list = running.ToArray();
        foreach (var c in list) if (c.Enabled && c.Tick != null) c.Tick(c, EventArgs.Empty);
    }
}

// The RGB edge: a rainbow along the rim, strong at two opposite corners and invisible halfway, with a soft inner glow.
// Precomputed per pixel once per size; only the colors are recomputed when the hue turns.
class RgbLayer {
    public readonly Size Size;
    readonly int[] idx;
    readonly float[] ang, fade, glow, stroke;
    readonly int[] px;
    readonly Bitmap bmp;
    int lastHue = -1;
    float lastAmount = -1f;

    static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

    static float FadeAt(float t) {
        float[] s = { 0f, 0.22f, 0.38f, 0.62f, 0.78f, 1f }, a = { 1f, 0.2f, 0f, 0f, 0.2f, 1f };
        if (t <= 0) return 1f;
        if (t >= 1) return 1f;
        for (int i = 1; i < s.Length; i++)
            if (t <= s[i]) return a[i - 1] + (a[i] - a[i - 1]) * (t - s[i - 1]) / (s[i] - s[i - 1]);
        return 1f;
    }

    public RgbLayer(int w, int h, RectangleF rect, float radius, float strokeW, float glowR) {
        Size = new Size(w, h);
        px = new int[w * h];
        bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        float hx = rect.Width / 2f, hy = rect.Height / 2f, cx = rect.X + hx, cy = rect.Y + hy;
        float r = Math.Min(radius, Math.Min(hx, hy));
        var li = new List<int>(); var la = new List<float>(); var lf = new List<float>(); var lg = new List<float>(); var ls = new List<float>();
        float diag = (float)w * w + (float)h * h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                float fx = x + 0.5f, fy = y + 0.5f;
                float d = Glass.Sdf(fx, fy, cx, cy, hx, hy, r);
                if (d > 1f || d < -glowR - 1f) continue;
                float cov = Clamp01(0.5f - d);
                float s = cov * Clamp01(d + strokeW + 0.5f);
                float depth = Math.Max(0f, -d);
                float gl = cov * (float)Math.Pow(Clamp01(1f - depth / glowR), 2);
                if (s <= 0f && gl <= 0f) continue;
                float f = FadeAt((fx * w + fy * h) / diag);
                if (f <= 0.002f) continue;
                double a = Math.Atan2(fy - h / 2f, fx - w / 2f) * 180 / Math.PI;
                if (a < 0) a += 360;
                li.Add(y * w + x); la.Add((float)a); lf.Add(f); lg.Add(gl); ls.Add(s);
            }
        idx = li.ToArray(); ang = la.ToArray(); fade = lf.ToArray(); glow = lg.ToArray(); stroke = ls.ToArray();
    }

    static int[] lut;

    public Bitmap Get(float hueDeg, float amount) {
        int hue = (int)Math.Round(hueDeg) % 360;
        if (hue < 0) hue += 360;
        if (hue == lastHue && amount == lastAmount) return bmp; // the turn moves in whole degrees
        lastHue = hue; lastAmount = amount;
        if (lut == null) {
            lut = new int[360];
            for (int i = 0; i < 360; i++) lut[i] = Glass.RainbowAt(i / 360f).ToArgb() & 0xFFFFFF;
        }
        for (int i = 0; i < idx.Length; i++) {
            float a = 1f - (1f - amount * 0.5f * glow[i]) * (1f - amount * stroke[i]);
            int al = (int)(Math.Min(1f, a * fade[i]) * 255);
            int deg = ((int)ang[i] - hue) % 360;
            if (deg < 0) deg += 360;
            int c = lut[deg];
            px[idx[i]] = (al << 24) | ((((c >> 16) & 255) * al / 255) << 16) | ((((c >> 8) & 255) * al / 255) << 8) | ((c & 255) * al / 255);
        }
        Glass.Write(bmp, px);
        return bmp;
    }
}

// One glass surface (the GlassPainter of Liquid Launch): the backdrop as seen through the glass, the RGB edge and the light.
class GlassSurface {
    public Bitmap Lens, Drops;
    public readonly GlassLight Light = new GlassLight();
    RgbLayer rgb;
    int builtRev = -1;
    Size builtSize;
    readonly GraphicsPath holder = new GraphicsPath();

    public bool NeedsBuild(Size s) { return Lens == null || s != builtSize || builtRev != GlassCfg.Rev; }

    public void Reset() { builtRev = -1; rgb = null; }

    public void Clear() {
        if (Lens != null) Lens.Dispose();
        if (Drops != null) Drops.Dispose();
        Lens = null; Drops = null; rgb = null;
    }

    // shot: what's behind the whole window. plate: the glass shape. drops: small round glass buttons inside it.
    public void Build(Bitmap shot, RectangleF plate, float radius, RectangleF[] drops) {
        var keepRgb = rgb != null && rgb.Size == shot.Size ? rgb : null;
        Clear();
        rgb = keepRgb;
        int w = shot.Width, h = shot.Height;
        using (var blurred = Glass.Blurred(shot, GlassCfg.Blur)) {
            int[] src = Glass.Pixels(blurred);
            int[] dst = (int[])src.Clone();
            if (GlassCfg.Refract) Glass.Refract(src, dst, w, h, plate, radius, GlassCfg.BezelPx);
            Lens = Glass.ToBitmap(dst, w, h);
            if (drops != null && drops.Length > 0) {
                int[] dd = (int[])src.Clone();
                if (GlassCfg.Refract)
                    foreach (var c in drops) Glass.Refract(src, dd, w, h, c, c.Width / 2f, Math.Min(GlassCfg.BezelPx, 12f * Ui.S));
                Drops = Glass.ToBitmap(dd, w, h);
            }
        }
        builtSize = new Size(w, h);
        builtRev = GlassCfg.Rev;
    }

    // Lens, darkening veil, white tint and the top sheen. bright = the small round buttons (lighter glass).
    public void Plate(Graphics g, GraphicsPath path, RectangleF rect, Bitmap lens, bool bright, float darkenScale) {
        if (lens != null)
            using (var tb = new TextureBrush(lens) { WrapMode = WrapMode.Clamp }) g.FillPath(tb, path);
        else
            using (var b = new SolidBrush(Color.FromArgb(0xD9, 0x1A, 0x1C, 0x22))) g.FillPath(b, path);
        float dk = GlassCfg.Darken * darkenScale;
        if (dk > 0f)
            using (var b = new SolidBrush(Color.FromArgb((int)(dk * 0.65f * 255), 10, 10, 14))) g.FillPath(b, path);
        using (var b = Glass.Vertical(rect, Color.FromArgb(bright ? 0x40 : 0x1A, 255, 255, 255), Color.FromArgb(bright ? 0x1A : 0x05, 255, 255, 255)))
            g.FillPath(b, path);
        var st = g.Save();
        g.SetClip(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height / 2f), CombineMode.Intersect);
        using (var b = Glass.Vertical(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height / 2f), Color.FromArgb(bright ? 0x40 : 0x24, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
            g.FillPath(b, path);
        g.Restore(st);
    }

    // Light that follows the pointer (halo inside + lit rim near it) and the wave that spreads on release.
    // refH: size of the light (the bar height, or a row of the panel); gain: how strong it is.
    public void Lighting(Graphics g, GraphicsPath path, RectangleF rect, float refH, float gain) {
        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        float lv = Light.Level * gain;
        if (lv > 0.001f) {
            var lp = Light.Pos;
            using (var b = Glass.Radial(lp, refH * 1.9f, new[] { Glass.White(0.26f * lv), Glass.White(0.17f * lv), Glass.White(0.06f * lv), Glass.White(0f) },
                new[] { 0f, 0.35f, 0.7f, 1f }, holder))
                g.FillEllipse(b, lp.X - refH * 1.9f, lp.Y - refH * 1.9f, refH * 3.8f, refH * 3.8f);
            g.Restore(st);
            using (var b = Glass.Radial(lp, refH * 2.4f, new[] { Glass.White(0.95f * lv), Glass.White(0.35f * lv), Glass.White(0f) }, new[] { 0f, 0.5f, 1f }, holder))
            using (var p = new Pen(b, 2f * Ui.S)) g.DrawPath(p, path);
            st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
        }
        float wv = Light.Wave;
        if (wv < 0.999f) {
            var wp = Light.WavePos;
            float far = 0f;
            foreach (var c in new[] { new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top), new PointF(rect.Left, rect.Bottom), new PointF(rect.Right, rect.Bottom) })
                far = Math.Max(far, (float)Math.Sqrt((c.X - wp.X) * (c.X - wp.X) + (c.Y - wp.Y) * (c.Y - wp.Y)));
            float ring = Math.Max(refH * 0.55f, 10f * Ui.S);
            float radius = ring + far * wv;
            float fd = (float)Math.Pow(1 - wv, 1.15) * gain;
            float inner = Math.Max(0f, Math.Min(0.98f, (radius - ring) / radius));
            float mid = Math.Max(inner + 0.005f, Math.Min(0.995f, inner + (1 - inner) * 0.6f));
            var colors = new[] { Glass.White(0.22f * fd), Glass.White(0.26f * fd), Glass.White(0.42f * fd), Glass.White(0f) };
            var stops = new[] { 0f, inner, mid, 1f };
            using (var b = Glass.Radial(wp, radius, colors, stops, holder))
                g.FillEllipse(b, wp.X - radius, wp.Y - radius, 2 * radius, 2 * radius);
            g.Restore(st);
            using (var b = Glass.Radial(wp, radius, colors, stops, holder))
            using (var p = new Pen(b, 2f * Ui.S)) g.DrawPath(p, path);
            return;
        }
        g.Restore(st);
    }

    // RGB edge and the rim (strong at the top, faint at the bottom).
    public void Edges(Graphics g, GraphicsPath path, RectangleF rect, float radius, bool bright, bool withRgb, Size win) {
        float amount = withRgb ? GlassCfg.RgbAmount : 0f;
        if (amount > 0.01f) {
            if (rgb == null || rgb.Size != win) rgb = new RgbLayer(win.Width, win.Height, rect, radius, 1.3f * Ui.S, 6f * Ui.S);
            g.DrawImage(rgb.Get(GlassCfg.RgbHue * 360f + GlassCfg.Spin, amount), 0, 0, win.Width, win.Height);
        }
        float k = amount > 0.01f ? 1f - amount * 0.6f : 1f;
        using (var b = Glass.Vertical(rect, Color.FromArgb((int)((bright ? 0xE6 : 0xA6) * k), 255, 255, 255), Color.FromArgb((int)(0x26 * k), 255, 255, 255)))
        using (var p = new Pen(b, 1.3f * Ui.S)) g.DrawPath(p, path);
    }
}

// Dark frosted panel with a brightness row; base of the tray panel and the shortcut bar.
// With the Liquid Glass design it becomes a layered window (per-pixel alpha) drawn entirely by GlassSurface.
abstract class DarkPanel : Form {
    protected Bitmap frost;
    protected int value = -1;
    protected bool drag, hoverSlider;
    protected float grow;
    protected readonly FrameClock anim = new FrameClock();
    protected bool acrylic;
    protected bool glass; // Liquid Glass design (decided when the window is created)
    protected readonly GlassSurface surface = new GlassSurface();
    Bitmap buffer, baseLayer, contentLayer; // buffer lives in a DIB section reused by every frame
    bool renderQueued;
    protected bool baseDirty = true; // the glass layer (plate and buttons) has to be drawn again
    protected readonly Font fTitle = Ui.F("Segoe UI Variable Display Semib", "Segoe UI Semibold", 11f, FontStyle.Regular);
    protected readonly Font fLabel = Ui.F("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 9f, FontStyle.Regular);
    protected readonly Font fText = Ui.F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular);
    protected readonly Font fSmall = Ui.F("Segoe UI Variable Small", "Segoe UI", 8.5f, FontStyle.Regular);
    protected readonly Font fBadge = Ui.F("Segoe UI Variable Small Semibol", "Segoe UI", 7f, FontStyle.Bold);
    public event Action<int> SliderMoved;

    protected DarkPanel() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        anim.Tick += delegate {
            float g0 = grow;
            if (!Animate()) anim.Stop();
            // Live backdrop: about 60 times a second, take again what's behind, blur it and bend it (the dock band of the
            // TV launcher does the same each frame), so moving video or windows show through the glass as they change.
            // The pace adapts to what one update costs (at most half the time), so a big panel never starves clicks.
            // Never while the slider is dragged: the bar has to follow the mouse without waiting for a capture.
            if (Live && Visible && IsHandleCreated && !drag && unchecked(Environment.TickCount - lastLive) >= liveEvery) {
                lastLive = Environment.TickCount;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (glass) { BuildGlass(); Render(true); } else { RefreshFrost(); Invalidate(); Update(); }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                liveEvery = Math.Max(16, Math.Min(100, (int)(ms * 2)));
                return;
            }
            if (SkipFrame()) return;
            // Only the light, the wave or the RGB edge move: reuse the glass and the content, redraw just those
            if (glass && grow == g0 && !drag) Render(false);
            else Invalidate();
        };
    }

    protected abstract RectangleF SliderRow { get; }
    protected abstract void PaintContent(Graphics g);
    protected virtual void ItemHover(Point p) { }
    protected virtual void ItemClick(Point p) { }

    // Glass shape and light, per window.
    protected virtual float Corner { get { return Ui.P(24); } }
    protected virtual float LightRef { get { return Ui.P(56); } }
    protected virtual float LightGain { get { return 0.7f; } }
    protected virtual RectangleF[] Drops { get { return null; } }
    protected virtual bool FollowsMouse { get { return false; } } // the light and wave of the glass: only on the shortcut bar
    protected virtual bool LiveBackdrop { get { return false; } } // what's behind is captured again while it's on screen
    protected virtual void PaintGlassInner(Graphics g) { }
    bool Live { get { return LiveBackdrop && (glass || !acrylic); } } // native Acrylic is already live

    RectangleF PlateRect {
        get { float i = 0.6f * Ui.S; return new RectangleF(i, i, ClientSize.Width - 2 * i, ClientSize.Height - 2 * i); }
    }

    float PlateCorner { get { var p = PlateRect; return Math.Min(Corner, Math.Min(p.Width, p.Height) / 2f); } }

    // Steps the animations; returns false when they're all done.
    int lastAnim, lastLive, liveEvery = 16;

    protected virtual bool Animate() {
        int now = Environment.TickCount;
        float dt = lastAnim == 0 ? 15 : Math.Max(1, Math.Min(50, now - lastAnim));
        lastAnim = now;
        float goal = drag ? 1f : (hoverSlider ? 0.5f : 0f);
        grow = Ui.Toward(grow, goal, 1f - (float)Math.Pow(0.75, dt / 15f)); // by time, not by frame
        bool more = grow != goal || (Live && Visible);
        if (glass) more |= surface.Light.Step() | GlassCfg.Spinning;
        return more;
    }

    int lastFrame;

    // While only the RGB edge turns, repaint at about 60 frames per second instead of every refresh.
    protected bool SkipFrame() {
        if (!glass || drag || grow != (hoverSlider ? 0.5f : 0f) || surface.Light.Active) return false;
        int now = Environment.TickCount;
        if (now - lastFrame < 16) return true;
        lastFrame = now;
        return false;
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            if (GlassCfg.On) cp.ExStyle |= 0x80000; // WS_EX_LAYERED: per-pixel alpha for the rounded glass
            return cp;
        }
    }

    // Windows 11 draws real Acrylic blur; on Windows 10 it's imitated with a blurred picture.
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        glass = GlassCfg.On;
        if (glass) { acrylic = false; return; }
        Ui.Round(this);
        acrylic = Ui.Backdrop(this, 3, true);
    }

    // Without Acrylic, takes the background picture before showing, so the window isn't in it.
    protected void ShowFrosted(Point at) {
        Location = at;
        if (!IsHandleCreated) CreateHandle();
        if (glass) {
            BuildGlass();
            Render();
            Show();
            Render();
            if (GlassCfg.Spinning || Live) anim.Start();
            return;
        }
        if (!acrylic) {
            if (frost != null) frost.Dispose();
            frost = Ui.Frost(new Rectangle(at, Size));
        }
        Show();
        Invalidate();
        if (Live) anim.Start();
    }

    // Switches between the Windows 11 and Liquid Glass designs (the window is rebuilt the next time it shows).
    public void ResetDesign() {
        if (Visible) Hide();
        FreeDib();
        if (baseLayer != null) { baseLayer.Dispose(); baseLayer = null; }
        surface.Clear();
        if (frost != null) { frost.Dispose(); frost = null; }
        if (IsHandleCreated) RecreateHandle();
    }

    // A glass setting changed: rebuild the lens now if the window is on screen.
    public void GlassChanged() {
        surface.Reset();
        if (glass && Visible) { BuildGlass(); Render(); if (GlassCfg.Spinning) anim.Start(); }
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

    // Screen captures include our own window: leave it out just for this capture, so it sees what's behind it.
    bool ExcludeFromCapture(bool exclude) {
        return IsHandleCreated && Visible && SetWindowDisplayAffinity(Handle, exclude ? 0x11u : 0u); // WDA_EXCLUDEFROMCAPTURE
    }

    void BuildGlass() {
        bool hidden = ExcludeFromCapture(true);
        try {
            using (var shot = Glass.Capture(new Rectangle(Location, ClientSize)))
                surface.Build(shot, PlateRect, PlateCorner, Drops);
            baseDirty = true;
        } finally {
            if (hidden) ExcludeFromCapture(false);
        }
    }

    // Windows 11 design without native Acrylic: the blurred picture behind has to follow the window's new place and size.
    void RefreshFrost() {
        if (glass || acrylic || !IsHandleCreated) return;
        bool hidden = ExcludeFromCapture(true);
        try {
            if (frost != null) frost.Dispose();
            frost = Ui.Frost(new Rectangle(Location, ClientSize));
        } finally {
            if (hidden) ExcludeFromCapture(false);
        }
    }

    bool moving;

    // Moves and resizes in one go, rebuilding the glass once (not once for the move and again for the size).
    protected void MoveResize(Rectangle r) {
        moving = true;
        try { Bounds = r; } finally { moving = false; }
        if (glass && Visible) { BuildGlass(); Render(); }
        else { if (Visible) RefreshFrost(); Invalidate(); }
    }

    protected override void OnLocationChanged(EventArgs e) {
        base.OnLocationChanged(e);
        if (moving) return;
        if (glass && Visible) { BuildGlass(); Render(); }
    }

    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        if (moving) return;
        if (glass && Visible) { BuildGlass(); Render(); }
        else if (Visible) RefreshFrost();
    }

    // Glass windows are drawn into a bitmap and handed to Windows with their transparency (UpdateLayeredWindow).
    protected override void OnInvalidated(InvalidateEventArgs e) {
        base.OnInvalidated(e);
        if (!glass || renderQueued || !IsHandleCreated || !Visible) return;
        renderQueued = true;
        BeginInvoke((Action)delegate { renderQueued = false; Render(); });
    }

    protected override void OnPaint(PaintEventArgs e) {
        if (glass) return;
        PaintContent(e.Graphics);
    }

    static void Quality(Graphics g) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    static void Fresh(ref Bitmap b, Size s) {
        if (b != null && b.Size == s) return;
        if (b != null) b.Dispose();
        b = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppPArgb);
    }

    // full: redraw the glass and the content too. Animation frames that only move the light, the wave or the RGB edge
    // reuse both layers, so a frame stays cheap enough for 144 or 240 Hz.
    void Render(bool full = true) {
        if (!glass || !IsHandleCreated) return;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (surface.NeedsBuild(size)) { BuildGlass(); full = true; }
        var plate = PlateRect;
        float r = PlateCorner;
        if (baseLayer == null || baseLayer.Size != size) baseDirty = true;
        if (contentLayer == null || contentLayer.Size != size) full = true;
        // The glass is drawn again only when it changed (new capture, a button turned on); moving the slider redraws just the content.
        if (baseDirty) {
            baseDirty = false;
            Fresh(ref baseLayer, size);
            using (var g = Graphics.FromImage(baseLayer)) {
                g.Clear(Color.Transparent);
                Quality(g);
                using (var path = Ui.RoundRect(plate, r)) surface.Plate(g, path, plate, surface.Lens, false, 1f);
                PaintGlassInner(g);
            }
        }
        if (full) {
            Fresh(ref contentLayer, size);
            using (var g = Graphics.FromImage(contentLayer)) {
                g.Clear(Color.Transparent);
                Quality(g);
                PaintContent(g);
            }
        }
        EnsureDib(size);
        using (var g = Graphics.FromImage(buffer)) {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(baseLayer, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Quality(g);
            using (var path = Ui.RoundRect(plate, r)) {
                if (GlassCfg.Light && FollowsMouse) surface.Lighting(g, path, plate, LightRef, LightGain);
                surface.Edges(g, path, plate, r, false, true, size);
            }
            g.DrawImageUnscaled(contentLayer, 0, 0);
        }
        PushDib();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    IntPtr dibDc, dib, dibOld;

    // One DIB section for the whole life of the window: GDI+ draws straight into it and UpdateLayeredWindow reads it,
    // instead of making a new HBITMAP copy every frame.
    void EnsureDib(Size s) {
        if (buffer != null && buffer.Size == s) return;
        FreeDib();
        var bi = new BitmapInfoHeader { Size = 40, Width = s.Width, Height = -s.Height, Planes = 1, BitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), bits;
        dibDc = CreateCompatibleDC(screen);
        dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        dibOld = SelectObject(dibDc, dib);
        buffer = new Bitmap(s.Width, s.Height, s.Width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    void FreeDib() {
        if (buffer != null) { buffer.Dispose(); buffer = null; }
        if (dibDc != IntPtr.Zero) {
            SelectObject(dibDc, dibOld);
            DeleteObject(dib);
            DeleteDC(dibDc);
            dibDc = IntPtr.Zero;
        }
    }

    void PushDib() {
        GdiFlush();
        IntPtr screen = GetDC(IntPtr.Zero);
        try {
            var size = buffer.Size;
            var src = Point.Empty;
            var dst = Location;
            var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, dibDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        } finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    protected override void OnHandleDestroyed(EventArgs e) { FreeDib(); base.OnHandleDestroyed(e); }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Point ptDst, ref Size size, IntPtr src, ref Point ptSrc, int key, ref Blend blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    protected override void OnPaintBackground(PaintEventArgs e) {
        if (glass) return;
        if (frost != null) { e.Graphics.DrawImageUnscaled(frost, 0, 0); return; }
        if (acrylic) {
            e.Graphics.Clear(Color.Transparent);
            using (var b = new SolidBrush(Color.FromArgb(150, 24, 24, 26))) e.Graphics.FillRectangle(b, ClientRectangle);
            return;
        }
        using (var b = new SolidBrush(Color.FromArgb(30, 30, 32))) e.Graphics.FillRectangle(b, ClientRectangle);
    }

    public void SetValue(int v) {
        if (drag) return;
        value = v;
        Invalidate();
    }

    protected void SlideTo(int v) {
        v = Math.Max(0, Math.Min(100, v));
        if (v == value) return;
        value = v;
        Invalidate();
        if (SliderMoved != null) SliderMoved(v);
    }

    // The glass bar fills the whole row; the Windows 11 one leaves room for the suns.
    int ValueAtX(float x) {
        if (!glass) return Ui.ValueAt(SliderRow, x);
        var row = SliderRow;
        return (int)Math.Round((x - row.X) * 100 / Math.Max(1f, row.Width));
    }

    bool OnSlider(Point p) {
        var r = SliderRow;
        r.Inflate(0, Ui.P(6));
        return r.Contains(p);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        if (glass && FollowsMouse) { surface.Light.Move(e.Location); surface.Light.Goal = 1f; anim.Start(); }
        if (OnSlider(e.Location)) { drag = true; anim.Start(); SlideTo(ValueAtX(e.X)); }
        else ItemClick(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (glass && FollowsMouse) {
            surface.Light.Move(e.Location);
            if (surface.Light.Goal < 0.55f) surface.Light.Goal = 0.55f;
            anim.Start();
        }
        if (drag) { SlideTo(ValueAtX(e.X)); return; }
        bool h = OnSlider(e.Location);
        if (h != hoverSlider) { hoverSlider = h; anim.Start(); }
        ItemHover(e.Location);
        if (h) Cursor = Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        drag = false;
        if (glass && FollowsMouse) { surface.Light.FireWave(e.Location); surface.Light.Goal = 0.55f; }
        anim.Start();
    }

    // Losing the mouse mid-drag (Alt+Tab, a system prompt) sends no mouse-up: without this the drag never ends.
    protected override void OnMouseCaptureChanged(EventArgs e) {
        base.OnMouseCaptureChanged(e);
        if (drag && !Capture) { drag = false; anim.Start(); }
    }

    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        if (!Visible) { drag = false; hoverSlider = false; grow = 0; surface.Light.Level = surface.Light.Goal = 0f; surface.Light.Wave = 1f; }
    }

    // Rebuilds the native window while hidden: after the display sleeps or changes mode, an old backdrop window can come back blank.
    public void ResetWindow() {
        if (IsHandleCreated && !Visible) RecreateHandle();
    }
    protected override void OnMouseLeave(EventArgs e) { hoverSlider = false; surface.Light.Goal = 0f; anim.Start(); ItemHover(new Point(-1, -1)); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (value >= 0) SlideTo(value + (e.Delta > 0 ? 2 : -2)); }

    protected void PaintSlider(Graphics g) {
        if (glass) Glass.Slider(g, SliderRow, value, grow, fLabel);
        else Ui.SliderRow(g, SliderRow, value, grow);
    }
}

// Panel that opens when you click the tray icon.
class Popup : DarkPanel {
    protected override bool LiveBackdrop { get { return true; } } // only while the panel is open: a few seconds
    static readonly string[] Labels = { "Eye Care", "Pixel Refresh", L.T("Entrada", "Input") };
    readonly bool[] on = new bool[3];
    int hot = -1;
    string title = L.T("Monitor LG", "LG monitor"), sub = "", status = L.T("Conectando…", "Connecting…"), input = "—";
    string screenOff = "—", sleep = "—";
    public const int RowScreen = 10, RowSleep = 11, RowPlan = 12, ItemHdr = 20, ChipBase = 100, ExpandInput = 2;
    bool connected, hdrOn, hdrSupported;
    RectangleF badge;
    public event Action HdrClicked; // the HDR button: turns Windows HDR on or off
    public int HiddenAt;
    public event Action<int> ButtonClicked;
    public event Action<int, int> ChoiceClicked; // which list (ExpandInput, RowScreen, RowSleep) and the option

    // Options shown when the input button or a time row is expanded inside the panel.
    public class Choice { public string Text; public bool Selected, Dim; }
    int expanded = -1;
    List<Choice> choices = new List<Choice>();
    public int Expanded { get { return expanded; } }

    // Round buttons are drops of glass of their own (GlassCircleButton of Liquid Launch).
    protected override RectangleF[] Drops { get { return new[] { Circle(0), Circle(1), Circle(2) }; } }

    protected override void PaintGlassInner(Graphics g) {
        for (int i = 0; i < Labels.Length; i++) {
            var c = Circle(i);
            using (var path = new GraphicsPath()) {
                path.AddEllipse(c);
                surface.Plate(g, path, c, surface.Drops ?? surface.Lens, true, 0.5f);
                if (on[i])
                    using (var b = Glass.Vertical(c, Color.FromArgb(0xF2, 255, 255, 255), Color.FromArgb(0xD9, 0xDD, 0xE1, 0xE6))) g.FillPath(b, path);
                surface.Edges(g, path, c, c.Width / 2f, true, false, ClientSize);
            }
        }
    }

    public Popup() {
        ClientSize = new Size(Ui.P(300), Ui.P(318));
        KeyPreview = true;
        // Close when the focus really went to another window (checked a moment later: a brief focus change must not close it).
        Deactivate += delegate {
            BeginInvoke((Action)delegate {
                IntPtr fg = GetForegroundWindow();
                if (!Visible || fg == Handle || ContainsFocus) return;
                Hide();
                HiddenAt = Environment.TickCount;
            });
        };
        VisibleChanged += delegate { if (!Visible && expanded >= 0) { expanded = -1; choices = new List<Choice>(); Relayout(); } };
    }

    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW: not in Alt+Tab
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    int W { get { return ClientSize.Width; } }
    protected override RectangleF SliderRow { get { return new RectangleF(Ui.P(18), Ui.P(64), W - Ui.P(36), Ui.P(28)); } }

    RectangleF Column(int i) {
        float colW = (W - Ui.P(16)) / 3f;
        return new RectangleF(Ui.P(8) + colW * i, Ui.P(104), colW, Ui.P(88));
    }

    RectangleF Circle(int i) {
        var col = Column(i);
        float d = Ui.P(44);
        return new RectangleF(col.X + (col.Width - d) / 2, col.Y + Ui.P(4), d, d);
    }

    readonly bool[] rows = { true, true, true }; // turn off display, sleep, power plan

    bool RowOn(int item) { return rows[item - RowScreen]; }

    // ---- Layout, in dp (Ui.P turns it into pixels). The expanded list pushes down what comes after it.
    int Cols { get { return expanded == ExpandInput ? 2 : 4; } }
    const float ChipH = 28, Gap = 6;

    float ChipsHeight {
        get {
            if (expanded < 0 || choices.Count == 0) return 0;
            int n = (choices.Count + Cols - 1) / Cols;
            return n * (ChipH + Gap) - Gap + 14;
        }
    }

    float InputExtra { get { return expanded == ExpandInput ? ChipsHeight + 4 : 0; } }
    float RowExtra(int item) { return expanded == item ? ChipsHeight : 0; }

    float RowY(int item) {
        float y = 206 + InputExtra;
        foreach (int r in new[] { RowScreen, RowSleep }) {
            if (r == item) return y;
            if (RowOn(r)) y += 36 + RowExtra(r);
        }
        return y + (rows[0] || rows[1] ? 4 : 0);
    }

    RectangleF Row(int item) {
        return new RectangleF(Ui.P(8), Ui.P(RowY(item)), W - Ui.P(16), Ui.P(item == RowPlan ? 30 : 34));
    }

    float ChipsTop { get { return expanded == ExpandInput ? 194 : RowY(expanded) + 34; } }

    RectangleF ChipsArea { get { return new RectangleF(Ui.P(10), Ui.P(ChipsTop + 2), W - Ui.P(20), Ui.P(ChipsHeight - 4)); } }

    RectangleF Chip(int i) {
        int cols = Cols;
        float wdp = W / Ui.S, padX = 16;
        float cw = (wdp - 2 * padX - Gap * (cols - 1)) / cols;
        int row = i / cols, col = i % cols;
        return new RectangleF(Ui.P(padX + col * (cw + Gap)), Ui.P(ChipsTop + 7 + row * (ChipH + Gap)), Ui.P(cw), Ui.P(ChipH));
    }

    float BottomDp {
        get {
            float bottom = 198 + InputExtra;
            foreach (int r in new[] { RowScreen, RowSleep, RowPlan })
                if (RowOn(r)) bottom = RowY(r) + (r == RowPlan ? 30 : 34) + RowExtra(r) + 6;
            return bottom;
        }
    }

    // New height; while it's on screen the edge next to the taskbar stays where it is.
    void Relayout() {
        int h = Ui.P(BottomDp);
        if (h != ClientSize.Height) {
            if (Visible) {
                var wa = Screen.FromControl(this).WorkingArea;
                bool keepBottom = Top + Height / 2 > wa.Top + wa.Height / 2;
                int top = keepBottom ? Bottom - h : Top;
                top = Math.Max(wa.Top + Ui.P(8), Math.Min(top, wa.Bottom - h - Ui.P(8)));
                MoveResize(new Rectangle(Left, top, Width, h));
            } else ClientSize = new Size(ClientSize.Width, h);
        }
        Invalidate();
    }

    public void SetRows(bool screen, bool sl, bool plan) {
        rows[0] = screen; rows[1] = sl; rows[2] = plan;
        if (expanded >= RowScreen && !RowOn(expanded)) { expanded = -1; choices = new List<Choice>(); }
        Relayout();
    }

    public void Expand(int item, List<Choice> list) { expanded = item; choices = list; hot = -1; Relayout(); }

    public void Collapse() {
        if (expanded < 0) return;
        expanded = -1;
        choices = new List<Choice>();
        hot = -1;
        Relayout();
    }

    public bool IsOn(int i) { return on[i]; }
    public void SetHdr(bool supported, bool enabled) { hdrSupported = supported; hdrOn = enabled; Invalidate(); }
    public void SetTimes(string screen, string sl) { screenOff = screen; sleep = sl; Invalidate(); }
    public void SetButton(int i, bool v) { on[i] = v; baseDirty = true; Invalidate(); }
    public void SetTitle(string t, string s) { title = t; sub = s; Invalidate(); }
    public void SetStatus(string s, bool ok) { status = s; connected = ok; Invalidate(); }
    public void SetInput(string name) { input = name; Invalidate(); }

    public void ShowNear(Point p) {
        var wa = Screen.FromPoint(p).WorkingArea;
        int m = Ui.P(12);
        int x = Math.Max(wa.Left + m, Math.Min(p.X - Width / 2, wa.Right - Width - m));
        int y = p.Y >= wa.Bottom ? wa.Bottom - Height - m : (p.Y <= wa.Top ? wa.Top + m : Math.Max(wa.Top + m, p.Y - Height - m));
        ShowFrosted(new Point(x, y));
        Activate();
        // If something moved the activation while showing, take it back: otherwise a click outside wouldn't close the panel.
        BeginInvoke((Action)delegate { if (Visible && GetForegroundWindow() != Handle) Activate(); });
    }

    int Hit(Point p) {
        if (hdrSupported && badge.Contains(p)) return ItemHdr;
        for (int i = 0; i < choices.Count && expanded >= 0; i++) if (Chip(i).Contains(p)) return ChipBase + i;
        for (int i = 0; i < Labels.Length; i++) if (Column(i).Contains(p)) return i;
        foreach (int r in new[] { RowScreen, RowSleep, RowPlan }) if (RowOn(r) && Row(r).Contains(p)) return r;
        return -1;
    }

    protected override void ItemHover(Point p) {
        int h = Hit(p);
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void ItemClick(Point p) {
        int h = Hit(p);
        if (h >= ChipBase) { if (ChoiceClicked != null) ChoiceClicked(expanded, h - ChipBase); }
        else if (h == ItemHdr) { if (HdrClicked != null) HdrClicked(); }
        else if (h >= 0 && ButtonClicked != null) ButtonClicked(h);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys k) {
        int b = Math.Max(0, value);
        switch (k) {
            case Keys.Left: case Keys.Down: SlideTo(b - 1); return true;
            case Keys.Right: case Keys.Up: SlideTo(b + 1); return true;
            case Keys.PageDown: SlideTo(b - 10); return true;
            case Keys.PageUp: SlideTo(b + 10); return true;
            case Keys.Home: SlideTo(0); return true;
            case Keys.End: SlideTo(100); return true;
            case Keys.Escape: if (expanded >= 0) Collapse(); else Hide(); return true;
        }
        return base.ProcessCmdKey(ref msg, k);
    }

    static void ChevronUp(Graphics g, PointF c, float s, Color col) {
        using (var p = new Pen(col, Math.Max(1.4f, 1.6f * Ui.S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(p, new[] { new PointF(c.X - s * 0.5f, c.Y + s * 0.22f), new PointF(c.X, c.Y - s * 0.25f), new PointF(c.X + s * 0.5f, c.Y + s * 0.22f) });
    }

    void PaintChips(Graphics g) {
        if (expanded < 0 || choices.Count == 0) return;
        using (var path = Ui.RoundRect(ChipsArea, Ui.P(12)))
        using (var b = new SolidBrush(Color.FromArgb(glass ? 22 : 14, 255, 255, 255))) g.FillPath(b, path);
        for (int i = 0; i < choices.Count; i++) {
            var c = choices[i];
            var r = Chip(i);
            bool hov = hot == ChipBase + i;
            Color fill, text;
            if (c.Selected) {
                fill = glass ? Color.FromArgb(235, 245, 245, 247) : Ui.Accent;
                text = glass || Ui.Accent.GetBrightness() > 0.6f ? Color.FromArgb(28, 28, 30) : Color.White;
            } else {
                fill = Color.FromArgb(hov ? 60 : (glass ? 30 : 24), 255, 255, 255);
                text = c.Dim ? Ui.Text2 : Ui.Text;
            }
            using (var path = Ui.Capsule(r)) {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!c.Selected) using (var p = new Pen(Color.FromArgb(glass ? 46 : 30, 255, 255, 255), 1)) g.DrawPath(p, path);
            }
            Ui.DrawText(g, c.Text, fSmall, r, text, StringAlignment.Center);
        }
    }

    protected override void PaintContent(Graphics g) {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float pad = Ui.P(18);

        float tw = Math.Min(g.MeasureString(title, fTitle, PointF.Empty, StringFormat.GenericTypographic).Width, W - 2 * pad - Ui.P(100));
        Ui.DrawText(g, title, fTitle, new RectangleF(pad, Ui.P(12), tw + Ui.P(4), Ui.P(24)), Ui.Text, StringAlignment.Near);
        // HDR button: yellow when Windows HDR is on, outlined when it's off. Tapping it switches Windows HDR.
        if (hdrSupported) {
            badge = new RectangleF(pad + tw + Ui.P(10), Ui.P(15), Ui.P(38), Ui.P(19));
            bool hov = hot == ItemHdr;
            using (var path = Ui.RoundRect(badge, Ui.P(5))) {
                if (hdrOn) using (var b = new SolidBrush(hov ? Color.FromArgb(255, 226, 80) : Color.FromArgb(255, 214, 10))) g.FillPath(b, path);
                else {
                    if (hov) using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(hov ? 240 : 190, 255, 255, 255), Math.Max(1f, 1.2f * Ui.S))) g.DrawPath(p, path);
                }
            }
            Ui.DrawText(g, "HDR", fBadge, badge, hdrOn ? Color.FromArgb(28, 28, 30) : Color.FromArgb(hov ? 255 : 220, 255, 255, 255), StringAlignment.Center);
        } else badge = RectangleF.Empty;
        if (value >= 0 && !glass) // the glass bar shows it inside
            Ui.DrawText(g, value + "%", fSmall, new RectangleF(W - pad - Ui.P(50), Ui.P(12), Ui.P(50), Ui.P(24)), Ui.Text2, StringAlignment.Far);
        string s = sub;
        if (!connected) s = sub == "" ? status : sub + " · " + status;
        Ui.DrawText(g, s, fSmall, new RectangleF(pad, Ui.P(34), W - 2 * pad, Ui.P(18)), Ui.Text2, StringAlignment.Near);

        PaintSlider(g);

        for (int i = 0; i < Labels.Length; i++) {
            var c = Circle(i);
            var center = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2);
            bool open = i == ExpandInput && expanded == ExpandInput;
            if (!glass) { // in glass the drop was painted with the glass, under the light
                var fill = on[i] ? Color.FromArgb(245, 245, 247) : Color.FromArgb(hot == i || open ? 58 : 38, 255, 255, 255);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, c);
            } else if (open)
                using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillEllipse(b, c);
            var ic = on[i] ? Color.FromArgb(28, 28, 30) : Ui.Text;
            float sz = c.Width * 0.46f;
            if (i == 0) Ui.IconEye(g, center, sz, ic);
            else if (i == 1) Ui.IconPixels(g, center, sz, ic);
            else Ui.IconInput(g, center, sz, ic);
            var col = Column(i);
            Ui.DrawText(g, Labels[i], fLabel, new RectangleF(col.X, c.Bottom + Ui.P(5), col.Width, Ui.P(18)), Ui.Text, StringAlignment.Center);
            string st = i == 2 ? input : on[i] ? (i == 1 ? L.T("Al apagar", "At power off") : "On") : "Off";
            Ui.DrawText(g, st, fSmall, new RectangleF(col.X, c.Bottom + Ui.P(22), col.Width, Ui.P(16)), Ui.Text2, StringAlignment.Center);
        }

        using (var p = new Pen(Ui.Line, 1)) {
            if (rows[0] || rows[1] || rows[2]) g.DrawLine(p, pad, Ui.P(200 + InputExtra), W - pad, Ui.P(200 + InputExtra));
            if (rows[2] && (rows[0] || rows[1])) g.DrawLine(p, pad, Ui.P(RowY(RowPlan) - 3), W - pad, Ui.P(RowY(RowPlan) - 3));
        }
        foreach (int item in new[] { RowScreen, RowSleep, RowPlan }) {
            if (!RowOn(item)) continue;
            var r = Row(item);
            if (hot == item || expanded == item)
                using (var path = Ui.RoundRect(r, Ui.P(8)))
                using (var b = new SolidBrush(Color.FromArgb(expanded == item ? 26 : 20, 255, 255, 255)))
                    g.FillPath(b, path);
            var txt = new RectangleF(r.X + Ui.P(10), r.Y, r.Width * 0.6f, r.Height);
            float cx = r.Right - Ui.P(16), cy = r.Y + r.Height / 2;
            if (item == RowPlan) {
                Ui.DrawText(g, L.T("Editar plan de energía", "Edit power plan"), fText, txt, Ui.Text2, StringAlignment.Near);
                Ui.Chevron(g, new PointF(cx, cy), Ui.P(8), Ui.Text2);
                continue;
            }
            Ui.DrawText(g, item == RowScreen ? L.T("Apagar pantalla", "Turn off display") : L.T("Suspender PC", "Sleep PC"), fText, txt, Ui.Text, StringAlignment.Near);
            Ui.DrawText(g, item == RowScreen ? screenOff : sleep, fSmall, new RectangleF(r.X, r.Y, r.Width - Ui.P(32), r.Height), Ui.Text2, StringAlignment.Far);
            if (expanded == item) ChevronUp(g, new PointF(cx, cy), Ui.P(8), Ui.Text);
            else Ui.ChevronDown(g, new PointF(cx, cy), Ui.P(8), Ui.Text2);
        }
        PaintChips(g);
    }
}

// Brightness bar shown by the shortcuts: same brightness row, draggable with the mouse. Doesn't steal focus.
class Osd : DarkPanel {
    public double Seconds = 1.5;
    public string Position = "abajo";
    readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();

    public Osd() {
        ClientSize = new Size(Ui.P(340), Ui.P(56));
        hide.Tick += delegate {
            if (drag || Bounds.Contains(Cursor.Position)) return; // wait while the mouse is over it
            hide.Stop();
            Hide();
        };
        SliderMoved += delegate { Restart(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override RectangleF SliderRow { get { return new RectangleF(Ui.P(18), (ClientSize.Height - Ui.P(28)) / 2f, ClientSize.Width - Ui.P(36), Ui.P(28)); } }

    // A pill of glass, lit like the search bar of Liquid Launch.
    protected override float Corner { get { return ClientSize.Height / 2f; } }
    protected override float LightRef { get { return ClientSize.Height; } }
    protected override float LightGain { get { return 1f; } }

    Point Where() {
        var wa = Screen.PrimaryScreen.WorkingArea;
        int cx = wa.Left + (wa.Width - Width) / 2;
        if (Position == "arriba") return new Point(cx, wa.Top + Ui.P(24));
        if (Position == "arriba-der") return new Point(wa.Right - Width - Ui.P(14), wa.Top + Ui.P(14));
        return new Point(cx, wa.Bottom - Height - Ui.P(72));
    }

    void Restart() {
        hide.Stop();
        hide.Interval = (int)(Seconds * 1000);
        hide.Start();
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    string message; // shown instead of the bar when there is no brightness to show
    public bool ShowingMessage { get { return Visible && message != null; } }

    public void ShowValue(int v) {
        message = null;
        SetValue(v);
        Present();
    }

    public void ShowMessage(string s) {
        message = s;
        Invalidate();
        Present();
    }

    void Present() {
        var at = Where();
        if (!Visible) ShowFrosted(at);
        else if (Location != at) Location = at;
        // Back to the top of the always-on-top windows, without taking the focus.
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
        if (!IsWindowVisible(Handle)) Log.Write("osd: window is not visible after showing it, at " + at);
        Restart();
    }

    protected override bool FollowsMouse { get { return message == null; } }
    protected override bool LiveBackdrop { get { return true; } }

    protected override void OnMouseDown(MouseEventArgs e) { if (message == null) base.OnMouseDown(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (message == null) base.OnMouseWheel(e); }

    protected override void PaintContent(Graphics g) {
        if (message == null) { PaintSlider(g); return; }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int s = Ui.P(18), pad = Ui.P(18);
        g.DrawImage(Ui.SunCached(s, Color.FromArgb(150, 150, 156)), pad, (ClientSize.Height - s) / 2f);
        Ui.DrawText(g, message, fText, new RectangleF(pad + s + Ui.P(12), 0, ClientSize.Width - 2 * pad - s - Ui.P(12), ClientSize.Height), Ui.Text, StringAlignment.Near);
    }
}

// Color swatches for the bar.
class Swatches : Control {
    public static readonly string[] Colors = { "#0A84FF", "#64D2FF", "#30D158", "#FFD60A", "#FF9F0A", "#FF375F", "#BF5AF2", "#F2F2F7" };
    public string Value = Colors[0];

    public Swatches() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    float Step { get { return (float)Width / Colors.Length; } }

    protected override void OnMouseDown(MouseEventArgs e) {
        int i = (int)(e.X / Step);
        if (i >= 0 && i < Colors.Length) { Value = Colors[i]; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float d = Math.Min(Step, Height) - Ui.P(8);
        for (int i = 0; i < Colors.Length; i++) {
            float cx = Step * i + Step / 2, cy = Height / 2f;
            using (var b = new SolidBrush(Ui.ParseColor(Colors[i]))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            if (string.Equals(Colors[i], Value, StringComparison.OrdinalIgnoreCase))
                using (var p = new Pen(Color.White, Math.Max(1.5f, 2 * Ui.S))) {
                    float r = d / 2 + Ui.P(3);
                    g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
                }
        }
    }
}

// ---------------------------------------------------------------- Windows 11 style controls for the settings window

static class Theme {
    public static readonly Color Window = Color.FromArgb(32, 32, 32);
    public static readonly Color Card = Color.FromArgb(43, 43, 43);
    public static readonly Color CardLine = Color.FromArgb(55, 55, 55);
    public static readonly Color Field = Color.FromArgb(56, 56, 58);
    public static readonly Color Text = Color.FromArgb(245, 245, 247);
    public static readonly Color Text2 = Color.FromArgb(160, 160, 166);
}

// On/off switch, like the ones in Windows 11 Settings.
class ToggleSwitch : Control {
    bool on;
    float pos;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
    public event EventHandler CheckedChanged;

    public ToggleSwitch() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Size = new Size(Ui.P(40), Ui.P(20));
        Cursor = Cursors.Hand;
        anim.Tick += delegate {
            pos = Ui.Toward(pos, on ? 1f : 0f, 0.3f);
            if (pos == (on ? 1f : 0f)) anim.Stop();
            Invalidate();
        };
    }

    public bool Checked {
        get { return on; }
        set {
            if (on == value) return;
            on = value;
            anim.Start();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    public void SetChecked(bool v) { on = v; pos = v ? 1 : 0; Invalidate(); }

    protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = !Checked; } }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space && Enabled) Checked = !Checked; }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        int a = Enabled ? 255 : 90;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var path = Ui.Capsule(r)) {
            if (pos > 0.01f) using (var b = new SolidBrush(Color.FromArgb((int)(a * pos), Ui.Accent))) g.FillPath(b, path);
            if (pos < 0.99f) using (var p = new Pen(Color.FromArgb((int)(a * (1 - pos) * 0.75f), 210, 210, 214), Math.Max(1f, Ui.S))) g.DrawPath(p, path);
        }
        float kr = r.Height * (0.24f + 0.08f * pos);
        float kx = r.X + r.Height / 2 + (r.Width - r.Height) * pos, ky = r.Y + r.Height / 2;
        var kc = pos > 0.5f ? (Ui.Accent.GetBrightness() > 0.7f ? Color.FromArgb(a, 28, 28, 30) : Color.FromArgb(a, 255, 255, 255)) : Color.FromArgb(a, 205, 205, 210);
        using (var b = new SolidBrush(kc)) g.FillEllipse(b, kx - kr, ky - kr, 2 * kr, 2 * kr);
    }
}

// A row of options where one is chosen (Windows 11 segmented control).
class Segmented : Control {
    readonly string[] items;
    int sel, hover = -1;
    public event EventHandler SelectedChanged;

    public Segmented(params string[] items) {
        this.items = items;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Ui.P(32);
        Cursor = Cursors.Hand;
        Font = Ui.F("Segoe UI Variable Text", "Segoe UI", 9.5f, FontStyle.Regular);
    }

    public int Selected {
        get { return sel; }
        set { sel = Math.Max(0, Math.Min(items.Length - 1, value)); Invalidate(); }
    }

    int At(int x) { return Math.Max(0, Math.Min(items.Length - 1, x * items.Length / Math.Max(1, Width))); }

    protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.X); if (h != hover) { hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) {
        int i = At(e.X);
        if (i == sel) return;
        sel = i;
        Invalidate();
        if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Ui.RoundRect(r, Ui.P(6)))
        using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
        float w = r.Width / items.Length;
        for (int i = 0; i < items.Length; i++) {
            var cell = new RectangleF(r.X + w * i + Ui.P(3), r.Y + Ui.P(3), w - Ui.P(6), r.Height - Ui.P(6));
            if (i == sel)
                using (var path = Ui.RoundRect(cell, Ui.P(4)))
                using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
            else if (i == hover)
                using (var path = Ui.RoundRect(cell, Ui.P(4)))
                using (var b = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillPath(b, path);
            var tc = i == sel ? (Ui.Accent.GetBrightness() > 0.6f ? Color.FromArgb(28, 28, 30) : Color.White) : Theme.Text2;
            Ui.DrawText(g, items[i], Font, cell, tc, StringAlignment.Center);
        }
    }
}

// Flat rounded button; Accent = the main action.
class ModernButton : Control {
    public bool Accent;
    bool hover, down;

    public ModernButton(string text, bool accent) {
        Text = text;
        Accent = accent;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(Ui.P(110), Ui.P(32));
        Font = Ui.F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Color fill = Accent ? Ui.Accent : Theme.Field;
        if (down) fill = Color.FromArgb(Math.Max(0, fill.R - 25), Math.Max(0, fill.G - 25), Math.Max(0, fill.B - 25));
        else if (hover) fill = Color.FromArgb(Math.Min(255, fill.R + 18), Math.Min(255, fill.G + 18), Math.Min(255, fill.B + 18));
        if (!Enabled) fill = Color.FromArgb(120, fill);
        using (var path = Ui.RoundRect(r, Ui.P(6))) {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (!Accent) using (var p = new Pen(Color.FromArgb(28, 255, 255, 255), 1)) g.DrawPath(p, path);
        }
        var tc = Accent && Ui.Accent.GetBrightness() > 0.6f ? Color.FromArgb(28, 28, 30) : Theme.Text;
        Ui.DrawText(g, Text, Font, r, Enabled ? tc : Theme.Text2, StringAlignment.Center);
    }
}

// Rounded box around a text field.
class FieldBox : Panel {
    public readonly TextBox Box;

    public FieldBox(TextBox box, int width) {
        Box = box;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(width, Ui.P(32));
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Field;
        box.ForeColor = Theme.Text;
        box.SetBounds(Ui.P(10), (Height - box.PreferredHeight) / 2, width - Ui.P(20), box.PreferredHeight);
        Controls.Add(box);
        box.GotFocus += delegate { Invalidate(); };
        box.LostFocus += delegate { Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Ui.RoundRect(r, Ui.P(6))) {
            using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(30, 255, 255, 255), 1)) g.DrawPath(p, path);
        }
        if (Box.Focused)
            using (var p = new Pen(Ui.Accent, Math.Max(1.5f, 2 * Ui.S)))
                g.DrawLine(p, Ui.P(6), Height - 1.5f * Ui.S, Width - Ui.P(6), Height - 1.5f * Ui.S);
    }
}

// A settings card: rounded block with rows separated by thin lines.
class Card : Panel {
    public readonly List<int> Lines = new List<int>();

    public Card() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Window);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Ui.RoundRect(r, Ui.P(8))) {
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(22, 255, 255, 255), 1)) g.DrawPath(p, path);
        }
        using (var p = new Pen(Theme.CardLine, 1))
            foreach (int y in Lines) g.DrawLine(p, Ui.P(16), y, Width - Ui.P(16), y);
    }
}

// ---------------------------------------------------------------- Settings window

class SettingsForm : Form {
    static readonly string[] PosKeys = { "abajo", "arriba", "arriba-der" };

    readonly Config cfg;
    readonly Segmented design = new Segmented("Windows 11", "Liquid Glass");
    readonly Segmented pos = new Segmented(L.T("Abajo", "Bottom"), L.T("Arriba", "Top"), L.T("Arriba der.", "Top right"));
    readonly Swatches colors = new Swatches();
    readonly MiniSlider secs = new MiniSlider(), step = new MiniSlider();
    readonly ToggleSwitch startup = new ToggleSwitch(), showScreen = new ToggleSwitch(), showSleep = new ToggleSwitch(), showPlan = new ToggleSwitch();
    readonly HotkeyBox keyUp = new HotkeyBox(), keyDown = new HotkeyBox();
    readonly TextBox ip = new TextBox();
    readonly ModernButton find = new ModernButton(L.T("Buscar", "Find"), false), glassBtn = new ModernButton(L.T("Abrir", "Open"), false);
    readonly ModernButton save = new ModernButton(L.T("Guardar", "Save"), true), cancel = new ModernButton(L.T("Cancelar", "Cancel"), false);
    readonly Font fHead = Ui.F("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 10.5f, FontStyle.Regular);
    readonly Font fRow = Ui.F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular);
    readonly Font fSub = Ui.F("Segoe UI Variable Small", "Segoe UI", 8.5f, FontStyle.Regular);
    readonly int[] colY = new int[2];
    readonly int colW;
    Card card;
    int cardCol;

    public bool Startup { get { return startup.Checked; } }
    public event Action<bool> Saved; // true if the IP changed
    public event Action AdjustGlass;

    public SettingsForm(Config cfg, bool isStartup) {
        this.cfg = cfg;
        Text = L.T("LGBrillo · Configuración", "LGBrillo · Settings");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = fRow;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        ClientSize = new Size(Ui.P(900), Ui.P(700));
        colW = (ClientSize.Width - Ui.P(24) * 3) / 2;

        var title = new Label { Text = L.T("Configuración", "Settings"), AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Window,
            Font = Ui.F("Segoe UI Variable Display Semib", "Segoe UI Semibold", 18f, FontStyle.Regular), Location = new Point(Ui.P(22), Ui.P(14)) };
        Controls.Add(title);
        colY[0] = colY[1] = Ui.P(70);

        // ---- Left column
        Begin(0, L.T("Apariencia", "Appearance"));
        design.Selected = cfg.Design == "glass" ? 1 : 0;
        Row(L.T("Diseño", "Design"), null, design, Ui.P(240));
        glassBtn.Click += delegate { if (AdjustGlass != null) AdjustGlass(); };
        Row(L.T("Ajustes del vidrio", "Glass settings"), L.T("Desenfoque, reflejos, borde RGB y luz", "Blur, reflections, RGB edge and light"), glassBtn, Ui.P(110));
        colors.Value = cfg.Color;
        Row(L.T("Color de la barra", "Bar color"), L.T("En el diseño Windows 11", "In the Windows 11 design"), colors, Ui.P(220));
        End();

        // ---- Right column
        Begin(1, L.T("Cartel de los atajos", "Shortcut bar"));
        secs.Value = (float)((Math.Max(0.5, Math.Min(10, cfg.OsdSegundos)) - 0.5) / 9.5);
        Row(L.T("Duración", "Duration"), null, secs, Ui.P(170), Value(secs, v => SecsOf(v).ToString("0.0") + " s"));
        pos.Selected = Math.Max(0, Array.IndexOf(PosKeys, cfg.OsdPos));
        Row(L.T("Posición", "Position"), null, pos, Ui.P(250));
        End();

        Begin(1, L.T("Atajos de teclado", "Keyboard shortcuts"));
        keyUp.Set(cfg.UpMods, (Keys)cfg.UpKey);
        keyDown.Set(cfg.DownMods, (Keys)cfg.DownKey);
        Row(L.T("Subir el brillo", "Brightness up"), null, new FieldBox(keyUp, Ui.P(180)), Ui.P(180));
        Row(L.T("Bajar el brillo", "Brightness down"), null, new FieldBox(keyDown, Ui.P(180)), Ui.P(180));
        step.Value = (Math.Max(1, Math.Min(20, cfg.Paso)) - 1) / 19f;
        Row(L.T("Paso", "Step"), null, step, Ui.P(170), Value(step, v => StepOf(v).ToString()));
        End();

        Begin(0, L.T("Mostrar en el panel", "Show in the panel"));
        Toggle(showScreen, L.T("Apagar pantalla", "Turn off display"), !cfg.HideScreenOff);
        Toggle(showSleep, L.T("Suspender PC", "Sleep PC"), !cfg.HideSleep);
        Toggle(showPlan, L.T("Editar plan de energía", "Edit power plan"), !cfg.HidePlan);
        End();

        Begin(1, L.T("Monitor y sistema", "Monitor and system"));
        ip.Text = cfg.Ip;
        var ipBox = new Panel { BackColor = Theme.Card, Size = new Size(Ui.P(250), Ui.P(32)) };
        var ipField = new FieldBox(ip, Ui.P(160));
        ipField.Location = Point.Empty;
        find.SetBounds(Ui.P(168), 0, Ui.P(82), Ui.P(32));
        find.BackColor = Theme.Card;
        ipBox.Controls.Add(ipField);
        ipBox.Controls.Add(find);
        Row(L.T("IP del monitor", "Monitor IP"), null, ipBox, Ui.P(250));
        startup.SetChecked(isStartup);
        Row(L.T("Iniciar con Windows", "Start with Windows"), null, startup, startup.Width);
        End();

        int bottom = Math.Max(colY[0], colY[1]) + Ui.P(8);
        ClientSize = new Size(ClientSize.Width, bottom + Ui.P(60));
        cancel.SetBounds(ClientSize.Width - Ui.P(24) - Ui.P(110), ClientSize.Height - Ui.P(48), Ui.P(110), Ui.P(32));
        save.SetBounds(cancel.Left - Ui.P(10) - Ui.P(110), cancel.Top, Ui.P(110), Ui.P(32));
        cancel.BackColor = save.BackColor = Theme.Window;
        Controls.Add(save);
        Controls.Add(cancel);

        find.Click += async delegate {
            find.Enabled = false;
            find.Text = "…";
            string found = await Tv.Discover();
            if (found != null) ip.Text = found;
            find.Text = found != null ? L.T("Buscar", "Find") : L.T("No está", "Not found");
            find.Enabled = true;
        };
        cancel.Click += delegate { Close(); };
        save.Click += delegate { SaveAndClose(); };
    }

    static double SecsOf(float v) { return 0.5 + Math.Round(v * 19) * 0.5; }
    static int StepOf(float v) { return 1 + (int)Math.Round(v * 19); }

    protected override bool ProcessCmdKey(ref Message msg, Keys k) {
        if (k == Keys.Escape && !(ActiveControl is HotkeyBox)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, k);
    }

    void SaveAndClose() {
        if (keyUp.Mods == keyDown.Mods && keyUp.Key == keyDown.Key) {
            MessageBox.Show(this, L.T("Los dos atajos no pueden ser iguales.", "The two shortcuts can't be the same."), "LGBrillo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string newIp = ip.Text.Trim();
        bool ipChanged = newIp != cfg.Ip;
        cfg.Design = design.Selected == 1 ? "glass" : "win11";
        cfg.Color = colors.Value;
        cfg.OsdSegundos = SecsOf(secs.Value);
        cfg.OsdPos = PosKeys[pos.Selected];
        cfg.UpMods = keyUp.Mods; cfg.UpKey = (int)keyUp.Key;
        cfg.DownMods = keyDown.Mods; cfg.DownKey = (int)keyDown.Key;
        cfg.Paso = StepOf(step.Value);
        cfg.HideScreenOff = !showScreen.Checked;
        cfg.HideSleep = !showSleep.Checked;
        cfg.HidePlan = !showPlan.Checked;
        cfg.Ip = newIp;
        cfg.Save();
        if (Saved != null) Saved(ipChanged);
        Close();
    }

    // ---- Layout helpers: a header and a card per section, rows inside the card.

    void Begin(int col, string header) {
        cardCol = col;
        int x = Ui.P(24) + col * (colW + Ui.P(24));
        var h = new Label { Text = header, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Window, Font = fHead, Location = new Point(x + Ui.P(2), colY[col]) };
        Controls.Add(h);
        colY[col] += Ui.P(28);
        card = new Card { Location = new Point(x, colY[col]), Size = new Size(colW, 0) };
        Controls.Add(card);
    }

    void End() {
        colY[cardCol] = card.Bottom + Ui.P(18);
        card.Invalidate();
    }

    Label Value(MiniSlider s, Func<float, string> fmt) {
        var l = new Label { AutoSize = false, ForeColor = Theme.Text2, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleRight, Font = fSub, Text = fmt(s.Value) };
        s.ValueChanged += delegate { l.Text = fmt(s.Value); };
        return l;
    }

    void Toggle(ToggleSwitch t, string label, bool value) {
        t.SetChecked(value);
        Row(label, null, t, t.Width);
    }

    void Row(string label, string subtitle, Control c, int width, Label value = null) {
        int h = Ui.P(subtitle != null ? 58 : 48);
        int y = card.Height;
        if (y > 0) card.Lines.Add(y);
        var l = new Label { Text = label, AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleLeft, Font = fRow };
        int textW = card.Width - width - Ui.P(40) - (value != null ? Ui.P(48) : 0);
        if (subtitle != null) {
            l.SetBounds(Ui.P(16), y + Ui.P(9), textW, Ui.P(22));
            var s = new Label { Text = subtitle, AutoSize = false, ForeColor = Theme.Text2, BackColor = Theme.Card, Font = fSub, AutoEllipsis = true };
            s.SetBounds(Ui.P(16), y + Ui.P(31), textW, Ui.P(18));
            card.Controls.Add(s);
        } else l.SetBounds(Ui.P(16), y, textW, h);
        card.Controls.Add(l);
        c.BackColor = Theme.Card;
        int right = card.Width - Ui.P(16);
        if (value != null) {
            value.SetBounds(right - Ui.P(48), y, Ui.P(48), h);
            card.Controls.Add(value);
            right -= Ui.P(52);
        }
        c.Width = width;
        if (c is Swatches) c.Height = Ui.P(30);
        c.Location = new Point(right - width, y + (h - c.Height) / 2);
        card.Controls.Add(c);
        card.Height = y + h;
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Ui.DarkTitle(this);
        Ui.Backdrop(this, 2, false);
    }
}

// Small slider for the settings windows (Windows 11 style). Rainbow = hue picker.
class MiniSlider : Control {
    float val;
    bool drag;
    public bool Rainbow;
    public event EventHandler ValueChanged;

    public MiniSlider() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = Ui.P(26);
    }

    public float Value {
        get { return val; }
        set { val = Math.Max(0f, Math.Min(1f, value)); Invalidate(); }
    }

    float Pad { get { return Ui.P(10); } }

    void SetFrom(float v) {
        v = Math.Max(0f, Math.Min(1f, v));
        if (Math.Abs(v - val) < 0.0001f) return;
        val = v;
        Invalidate();
        if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
    }

    void FromX(int x) { SetFrom((x - Pad) / Math.Max(1f, Width - 2 * Pad)); }

    protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; Focus(); drag = true; FromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) drag = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { if (Enabled) SetFrom(val + (e.Delta > 0 ? 0.02f : -0.02f)); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Left) SetFrom(val - 0.02f);
        else if (e.KeyCode == Keys.Right) SetFrom(val + 0.02f);
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        int a = Enabled ? 255 : 90;
        float cy = Height / 2f, th = Ui.P(4);
        var track = new RectangleF(Pad, cy - th / 2, Width - 2 * Pad, th);
        float kx = Pad + (Width - 2 * Pad) * val;
        if (Rainbow) {
            var cols = Glass.RainbowColors();
            using (var b = new LinearGradientBrush(new RectangleF(track.X - 1, track.Y, track.Width + 2, track.Height), Color.White, Color.White, 0f)) {
                var blend = new ColorBlend(cols.Length);
                for (int i = 0; i < cols.Length; i++) { blend.Colors[i] = Color.FromArgb(a, cols[i]); blend.Positions[i] = i / (float)(cols.Length - 1); }
                b.InterpolationColors = blend;
                using (var path = Ui.Capsule(track)) g.FillPath(b, path);
            }
        } else {
            using (var path = Ui.Capsule(track))
            using (var b = new SolidBrush(Color.FromArgb(a * 60 / 255, 255, 255, 255))) g.FillPath(b, path);
            if (kx > track.X)
                using (var path = Ui.Capsule(new RectangleF(track.X, track.Y, kx - track.X, th)))
                using (var b = new SolidBrush(Color.FromArgb(a, Ui.Accent))) g.FillPath(b, path);
        }
        float r = Ui.P(8);
        using (var b = new SolidBrush(Color.FromArgb(a * 50 / 255, 0, 0, 0))) g.FillEllipse(b, kx - r, cy - r + Ui.S, 2 * r, 2 * r);
        using (var b = new SolidBrush(Color.FromArgb(a, 245, 245, 247))) g.FillEllipse(b, kx - r, cy - r, 2 * r, 2 * r);
        if (Rainbow)
            using (var b = new SolidBrush(Color.FromArgb(a, Glass.RainbowAt(val)))) g.FillEllipse(b, kx - r * 0.55f, cy - r * 0.55f, r * 1.1f, r * 1.1f);
    }
}

// Live preview of the glass: the on-screen bar over a sample wallpaper. It reacts to the mouse like the real one.
class GlassPreview : Control {
    Bitmap wall;
    readonly GlassSurface surface = new GlassSurface();
    readonly FrameClock anim = new FrameClock();
    readonly Font font = Ui.F("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 9f, FontStyle.Regular);
    int value = 62;
    bool drag;
    float grow;

    public GlassPreview() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        anim.Tick += delegate {
            bool moving = surface.Light.Step();
            float goal = drag ? 1f : 0f;
            grow = Ui.Toward(grow, goal, 0.25f);
            if (!moving && grow == goal && !GlassCfg.Spinning) anim.Stop();
            Invalidate();
        };
    }

    RectangleF Pill {
        get {
            float w = Math.Min(Width - Ui.P(40), Ui.P(340)), h = Ui.P(56);
            return new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
        }
    }

    RectangleF Row { get { var p = Pill; return new RectangleF(p.X + Ui.P(18), p.Y + (p.Height - Ui.P(28)) / 2f, p.Width - Ui.P(36), Ui.P(28)); } }

    public void Changed() {
        surface.Reset();
        if (GlassCfg.Spinning) anim.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        if (wall == null || wall.Size != ClientSize) {
            if (wall != null) wall.Dispose();
            wall = Ui.SampleText(Math.Max(1, Width), Math.Max(1, Height));
            surface.Reset();
        }
        g.DrawImageUnscaled(wall, 0, 0);
        if (!GlassCfg.On) {
            using (var b = new SolidBrush(Color.FromArgb(150, 20, 20, 22))) g.FillRectangle(b, ClientRectangle);
            Ui.DrawText(g, L.T("Elegí el diseño Liquid Glass para usar estos ajustes", "Choose the Liquid Glass design to use these settings"),
                font, ClientRectangle, Ui.Text, StringAlignment.Center);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var plate = Pill;
        float r = plate.Height / 2f;
        if (surface.NeedsBuild(ClientSize)) surface.Build(wall, plate, r, null);
        using (var path = Ui.RoundRect(plate, r)) {
            surface.Plate(g, path, plate, surface.Lens, false, 1f);
            if (GlassCfg.Light) surface.Lighting(g, path, plate, plate.Height, 1f);
            surface.Edges(g, path, plate, r, false, true, ClientSize);
        }
        Glass.Slider(g, Row, value, grow, font);
    }

    void Wake() { if (!anim.Enabled) anim.Start(); }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (drag) { SetFromX(e.X); }
        var lt = surface.Light;
        lt.Move(e.Location);
        lt.Goal = Pill.Contains(e.Location) ? (drag ? 1f : 0.55f) : 0f;
        Wake();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (!GlassCfg.On || e.Button != MouseButtons.Left) return;
        surface.Light.Goal = 1f;
        var row = Row;
        row.Inflate(0, Ui.P(6));
        if (row.Contains(e.Location)) { drag = true; SetFromX(e.X); }
        Wake();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        drag = false;
        if (GlassCfg.On && Pill.Contains(e.Location)) { surface.Light.FireWave(e.Location); surface.Light.Goal = 0.55f; }
        Wake();
    }

    protected override void OnMouseLeave(EventArgs e) { surface.Light.Goal = 0f; Wake(); }

    void SetFromX(int x) {
        var row = Row;
        value = Math.Max(0, Math.Min(100, (int)Math.Round((x - row.X) * 100 / row.Width)));
        Invalidate();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { anim.Stop(); surface.Clear(); if (wall != null) wall.Dispose(); }
        base.Dispose(disposing);
    }
}

// Glass settings (the "Glass", "Advanced glass" and "RGB edge" sections of Liquid Launch). Changes apply live.
class GlassSettingsForm : Form {
    static readonly Color Bg = Color.FromArgb(32, 32, 34), Field = Color.FromArgb(48, 48, 52);
    readonly Config cfg;
    readonly GlassPreview preview = new GlassPreview();
    readonly List<Action> refreshers = new List<Action>();
    readonly int colW, rowH = Ui.P(34);
    readonly int[] colY = new int[2];
    public event Action Changed;

    public GlassSettingsForm(Config cfg) {
        this.cfg = cfg;
        Text = L.T("LGBrillo · Ajustes del vidrio", "LGBrillo · Glass settings");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Bg;
        ForeColor = Ui.Text;
        Font = Ui.F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular);
        ClientSize = new Size(Ui.P(780), Ui.P(600));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        preview.SetBounds(Ui.P(22), Ui.P(18), ClientSize.Width - Ui.P(44), Ui.P(130));
        Controls.Add(preview);
        colW = (ClientSize.Width - Ui.P(66)) / 2;
        colY[0] = colY[1] = preview.Bottom + Ui.P(16);

        Head(0, L.T("Vidrio", "Glass"));
        Slider(0, L.T("Desenfoque", "Blur"), () => GlassCfg.Blur, v => { GlassCfg.Blur = v; GlassCfg.Rev++; }, Pct, null);
        Slider(0, L.T("Oscurecer", "Darken"), () => GlassCfg.Darken, v => GlassCfg.Darken = v, Pct, null);
        Check(0, L.T("Refracción en el borde", "Edge refraction"), () => GlassCfg.Refract, v => { GlassCfg.Refract = v; GlassCfg.Rev++; }, null);
        Slider(0, L.T("Bisel", "Bezel"), () => GlassCfg.Bezel, v => { GlassCfg.Bezel = v; GlassCfg.Rev++; },
            v => Math.Round(4 + 16 * v) + " dp", () => GlassCfg.Refract);

        Head(1, L.T("Borde RGB", "RGB edge"));
        Check(1, L.T("Borde RGB", "RGB edge"), () => GlassCfg.RgbOn, v => GlassCfg.RgbOn = v, null);
        Slider(1, L.T("Intensidad", "Intensity"), () => GlassCfg.Rgb, v => GlassCfg.Rgb = v, Pct, () => GlassCfg.RgbOn);
        var hue = Slider(1, L.T("Tono", "Hue"), () => GlassCfg.RgbHue, v => GlassCfg.RgbHue = v, v => Math.Round(v * 360) + "°", () => GlassCfg.RgbOn);
        hue.Rainbow = true;
        Check(1, L.T("RGB animado", "Animated RGB"), () => GlassCfg.RgbAnim, v => GlassCfg.RgbAnim = v, () => GlassCfg.RgbOn);
        Slider(1, L.T("Velocidad del giro", "Spin speed"), () => GlassCfg.RgbSpeed, v => GlassCfg.RgbSpeed = v, Pct, () => GlassCfg.Spinning);
        Head(1, L.T("Luz", "Light"));
        Check(1, L.T("Luz que sigue al mouse (cartel de atajos)", "Light that follows the mouse (shortcut bar)"), () => GlassCfg.Light, v => GlassCfg.Light = v, null);
        Wave(1);

        int bottom = Math.Max(colY[0], colY[1]) + Ui.P(12);
        ClientSize = new Size(ClientSize.Width, bottom + Ui.P(56));
        var reset = new Button { Text = L.T("Restablecer", "Reset") };
        var done = new Button { Text = L.T("Listo", "Done") };
        reset.SetBounds(Ui.P(22), ClientSize.Height - Ui.P(50), Ui.P(130), Ui.P(34));
        done.SetBounds(ClientSize.Width - Ui.P(22) - Ui.P(110), reset.Top, Ui.P(110), Ui.P(34));
        Flat(reset, Field);
        Flat(done, Ui.Accent);
        if (Ui.Accent.GetBrightness() > 0.6f) done.ForeColor = Color.Black;
        reset.Click += delegate { GlassCfg.Reset(); Apply(); };
        done.Click += delegate { Close(); };
        Controls.Add(reset);
        Controls.Add(done);
        AcceptButton = done;
        CancelButton = done;
        FormClosed += delegate { GlassCfg.Into(cfg); cfg.Save(); };
        Apply(false);
    }

    static string Pct(float v) { return Math.Round(v * 100) + "%"; }

    // Writes the values to the config, refreshes every control and the preview, and tells the app.
    void Apply(bool notify = true) {
        GlassCfg.Into(cfg);
        foreach (var r in refreshers) r();
        preview.Changed();
        preview.Update(); // paint now, also while a slider is being dragged
        if (notify && Changed != null) Changed();
    }

    Point Cell(int col) { return new Point(Ui.P(22) + col * (colW + Ui.P(22)), colY[col]); }

    void Head(int col, string text) {
        var p = Cell(col);
        if (colY[col] > preview.Bottom + Ui.P(20)) p.Y += Ui.P(6);
        var l = new Label { Text = text, AutoSize = true, ForeColor = Ui.Text2, Location = p };
        Controls.Add(l);
        colY[col] = p.Y + Ui.P(26);
    }

    MiniSlider Slider(int col, string label, Func<float> get, Action<float> set, Func<float, string> fmt, Func<bool> enabled) {
        var p = Cell(col);
        var l = new Label { Text = label, AutoSize = false, ForeColor = Ui.Text, TextAlign = ContentAlignment.MiddleLeft };
        l.SetBounds(p.X, p.Y, Ui.P(150), rowH);
        var val = new Label { AutoSize = false, ForeColor = Ui.Text2, TextAlign = ContentAlignment.MiddleRight };
        val.SetBounds(p.X + colW - Ui.P(52), p.Y, Ui.P(52), rowH);
        var s = new MiniSlider { BackColor = Bg };
        s.SetBounds(p.X + Ui.P(150), p.Y + (rowH - s.Height) / 2, colW - Ui.P(150) - Ui.P(56), s.Height);
        s.ValueChanged += delegate { set(s.Value); Apply(); };
        refreshers.Add(() => {
            s.Value = get();
            val.Text = fmt(get());
            bool on = enabled == null || enabled();
            s.Enabled = on; l.ForeColor = on ? Ui.Text : Ui.Text2;
        });
        Controls.Add(l); Controls.Add(s); Controls.Add(val);
        colY[col] += rowH;
        return s;
    }

    void Check(int col, string label, Func<bool> get, Action<bool> set, Func<bool> enabled) {
        var p = Cell(col);
        var c = new CheckBox { Text = label, AutoSize = true, ForeColor = Ui.Text };
        c.Location = new Point(p.X, p.Y + (rowH - c.PreferredSize.Height) / 2);
        bool busy = false;
        c.CheckedChanged += delegate { if (busy) return; set(c.Checked); Apply(); };
        refreshers.Add(() => { busy = true; c.Checked = get(); busy = false; c.Enabled = enabled == null || enabled(); });
        Controls.Add(c);
        colY[col] += rowH;
    }

    void Wave(int col) {
        var p = Cell(col);
        var l = new Label { Text = L.T("Velocidad de la ola", "Wave speed"), AutoSize = false, ForeColor = Ui.Text, TextAlign = ContentAlignment.MiddleLeft };
        l.SetBounds(p.X, p.Y, Ui.P(150), rowH);
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Ui.Text };
        cb.Items.AddRange(new object[] { L.T("Lenta", "Slow"), L.T("Normal", "Normal"), L.T("Rápida", "Fast") });
        cb.SetBounds(p.X + Ui.P(150), p.Y + Ui.P(3), colW - Ui.P(150), rowH);
        int[] ms = { 1300, 850, 480 };
        bool busy = false;
        cb.SelectedIndexChanged += delegate { if (busy || cb.SelectedIndex < 0) return; GlassCfg.WaveMs = ms[cb.SelectedIndex]; Apply(); };
        refreshers.Add(() => {
            busy = true;
            cb.SelectedIndex = GlassCfg.WaveMs >= 1300 ? 0 : GlassCfg.WaveMs <= 480 ? 2 : 1;
            busy = false;
            cb.Enabled = GlassCfg.Light; l.ForeColor = GlassCfg.Light ? Ui.Text : Ui.Text2;
        });
        Controls.Add(l); Controls.Add(cb);
        colY[col] += rowH;
    }

    static void Flat(Button b, Color bg) {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
        b.FlatAppearance.BorderSize = 1;
        b.BackColor = bg;
        b.ForeColor = Ui.Text;
        b.Cursor = Cursors.Hand;
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Ui.DarkTitle(this);
        Ui.Backdrop(this, 2, false);
    }
}

// Inactivity timeouts of the active Windows power plan (in seconds, 0 = never).
static class Power {
    public static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    static bool Active(out Guid scheme) {
        scheme = Guid.Empty;
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return false;
        scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
        LocalFree(p);
        return true;
    }

    // Value when plugged in; -1 if it couldn't be read.
    public static int Get(Guid sub, Guid setting) {
        Guid s;
        uint v;
        if (!Active(out s) || PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out v) != 0) return -1;
        return (int)v;
    }

    // Writes it for plugged in and battery, and re-applies the plan so Windows uses it right away.
    public static bool Set(Guid sub, Guid setting, int seconds) {
        Guid s;
        if (!Active(out s)) return false;
        uint ok = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, (uint)seconds);
        PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, (uint)seconds);
        return ok == 0 && PowerSetActiveScheme(IntPtr.Zero, ref s) == 0;
    }

    // Menu options: "Never" at the top.
    public static readonly int[] Times = { 0, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000 };

    public static string Name(int s) {
        if (s < 0) return "—";
        if (s == 0) return L.T("Nunca", "Never");
        if (s % 3600 == 0) return s / 3600 + " h";
        if (s % 60 == 0) return s / 60 + " min";
        return s + " s";
    }

    // The Control Panel "Edit Plan Settings" page.
    public static void OpenPlanSettings() {
        try { System.Diagnostics.Process.Start("control.exe", "/name Microsoft.PowerOptions /page pagePlanSettings"); }
        catch { System.Diagnostics.Process.Start("control.exe", "powercfg.cpl"); }
    }
}

// Windows HDR ("advanced color") of the display: read it and turn it on or off, like the switch in Windows Settings.
static class Hdr {
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PathSource { public Luid Adapter; public uint Id, ModeIdx, Status; }
    [StructLayout(LayoutKind.Sequential)] struct PathTarget {
        public Luid Adapter; public uint Id, ModeIdx, Tech, Rotation, Scaling, RefreshN, RefreshD, ScanLine; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct ModeInfo { public uint Type; }
    [StructLayout(LayoutKind.Sequential)] struct AdvancedColor { public uint Type, Size; public Luid Adapter; public uint Id; public uint Value, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)] struct SetColor { public uint Type, Size; public Luid Adapter; public uint Id; public uint Value; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] paths, ref uint nm, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetColor info);

    static PathInfo[] Paths() {
        uint np, nm;
        if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return new PathInfo[0]; // QDC_ONLY_ACTIVE_PATHS
        var paths = new PathInfo[np];
        var modes = new ModeInfo[nm];
        if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return new PathInfo[0];
        return paths;
    }

    static uint Info(PathInfo p) {
        var a = new AdvancedColor { Type = 9, Size = (uint)Marshal.SizeOf(typeof(AdvancedColor)), Adapter = p.Target.Adapter, Id = p.Target.Id }; // GET_ADVANCED_COLOR_INFO
        return DisplayConfigGetDeviceInfo(ref a) == 0 ? a.Value : 0;
    }

    // Bit 0: the display can do HDR. Bit 1: HDR is on.
    public static bool Supported() { try { foreach (var p in Paths()) if ((Info(p) & 1) != 0) return true; } catch { } return false; }
    public static bool Enabled() { try { foreach (var p in Paths()) if ((Info(p) & 2) != 0) return true; } catch { } return false; }

    // Turns HDR on or off on every display that supports it. Returns false if Windows refused.
    public static bool Set(bool on) {
        bool ok = false;
        try {
            foreach (var p in Paths()) {
                if ((Info(p) & 1) == 0) continue;
                var s = new SetColor { Type = 10, Size = (uint)Marshal.SizeOf(typeof(SetColor)), Adapter = p.Target.Adapter, Id = p.Target.Id, Value = on ? 1u : 0u }; // SET_ADVANCED_COLOR_STATE
                int r = DisplayConfigSetDeviceInfo(ref s);
                if (r != 0) { s.Type = 16; r = DisplayConfigSetDeviceInfo(ref s); } // SET_HDR_STATE (Windows 11 24H2)
                if (r == 0) ok = true;
                else Log.Write("hdr switch refused by Windows: " + r);
            }
        } catch (Exception ex) { Log.Write("hdr switch failed: " + ex.Message); }
        return ok;
    }
}

class DarkMenuColors : ProfessionalColorTable {
    static readonly Color Bg = Color.FromArgb(38, 38, 40), Edge = Color.FromArgb(62, 62, 66), Sel = Color.FromArgb(58, 58, 64);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Sel; } }
    public override Color MenuItemSelected { get { return Sel; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Bg; } }
    public override Color CheckBackground { get { return Sel; } }
    public override Color CheckSelectedBackground { get { return Sel; } }
    public override Color CheckPressedBackground { get { return Sel; } }
}

// Field that captures a key combination (with at least one modifier, or an F key).
class HotkeyBox : TextBox {
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    public int Mods;
    public Keys Key;

    public HotkeyBox() { ReadOnly = true; ShortcutsEnabled = false; Cursor = Cursors.Hand; TextAlign = HorizontalAlignment.Center; }

    public void Set(int mods, Keys k) { Mods = mods; Key = k; Text = Describe(mods, k); }

    public static string Describe(int m, Keys k) {
        var parts = new List<string>();
        if ((m & 8) != 0) parts.Add("Win");
        if ((m & 2) != 0) parts.Add("Ctrl");
        if ((m & 1) != 0) parts.Add("Alt");
        if ((m & 4) != 0) parts.Add("Shift");
        string name;
        switch (k) {
            case Keys.Up: name = "↑"; break;
            case Keys.Down: name = "↓"; break;
            case Keys.Left: name = "←"; break;
            case Keys.Right: name = "→"; break;
            case Keys.PageUp: name = "RePág"; break;
            case Keys.PageDown: name = "AvPág"; break;
            case Keys.Oemplus: name = "+"; break;
            case Keys.OemMinus: name = "-"; break;
            case Keys.Add: name = "Num +"; break;
            case Keys.Subtract: name = "Num -"; break;
            default:
                name = k >= Keys.D0 && k <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString();
                break;
        }
        if (k == Keys.PageUp || k == Keys.PageDown) name = L.T(name, k == Keys.PageUp ? "PgUp" : "PgDn");
        parts.Add(name);
        return string.Join(" + ", parts.ToArray());
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        Keys k = keyData & Keys.KeyCode;
        if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return true;
        int m = 0;
        if ((keyData & Keys.Control) != 0) m |= 2;
        if ((keyData & Keys.Alt) != 0) m |= 1;
        if ((keyData & Keys.Shift) != 0) m |= 4;
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) m |= 8;
        bool fkey = k >= Keys.F1 && k <= Keys.F24;
        if ((m & ~4) == 0 && !fkey) { System.Media.SystemSounds.Beep.Play(); return true; } // needs Ctrl, Alt or Win
        Set(m, k);
        return true;
    }
}

class Hotkeys : NativeWindow {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    public const uint Alt = 1, Ctrl = 2;
    readonly List<int> ids = new List<int>();
    public event Action<int> Pressed;

    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid setting, int flags);
    public event Action<bool> DisplayPower; // the display turned on (true) or off

    public Hotkeys() {
        CreateHandle(new CreateParams());
        var consoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        try { RegisterPowerSettingNotification(Handle, ref consoleDisplayState, 0); } catch { }
    }

    public void Clear() {
        foreach (int id in ids) UnregisterHotKey(Handle, id);
        ids.Clear();
    }

    public bool Add(int id, uint mod, Keys k) {
        if (!RegisterHotKey(Handle, id, mod, (uint)k)) return false;
        ids.Add(id);
        return true;
    }

    public void Release() {
        foreach (int id in ids) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312 && Pressed != null) Pressed(m.WParam.ToInt32()); // WM_HOTKEY
        // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE: the state is the first data byte, after the GUID and the length.
        if (m.Msg == 0x0218 && m.WParam.ToInt32() == 0x8013 && m.LParam != IntPtr.Zero && DisplayPower != null)
            DisplayPower(Marshal.ReadInt32(m.LParam, 20) != 0);
        base.WndProc(ref m);
    }
}

class App : ApplicationContext {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Config cfg = Config.Load();
    readonly Tv tv = new Tv();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Popup popup = new Popup();
    readonly Osd osd = new Osd();
    readonly Hotkeys hk = new Hotkeys();
    readonly System.Windows.Forms.Timer retry = new System.Windows.Forms.Timer { Interval = 15000 };
    int target = -1, applied = -1;
    bool applying, connecting;
    string lastError = "";
    DateTime lostAt = DateTime.MinValue, lastSet = DateTime.MinValue, lastDiscover = DateTime.MinValue, fastUntil = DateTime.MinValue;
    int pendingDelta;
    bool stepWaiting, displayOn = true;
    bool announce; // the pending change came from a shortcut: keep the on-screen bar informed until it is applied
    SettingsForm settings;
    GlassSettingsForm glassForm;
    string designShown;

    public App() {
        GlassCfg.From(cfg);
        ApplyConfig();
        tray.Icon = Icon.FromHandle(Ui.Sun(SystemInformation.SmallIconSize.Width, Color.White).GetHicon());
        tray.Text = L.T("Brillo del monitor", "Monitor brightness");
        tray.ContextMenuStrip = BuildMenu();
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        tray.Visible = true;

        popup.SetTitle(cfg.Name == "" ? L.T("Monitor LG", "LG monitor") : cfg.Name, Resolution());
        popup.SetButton(0, cfg.EyeCare);
        popup.SetButton(1, cfg.PixelRefresh);
        RefreshTimes();
        SystemEvents.DisplaySettingsChanged += delegate { osd.ResetWindow(); popup.ResetWindow(); RefreshHdr(); };
        popup.HdrClicked += ToggleWindowsHdr;
        RefreshHdr();
        SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) { Log.Write("pc resumed"); Wake(); } };
        hk.DisplayPower += on => {
            if (on == displayOn) return;
            displayOn = on;
            Log.Write(on ? "display on" : "display off");
            if (on) Wake();
        };
        popup.SliderMoved += v => Want(v, false);
        osd.SliderMoved += v => Want(v, false);
        popup.ButtonClicked += OnButton;
        popup.ChoiceClicked += OnChoice;

        tv.BacklightChanged += OnBacklight;
        tv.Disconnected += delegate { Log.Write("connection lost"); lostAt = DateTime.UtcNow; SetStatus(L.T("Sin conexión, reintentando…", "Disconnected, retrying…"), false); };
        tv.PairingPrompt += delegate {
            SetStatus(L.T("Aceptá el aviso en el monitor", "Accept the prompt on the monitor"), false);
            tray.ShowBalloonTip(15000, L.T("Brillo del monitor", "Monitor brightness"), L.T("Aceptá el aviso que apareció en el monitor (con el control remoto). Es una sola vez.", "Accept the prompt that appeared on the monitor (with the remote). Only needed once."), ToolTipIcon.Info);
        };

        hk.Pressed += id => Step(id == 1 ? cfg.Paso : -cfg.Paso);
        RegisterHotkeys();

        retry.Tick += async delegate {
            retry.Interval = DateTime.UtcNow < fastUntil ? 3000 : 15000;
            if (!tv.Connected) await EnsureConnected();
        };
        fastUntil = DateTime.UtcNow.AddSeconds(60); // at startup the network may not be up yet
        retry.Interval = 3000;
        retry.Start();
        var _ = EnsureConnected();
    }

    // The display or the PC woke up: the old socket is usually dead and the monitor takes a while to come back.
    // Check now and retry quickly for a minute, instead of finding out on the first key press.
    async void Wake() {
        osd.ResetWindow();
        popup.ResetWindow();
        fastUntil = DateTime.UtcNow.AddSeconds(60);
        retry.Interval = 3000;
        await Task.Delay(1000);
        if (tv.Connected && !await tv.Alive()) Log.Write("connection was dead after waking up");
        if (!tv.Connected) await EnsureConnected();
    }

    void RegisterHotkeys() {
        hk.Clear();
        bool ok = hk.Add(1, (uint)cfg.UpMods, (Keys)cfg.UpKey);
        ok &= hk.Add(2, (uint)cfg.DownMods, (Keys)cfg.DownKey);
        if (!ok) {
            string keys = HotkeyBox.Describe(cfg.UpMods, (Keys)cfg.UpKey) + " / " + HotkeyBox.Describe(cfg.DownMods, (Keys)cfg.DownKey);
            tray.ShowBalloonTip(8000, L.T("Brillo del monitor", "Monitor brightness"),
                L.T("Otro programa ya usa ", "Another app already uses ") + keys + L.T(". Elegí otro atajo en Configuración.", ". Pick another shortcut in Settings."), ToolTipIcon.Warning);
        }
    }

    void ApplyConfig() {
        GlassCfg.From(cfg);
        if (designShown != null && designShown != cfg.Design) { popup.ResetDesign(); osd.ResetDesign(); }
        else { popup.GlassChanged(); osd.GlassChanged(); }
        designShown = cfg.Design;
        popup.SetRows(!cfg.HideScreenOff, !cfg.HideSleep, !cfg.HidePlan);
        Ui.Accent = Ui.ParseColor(cfg.Color);
        osd.Seconds = cfg.OsdSegundos;
        osd.Position = cfg.OsdPos;
        popup.Invalidate();
        osd.Invalidate();
    }

    static string Resolution() {
        var b = Screen.PrimaryScreen.Bounds;
        return b.Width + "×" + b.Height;
    }

    ContextMenuStrip BuildMenu() {
        var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ForeColor = Ui.Text, ShowImageMargin = true };
        m.Items.Add(L.T("Abrir LGBrillo", "Open LGBrillo"), null, delegate { TogglePopup(); });
        m.Items.Add(L.T("Configuración…", "Settings…"), null, delegate { OpenSettings(); });
        var design = new ToolStripMenuItem(L.T("Diseño", "Design"));
        var win11 = new ToolStripMenuItem("Windows 11");
        var liquid = new ToolStripMenuItem("Liquid Glass");
        win11.Click += delegate { SetDesign("win11"); };
        liquid.Click += delegate { SetDesign("glass"); };
        design.DropDownItems.Add(win11);
        design.DropDownItems.Add(liquid);
        design.DropDownItems.Add(new ToolStripSeparator());
        design.DropDownItems.Add(L.T("Ajustes del vidrio…", "Glass settings…"), null, delegate { OpenGlassSettings(); });
        foreach (ToolStripItem it in design.DropDownItems) it.ForeColor = Ui.Text;
        ((ToolStripDropDownMenu)design.DropDown).Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        m.Items.Add(design);
        m.Items.Add(new ToolStripSeparator());
        var start = new ToolStripMenuItem(L.T("Iniciar con Windows", "Start with Windows"));
        start.Click += delegate { SetStartup(!IsStartup()); };
        m.Items.Add(start);
        m.Items.Add(L.T("Buscar el monitor de nuevo", "Find the monitor again"), null, async delegate {
            cfg.Ip = "";
            tv.Close();
            await EnsureConnected();
        });
        m.Items.Add(L.T("Abrir registro de diagnóstico", "Open diagnostic log"), null, delegate {
            Log.Write("log opened from the menu");
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { }
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(L.T("Salir", "Quit"), null, delegate { Quit(); });
        m.Opening += delegate {
            start.Checked = IsStartup(); win11.Checked = cfg.Design != "glass"; liquid.Checked = cfg.Design == "glass";
        };
        foreach (ToolStripItem it in m.Items) it.ForeColor = Ui.Text;
        return m;
    }

    void TogglePopup() {
        if (popup.Visible) { popup.Hide(); return; }
        if (unchecked(Environment.TickCount - popup.HiddenAt) < 250) return; // the click that closed it
        RefreshTimes();
        RefreshHdr();
        popup.ShowNear(Cursor.Position);
        if (!tv.Connected) { var _ = EnsureConnected(); }
        else RefreshInput();
    }

    void SetDesign(string d) {
        if (cfg.Design == d) return;
        cfg.Design = d;
        cfg.Save();
        Log.Write("design " + d);
        ApplyConfig();
    }

    void OpenGlassSettings() {
        popup.Hide();
        if (glassForm != null && !glassForm.IsDisposed) { glassForm.Activate(); return; }
        glassForm = new GlassSettingsForm(cfg);
        glassForm.Changed += delegate { popup.GlassChanged(); osd.GlassChanged(); };
        glassForm.Show();
        glassForm.Activate();
    }

    void OpenSettings() {
        popup.Hide();
        if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
        settings = new SettingsForm(cfg, IsStartup());
        var f = settings;
        f.AdjustGlass += OpenGlassSettings;
        f.Saved += ipChanged => {
            SetStartup(f.Startup);
            ApplyConfig();
            RegisterHotkeys();
            if (ipChanged) { tv.Close(); var _ = EnsureConnected(); }
        };
        f.Show();
        f.Activate();
    }

    void SetStatus(string s, bool ok) { popup.SetStatus(s, ok); }

    void ShowValue(int v) {
        popup.SetValue(v);
        if (osd.Visible) osd.SetValue(v);
        tray.Text = L.T("Brillo del monitor: ", "Monitor brightness: ") + v;
    }

    // The monitor reports every change, including ours. While we are sending, and right after, those reports can be
    // older than the value just requested: taking them would move the bar backwards and lose a key press.
    void OnBacklight(int v) {
        if (applying) return;
        if (v != target && (DateTime.UtcNow - lastSet).TotalMilliseconds < 800) return;
        applied = v;
        target = v;
        ShowValue(v);
    }

    // A shortcut was pressed. The on-screen bar always answers: with the new value, or with why there isn't one.
    async void Step(int delta) {
        int b = target >= 0 ? target : applied;
        if (b >= 0) {
            Log.Write("hotkey " + delta.ToString("+0;-0") + " -> " + Math.Max(0, Math.Min(100, b + delta)) + (tv.Connected ? "" : " (not connected)"));
            Want(b + delta, true);
            return;
        }
        // The brightness isn't known yet (just started, or the monitor was off): say so, connect, then apply the presses.
        pendingDelta += delta;
        osd.ShowMessage(L.T("Conectando con el monitor…", "Connecting to the monitor…"));
        if (stepWaiting) return;
        stepWaiting = true;
        Log.Write("hotkey " + delta.ToString("+0;-0") + " with unknown brightness, connecting");
        try {
            announce = true;
            bool ok = await ConnectPatiently();
            b = target >= 0 ? target : applied;
            if (ok && b < 0) { // connected, but the monitor never reported its brightness: ask for it
                b = await tv.GetBacklight();
                if (b >= 0) { applied = b; target = b; }
            }
            if (ok && b >= 0) Want(b + pendingDelta, true);
            else {
                Log.Write("hotkey not applied: " + (ok ? "the monitor did not report its brightness" : lastError));
                osd.ShowMessage(ok ? L.T("El monitor no informó su brillo", "The monitor didn't report its brightness") : Unreachable);
            }
        } finally { pendingDelta = 0; stepWaiting = false; if (!applying) announce = false; }
    }

    void Want(int v, bool fromHotkey) {
        v = Math.Max(0, Math.Min(100, v));
        target = v;
        ShowValue(v);
        if (fromHotkey) { announce = true; osd.ShowValue(v); }
        if (!applying) Apply();
    }

    // Always sends the latest requested value; values in between are skipped while one is in flight.
    async void Apply() {
        applying = true;
        try {
            while (target >= 0 && target != applied) {
                if (!await ConnectPatiently()) { NotApplied(); break; }
                int v = target;
                try {
                    await tv.SetBacklight(v);
                    applied = v;
                    lastSet = DateTime.UtcNow;
                    if (osd.ShowingMessage) osd.ShowValue(target); // it was saying "Connecting…"
                }
                catch (Exception ex) { lastError = L.T("No se pudo cambiar: ", "Could not change it: ") + ex.Message; NotApplied(); break; }
            }
        } finally { applying = false; announce = false; }
    }

    // Connects, insisting for a while: right after waking up, the monitor needs some seconds before it accepts connections.
    // While a shortcut is waiting for it, the on-screen bar says so instead of showing a value that isn't applied yet.
    async Task<bool> ConnectPatiently() {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!await EnsureConnected()) {
            if (DateTime.UtcNow > until) return false;
            if (announce) osd.ShowMessage(L.T("Conectando con el monitor…", "Connecting to the monitor…"));
            await Task.Delay(1000);
        }
        return true;
    }

    static string Unreachable { get { return L.T("Sin conexión con el monitor", "Can't reach the monitor"); } }

    // The change didn't reach the monitor: say why and go back to showing the brightness it really has.
    void NotApplied() {
        Log.Write("brightness " + target + " not applied: " + lastError);
        SetStatus(lastError, false);
        if (osd.Visible) osd.ShowMessage(Unreachable);
        if (applied >= 0) {
            target = applied;
            popup.SetValue(applied);
            tray.Text = L.T("Brillo del monitor: ", "Monitor brightness: ") + applied;
        }
    }

    List<Tv.Input> inputs = new List<Tv.Input>();
    string currentApp = "";

    string CurrentInput() {
        foreach (var inp in inputs) if (inp.AppId == currentApp) return inp.Name;
        return currentApp == "" ? "—" : "webOS";
    }

    async Task<bool> LoadInputs() {
        try {
            inputs = await tv.GetInputs();
            currentApp = await tv.GetForegroundApp();
            popup.SetInput(CurrentInput());
            return true;
        } catch { return false; }
    }

    async void RefreshInput() { if (tv.Connected) await LoadInputs(); }

    // Expands the list of inputs inside the panel.
    async void ExpandInputs() {
        if (!await EnsureConnected()) return;
        if (!await LoadInputs()) { SetStatus(L.T("No pude leer las entradas del monitor", "Could not read the monitor's inputs"), false); return; }
        var list = new List<Popup.Choice>();
        foreach (var inp in inputs)
            list.Add(new Popup.Choice { Text = inp.Connected ? inp.Name : inp.Name + L.T(" · sin señal", " · no signal"), Selected = inp.AppId == currentApp, Dim = !inp.Connected });
        popup.Expand(Popup.ExpandInput, list);
    }

    List<int> timeValues = new List<int>();

    // Expands the Windows inactivity times inside the panel ("Never" first), with the current one marked.
    void ExpandTimes(int row) {
        int current = row == Popup.RowScreen ? Power.Get(Power.SubVideo, Power.VideoIdle) : Power.Get(Power.SubSleep, Power.StandbyIdle);
        timeValues = new List<int>(Power.Times);
        if (current > 0 && !timeValues.Contains(current)) { timeValues.Add(current); timeValues.Sort(); timeValues.Remove(0); timeValues.Insert(0, 0); }
        var list = new List<Popup.Choice>();
        foreach (int s in timeValues) list.Add(new Popup.Choice { Text = Power.Name(s), Selected = s == current });
        popup.Expand(row, list);
    }

    // An option of an expanded list was chosen.
    async void OnChoice(int which, int index) {
        if (which == Popup.ExpandInput) {
            if (index < 0 || index >= inputs.Count) return;
            string id = inputs[index].Id;
            popup.Collapse();
            try { await tv.SwitchInput(id); }
            catch (Exception ex) { SetStatus(L.T("No se pudo cambiar la entrada: ", "Could not switch input: ") + ex.Message, false); return; }
            await Task.Delay(1200);
            RefreshInput();
            return;
        }
        if (index < 0 || index >= timeValues.Count) return;
        bool ok = which == Popup.RowScreen ? Power.Set(Power.SubVideo, Power.VideoIdle, timeValues[index]) : Power.Set(Power.SubSleep, Power.StandbyIdle, timeValues[index]);
        if (!ok) tray.ShowBalloonTip(5000, "LGBrillo", L.T("Windows no dejó cambiar ese tiempo.", "Windows did not allow changing that time."), ToolTipIcon.Warning);
        RefreshTimes();
        popup.Collapse();
    }

    void RefreshHdr() { popup.SetHdr(Hdr.Supported(), Hdr.Enabled()); }

    // The HDR button of the panel: switches Windows HDR, then shows the state Windows really ended up in.
    async void ToggleWindowsHdr() {
        bool want = !Hdr.Enabled();
        popup.SetHdr(true, want);
        bool ok = Hdr.Set(want);
        Log.Write("windows hdr " + (want ? "on" : "off") + (ok ? "" : " (refused)"));
        await Task.Delay(ok ? 900 : 100);
        RefreshHdr();
        if (!ok) tray.ShowBalloonTip(5000, "LGBrillo", L.T("Windows no dejó cambiar el HDR.", "Windows did not allow changing HDR."), ToolTipIcon.Warning);
    }

    void RefreshTimes() {
        popup.SetTimes(Power.Name(Power.Get(Power.SubVideo, Power.VideoIdle)), Power.Name(Power.Get(Power.SubSleep, Power.StandbyIdle)));
    }

    // 0 = Eye Care, 1 = Pixel Refresh (both on the monitor), 2 = video input; Windows power rows.
    async void OnButton(int i) {
        if (i == 2 || i == Popup.RowScreen || i == Popup.RowSleep) {
            if (popup.Expanded == i) { popup.Collapse(); return; }
            if (i == 2) ExpandInputs(); else ExpandTimes(i);
            return;
        }
        if (i == Popup.RowPlan) { popup.Hide(); Power.OpenPlanSettings(); return; }
        bool want = !popup.IsOn(i);
        popup.SetButton(i, want);
        string error = null;
        if (!await EnsureConnected()) error = lastError;
        else {
            try {
                if (i == 0) await tv.SetEyeCare(want);
                else await tv.SetPixelRefresh(want);
            } catch (Exception ex) { error = L.T("No se pudo cambiar: ", "Could not change it: ") + ex.Message; }
        }
        if (error != null) {
            popup.SetButton(i, !want);
            SetStatus(error, false);
            return;
        }
        if (i == 0) cfg.EyeCare = want; else cfg.PixelRefresh = want;
        cfg.Save();
    }

    async Task<bool> EnsureConnected() {
        if (tv.Connected) return true;
        if (connecting) {
            while (connecting) await Task.Delay(100);
            return tv.Connected;
        }
        connecting = true;
        try {
            SetStatus(L.T("Conectando…", "Connecting…"), false);
            bool ok = cfg.Ip != "" && await TryConnect(cfg.Ip);
            // Search the network when there is no address, and otherwise at most twice a minute (the quick retries would flood it).
            if (!ok && (cfg.Ip == "" || (DateTime.UtcNow - lastDiscover).TotalSeconds > 30)) {
                lastDiscover = DateTime.UtcNow;
                SetStatus(L.T("Buscando el monitor en la red…", "Searching the network for the monitor…"), false);
                string ip = await Tv.Discover();
                if (ip == null) lastError = L.T("No encuentro el monitor (¿está prendido?)", "Monitor not found (is it on?)");
                else if (ip != cfg.Ip) {
                    ok = await TryConnect(ip);
                    if (ok) { cfg.Ip = ip; cfg.Save(); }
                }
            }
            if (!ok && lastError == "") lastError = L.T("Sin conexión con el monitor", "Not connected to the monitor");
            SetStatus(ok ? L.T("Conectado", "Connected") : lastError, ok);
            if (ok) OnReconnected();
            return ok;
        } finally { connecting = false; }
    }

    void OnReconnected() {
        // If the monitor was off for a while, the scheduled Pixel Refresh already ran.
        if (cfg.PixelRefresh && lostAt != DateTime.MinValue && DateTime.UtcNow - lostAt > TimeSpan.FromMinutes(2)) {
            cfg.PixelRefresh = false;
            cfg.Save();
            popup.SetButton(1, false);
        }
        lostAt = DateTime.MinValue;
        Log.Write("connected, brightness " + applied);
        if (cfg.Name == "") FetchName();
        RefreshInput();
    }

    async void FetchName() {
        try {
            using (var wc = new WebClient()) {
                string xml = await wc.DownloadStringTaskAsync("http://" + cfg.Ip + ":1661/");
                var m = Regex.Match(xml, "<modelNumber>([^<-]+)");
                if (!m.Success) return;
                cfg.Name = "LG " + m.Groups[1].Value.Trim();
                cfg.Save();
                popup.SetTitle(cfg.Name, Resolution());
            }
        } catch { }
    }

    async Task<bool> TryConnect(string ip) {
        try {
            tv.Ip = ip;
            tv.Key = cfg.Key;
            await tv.ConnectAsync();
            if (tv.Key != cfg.Key) { cfg.Key = tv.Key; cfg.Save(); }
            return true;
        } catch (Exception ex) {
            string msg = L.T("Sin conexión: ", "Not connected: ") + ex.Message;
            if (msg != lastError) Log.Write("connect failed: " + ex.Message);
            lastError = msg;
            return false;
        }
    }

    static bool IsStartup() {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LGBrillo") != null;
    }

    static void SetStartup(bool on) {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
            if (on) k.SetValue("LGBrillo", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("LGBrillo", false);
        }
    }

    void Quit() {
        retry.Stop();
        tray.Visible = false;
        hk.Release();
        tv.Close();
        ExitThread();
    }
}

}
