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

namespace LGBrillo {

static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args) {
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
            Application.Run(new App());
        }
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

    public Config() { Ip = ""; Key = ""; Paso = 5; Name = ""; OsdSegundos = 1.5; OsdPos = "abajo"; Color = "#0A84FF";
        UpMods = 3; UpKey = (int)Keys.Up; DownMods = 3; DownKey = (int)Keys.Down; }

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
        var loop = ReceiveLoop(s);

        var p = new Dictionary<string, object> {
            { "forcePairing", false }, { "pairingType", "PROMPT" }, { "manifest", Manifest.Get() }
        };
        if (Key != "") p["client-key"] = Key;
        await Send(new Dictionary<string, object> { { "type", "register" }, { "id", "reg0" }, { "payload", p } });

        var reg = regTcs.Task;
        if (await Task.WhenAny(reg, Task.Delay(60000)) != reg) { Close(); throw new TimeoutException(L.T("no se aceptó el aviso en el monitor", "the prompt on the monitor was not accepted")); }
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
            else if (Str(payload, "pairingType") == "PROMPT") PromptLater();
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
        TaskCompletionSource<Dictionary<string, object>> t;
        if (id != null && pend.TryGetValue(id, out t)) {
            pend.Remove(id);
            if (type == "error") t.TrySetException(new Exception(Str(m, "error") ?? "error"));
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

    async Task<Dictionary<string, object>> Request(string uri, object payload) {
        string id = "r" + (++nextId);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        pend[id] = tcs;
        await Send(new Dictionary<string, object> { { "type", "request" }, { "id", id }, { "uri", uri }, { "payload", payload } });
        if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) != tcs.Task) {
            pend.Remove(id);
            Drop();
            throw new TimeoutException(L.T("el monitor no respondió", "the monitor did not respond"));
        }
        return await tcs.Task;
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

    // setSystemSettings isn't open over SSAP: luna is called through an alert that closes instantly.
    public Task SetBacklight(int v) { return SetSetting("picture", "backlight", v); }

    // Eye Care (low blue light). It can't be read over SSAP, only written.
    public Task SetEyeCare(bool on) { return SetSetting("picture", "eyeComfortMode", on ? "on" : "off"); }

    // Pixel Refresh (OLED pixel cleaning) the next time it turns off. It can't be read either.
    public Task SetPixelRefresh(bool on) { return SetSetting("picture", "scheduledPixelCleaning", on ? "on" : "off"); }

    async Task SetSetting(string category, string key, object value) {
        var prm = new Dictionary<string, object> {
            { "category", category }, { "settings", new Dictionary<string, object> { { key, value } } }
        };
        var act = new Dictionary<string, object> { { "uri", LunaSet }, { "params", prm } };
        var r = await Request("ssap://system.notifications/createAlert", new Dictionary<string, object> {
            { "message", " " },
            { "buttons", new object[] { new Dictionary<string, object> { { "label", "" }, { "onClick", LunaSet }, { "params", prm } } } },
            { "onclose", act }, { "onfail", act }
        });
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
        using (var sun = Sun(s1, Color.FromArgb(150, 150, 156))) g.DrawImage(sun, row.X, row.Y + (row.Height - s1) / 2f);
        using (var sun = Sun(s2, Color.FromArgb(205, 205, 210))) g.DrawImage(sun, row.Right - s2, row.Y + (row.Height - s2) / 2f);
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

    public static float Toward(float cur, float goal, float k) {
        float n = cur + (goal - cur) * k;
        return Math.Abs(goal - n) < 0.002f ? goal : n;
    }
}

// Dark frosted panel with a brightness row; base of the tray panel and the shortcut bar.
abstract class DarkPanel : Form {
    protected Bitmap frost;
    protected int value = -1;
    protected bool drag, hoverSlider;
    protected float grow;
    protected readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
    protected bool acrylic;
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
        anim.Tick += delegate { if (!Animate()) anim.Stop(); Invalidate(); };
    }

    protected abstract RectangleF SliderRow { get; }
    protected virtual void ItemHover(Point p) { }
    protected virtual void ItemClick(Point p) { }

    // Steps the animations; returns false when they're all done.
    protected virtual bool Animate() {
        float goal = drag ? 1f : (hoverSlider ? 0.5f : 0f);
        grow = Ui.Toward(grow, goal, 0.25f);
        return grow != goal;
    }

    // Windows 11 draws real Acrylic blur; on Windows 10 it's imitated with a blurred picture.
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Ui.Round(this);
        acrylic = Ui.Backdrop(this, 3, true);
    }

    // Without Acrylic, takes the background picture before showing, so the window isn't in it.
    protected void ShowFrosted(Point at) {
        Location = at;
        if (!IsHandleCreated) CreateHandle();
        if (!acrylic) {
            if (frost != null) frost.Dispose();
            frost = Ui.Frost(new Rectangle(at, Size));
        }
        Show();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) {
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

    bool OnSlider(Point p) {
        var r = SliderRow;
        r.Inflate(0, Ui.P(6));
        return r.Contains(p);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        if (OnSlider(e.Location)) { drag = true; anim.Start(); SlideTo(Ui.ValueAt(SliderRow, e.X)); }
        else ItemClick(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (drag) { SlideTo(Ui.ValueAt(SliderRow, e.X)); return; }
        bool h = OnSlider(e.Location);
        if (h != hoverSlider) { hoverSlider = h; anim.Start(); }
        ItemHover(e.Location);
        if (h) Cursor = Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e) { drag = false; anim.Start(); }
    protected override void OnMouseLeave(EventArgs e) { hoverSlider = false; anim.Start(); ItemHover(new Point(-1, -1)); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (value >= 0) SlideTo(value + (e.Delta > 0 ? 2 : -2)); }

    protected void PaintSlider(Graphics g) { Ui.SliderRow(g, SliderRow, value, grow); }
}

// Panel that opens when you click the tray icon.
class Popup : DarkPanel {
    static readonly string[] Labels = { "Eye Care", "Pixel Refresh", L.T("Entrada", "Input") };
    readonly bool[] on = new bool[3];
    int hot = -1;
    string title = L.T("Monitor LG", "LG monitor"), sub = "", status = L.T("Conectando…", "Connecting…"), input = "—";
    string screenOff = "—", sleep = "—";
    public const int RowScreen = 10, RowSleep = 11, RowPlan = 12;
    bool connected, hdr;
    public int HiddenAt;
    public event Action<int> ButtonClicked;
    public bool MenuOpen;

    public Popup() {
        ClientSize = new Size(Ui.P(300), Ui.P(318));
        KeyPreview = true;
        Deactivate += delegate { if (MenuOpen) return; Hide(); HiddenAt = Environment.TickCount; };
    }

    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW: not in Alt+Tab
    }

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

    // Bottom rows, stacked according to which ones are visible.
    float RowY(int item) {
        int n = 0;
        if (item != RowScreen && rows[0]) n++;
        if (item == RowPlan && rows[1]) n++;
        return 206 + 36 * n + (item == RowPlan && n > 0 ? 4 : 0);
    }

    RectangleF Row(int item) {
        return new RectangleF(Ui.P(8), Ui.P(RowY(item)), W - Ui.P(16), Ui.P(item == RowPlan ? 30 : 34));
    }

    public void SetRows(bool screen, bool sl, bool plan) {
        rows[0] = screen; rows[1] = sl; rows[2] = plan;
        float bottom = 198;
        foreach (int r in new[] { RowScreen, RowSleep, RowPlan })
            if (RowOn(r)) bottom = RowY(r) + (r == RowPlan ? 30 : 34) + 6;
        ClientSize = new Size(ClientSize.Width, Ui.P(bottom));
        Invalidate();
    }

    public bool IsOn(int i) { return on[i]; }
    public void SetTimes(string screen, string sl) { screenOff = screen; sleep = sl; Invalidate(); }
    public void SetButton(int i, bool v) { on[i] = v; Invalidate(); }
    public void SetTitle(string t, string s) { title = t; sub = s; Invalidate(); }
    public void SetStatus(string s, bool ok) { status = s; connected = ok; Invalidate(); }
    public void SetHdr(bool v) { hdr = v; Invalidate(); }
    public void SetInput(string name) { input = name; Invalidate(); }

    // Shows a menu under a button or row, keeping the panel open while the menu is open.
    public void ShowMenu(int i, ContextMenuStrip m) {
        MenuOpen = true;
        m.Closed += delegate {
            MenuOpen = false;
            if (Form.ActiveForm != this) { Hide(); HiddenAt = Environment.TickCount; }
        };
        if (i < Labels.Length) {
            var c = Circle(i);
            m.Show(this, new Point((int)c.X - Ui.P(20), (int)c.Bottom + Ui.P(6)));
        } else {
            var r = Row(i);
            m.Show(this, new Point((int)r.Right - m.GetPreferredSize(Size.Empty).Width, (int)r.Bottom + Ui.P(2)));
        }
    }

    public void ShowNear(Point p) {
        var wa = Screen.FromPoint(p).WorkingArea;
        int m = Ui.P(12);
        int x = Math.Max(wa.Left + m, Math.Min(p.X - Width / 2, wa.Right - Width - m));
        int y = p.Y >= wa.Bottom ? wa.Bottom - Height - m : (p.Y <= wa.Top ? wa.Top + m : Math.Max(wa.Top + m, p.Y - Height - m));
        ShowFrosted(new Point(x, y));
        Activate();
    }

    int Hit(Point p) {
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
        if (h >= 0 && ButtonClicked != null) ButtonClicked(h);
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
            case Keys.Escape: Hide(); return true;
        }
        return base.ProcessCmdKey(ref msg, k);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float pad = Ui.P(18);

        float tw = Math.Min(g.MeasureString(title, fTitle, PointF.Empty, StringFormat.GenericTypographic).Width, W - 2 * pad - Ui.P(100));
        Ui.DrawText(g, title, fTitle, new RectangleF(pad, Ui.P(12), tw + Ui.P(4), Ui.P(24)), Ui.Text, StringAlignment.Near);
        if (hdr) {
            var br = new RectangleF(pad + tw + Ui.P(10), Ui.P(16), Ui.P(34), Ui.P(17));
            using (var path = Ui.RoundRect(br, Ui.P(5)))
            using (var b = new SolidBrush(Color.FromArgb(255, 214, 10)))
                g.FillPath(b, path);
            Ui.DrawText(g, "HDR", fBadge, br, Color.FromArgb(28, 28, 30), StringAlignment.Center);
        }
        if (value >= 0)
            Ui.DrawText(g, value + "%", fSmall, new RectangleF(W - pad - Ui.P(50), Ui.P(12), Ui.P(50), Ui.P(24)), Ui.Text2, StringAlignment.Far);
        string s = sub;
        if (hdr) s += L.T(" · HDR activo en Windows", " · HDR on in Windows");
        if (!connected) s = sub == "" ? status : sub + " · " + status;
        Ui.DrawText(g, s, fSmall, new RectangleF(pad, Ui.P(34), W - 2 * pad, Ui.P(18)), Ui.Text2, StringAlignment.Near);

        PaintSlider(g);

        for (int i = 0; i < Labels.Length; i++) {
            var c = Circle(i);
            var center = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2);
            var fill = on[i] ? Color.FromArgb(245, 245, 247) : Color.FromArgb(hot == i ? 58 : 38, 255, 255, 255);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, c);
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
            if (rows[0] || rows[1] || rows[2]) g.DrawLine(p, pad, Ui.P(200), W - pad, Ui.P(200));
            if (rows[2] && (rows[0] || rows[1])) g.DrawLine(p, pad, Ui.P(RowY(RowPlan) - 3), W - pad, Ui.P(RowY(RowPlan) - 3));
        }
        foreach (int item in new[] { RowScreen, RowSleep, RowPlan }) {
            if (!RowOn(item)) continue;
            var r = Row(item);
            if (hot == item)
                using (var path = Ui.RoundRect(r, Ui.P(8)))
                using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255)))
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
            Ui.ChevronDown(g, new PointF(cx, cy), Ui.P(8), Ui.Text2);
        }
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

    public void ShowValue(int v) {
        SetValue(v);
        if (!Visible) ShowFrosted(Where());
        Restart();
    }

    protected override void OnPaint(PaintEventArgs e) { PaintSlider(e.Graphics); }
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

class SettingsForm : Form {
    static readonly string[] PosKeys = { "abajo", "arriba", "arriba-der" };
    static readonly string[] PosNames = { L.T("Abajo al centro", "Bottom center"), L.T("Arriba al centro", "Top center"), L.T("Arriba a la derecha", "Top right") };
    static readonly Color Bg = Color.FromArgb(32, 32, 34), Field = Color.FromArgb(48, 48, 52);

    readonly Config cfg;
    readonly NumericUpDown secs = new NumericUpDown(), step = new NumericUpDown();
    readonly ComboBox pos = new ComboBox();
    readonly Swatches colors = new Swatches();
    readonly CheckBox startup = new CheckBox(), showScreen = new CheckBox(), showSleep = new CheckBox(), showPlan = new CheckBox();
    readonly HotkeyBox keyUp = new HotkeyBox(), keyDown = new HotkeyBox();
    readonly TextBox ip = new TextBox();
    readonly Button find = new Button(), save = new Button(), cancel = new Button();
    int y;

    public bool Startup { get { return startup.Checked; } }
    public event Action<bool> Saved; // true if the IP changed

    public SettingsForm(Config cfg, bool isStartup) {
        this.cfg = cfg;
        Text = L.T("LGBrillo · Configuración", "LGBrillo · Settings");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Bg;
        ForeColor = Ui.Text;
        Font = Ui.F("Segoe UI Variable Text", "Segoe UI", 10f, FontStyle.Regular);
        ClientSize = new Size(Ui.P(460), Ui.P(392));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        y = Ui.P(18);
        secs.DecimalPlaces = 1; secs.Increment = 0.5M; secs.Minimum = 0.5M; secs.Maximum = 10;
        secs.Value = (decimal)Math.Max(0.5, Math.Min(10, cfg.OsdSegundos));
        AddRow(L.T("Duración del cartel (segundos)", "On-screen bar duration (s)"), secs, Ui.P(90));

        pos.DropDownStyle = ComboBoxStyle.DropDownList;
        pos.FlatStyle = FlatStyle.Flat;
        pos.Items.AddRange(PosNames);
        pos.SelectedIndex = Math.Max(0, Array.IndexOf(PosKeys, cfg.OsdPos));
        AddRow(L.T("Posición del cartel", "On-screen bar position"), pos, Ui.P(190));

        step.Minimum = 1; step.Maximum = 20; step.Value = Math.Max(1, Math.Min(20, cfg.Paso));
        keyUp.Set(cfg.UpMods, (Keys)cfg.UpKey);
        keyDown.Set(cfg.DownMods, (Keys)cfg.DownKey);
        AddRow(L.T("Atajo para subir el brillo", "Brightness up shortcut"), keyUp, Ui.P(190));
        AddRow(L.T("Atajo para bajar el brillo", "Brightness down shortcut"), keyDown, Ui.P(190));
        AddRow(L.T("Paso de los atajos", "Shortcut step"), step, Ui.P(90));

        colors.Value = cfg.Color;
        colors.BackColor = Bg;
        AddRow(L.T("Color de la barra", "Bar color"), colors, Ui.P(216));

        ip.Text = cfg.Ip;
        var ipBox = new Panel { BackColor = Bg, Size = new Size(Ui.P(216), Ui.P(30)) };
        ip.SetBounds(0, Ui.P(2), Ui.P(130), Ui.P(26));
        find.SetBounds(Ui.P(138), 0, Ui.P(78), Ui.P(30));
        find.Text = L.T("Buscar", "Find");
        Flat(find, Field);
        ipBox.Controls.Add(ip);
        ipBox.Controls.Add(find);
        AddRow(L.T("IP del monitor", "Monitor IP"), ipBox, Ui.P(216));

        var head = new Label { Text = L.T("Mostrar en el panel", "Show in the panel"), AutoSize = true, ForeColor = Ui.Text2 };
        head.Location = new Point(Ui.P(22), y - Ui.P(4));
        Controls.Add(head);
        y += Ui.P(22);
        int cx = Ui.P(22);
        foreach (var t in new[] {
            Tuple.Create(showScreen, L.T("Apagar pantalla", "Turn off display"), !cfg.HideScreenOff),
            Tuple.Create(showSleep, L.T("Suspender PC", "Sleep PC"), !cfg.HideSleep),
            Tuple.Create(showPlan, L.T("Plan de energía", "Power plan"), !cfg.HidePlan) }) {
            t.Item1.Text = t.Item2;
            t.Item1.Checked = t.Item3;
            t.Item1.AutoSize = true;
            t.Item1.Location = new Point(cx, y);
            Controls.Add(t.Item1);
            cx += t.Item1.PreferredSize.Width + Ui.P(14);
        }
        y += Ui.P(40);

        startup.Text = L.T("Iniciar con Windows", "Start with Windows");
        startup.Checked = isStartup;
        startup.AutoSize = true;
        startup.Location = new Point(Ui.P(22), y + Ui.P(6));
        Controls.Add(startup);
        ClientSize = new Size(ClientSize.Width, y + Ui.P(100));

        save.Text = L.T("Guardar", "Save");
        cancel.Text = L.T("Cancelar", "Cancel");
        cancel.SetBounds(ClientSize.Width - Ui.P(22) - Ui.P(100), ClientSize.Height - Ui.P(54), Ui.P(100), Ui.P(34));
        save.SetBounds(cancel.Left - Ui.P(10) - Ui.P(100), cancel.Top, Ui.P(100), Ui.P(34));
        Flat(save, Ui.Accent);
        if (Ui.Accent.GetBrightness() > 0.6f) save.ForeColor = Color.Black;
        Flat(cancel, Field);
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (Control c in new Control[] { secs, step, pos, ip, keyUp, keyDown }) { c.BackColor = Field; c.ForeColor = Ui.Text; }
        ip.BorderStyle = BorderStyle.FixedSingle;
        keyUp.BorderStyle = BorderStyle.FixedSingle;
        keyDown.BorderStyle = BorderStyle.FixedSingle;
        secs.BorderStyle = BorderStyle.FixedSingle;
        step.BorderStyle = BorderStyle.FixedSingle;

        find.Click += async delegate {
            find.Enabled = false;
            find.Text = "…";
            string found = await Tv.Discover();
            if (found != null) ip.Text = found;
            find.Text = found != null ? L.T("Buscar", "Find") : L.T("No está", "Not found");
            find.Enabled = true;
        };
        cancel.Click += delegate { Close(); };
        save.Click += delegate {
            if (keyUp.Mods == keyDown.Mods && keyUp.Key == keyDown.Key) {
                MessageBox.Show(this, L.T("Los dos atajos no pueden ser iguales.", "The two shortcuts can't be the same."), "LGBrillo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string newIp = ip.Text.Trim();
            bool ipChanged = newIp != cfg.Ip;
            cfg.OsdSegundos = (double)secs.Value;
            cfg.OsdPos = PosKeys[Math.Max(0, pos.SelectedIndex)];
            cfg.Paso = (int)step.Value;
            cfg.Color = colors.Value;
            cfg.Ip = newIp;
            cfg.UpMods = keyUp.Mods; cfg.UpKey = (int)keyUp.Key;
            cfg.DownMods = keyDown.Mods; cfg.DownKey = (int)keyDown.Key;
            cfg.HideScreenOff = !showScreen.Checked;
            cfg.HideSleep = !showSleep.Checked;
            cfg.HidePlan = !showPlan.Checked;
            cfg.Save();
            if (Saved != null) Saved(ipChanged);
            Close();
        };
    }

    void AddRow(string label, Control c, int width) {
        int h = Ui.P(30);
        var l = new Label { Text = label, AutoSize = false, ForeColor = Ui.Text, TextAlign = ContentAlignment.MiddleLeft };
        l.SetBounds(Ui.P(22), y, Ui.P(200), h);
        Controls.Add(l);
        c.Width = width;
        if (c is Swatches || c is Panel) c.Height = h;
        c.Location = new Point(ClientSize.Width - Ui.P(22) - width, y + (h - c.Height) / 2);
        Controls.Add(c);
        y += h + Ui.P(18);
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
        Ui.Backdrop(this, 2, false); // Mica on the title bar
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

// Windows HDR ("advanced color") on any active display.
static class Hdr {
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PathSource { public Luid Adapter; public uint Id, ModeIdx, Status; }
    [StructLayout(LayoutKind.Sequential)] struct PathTarget {
        public Luid Adapter; public uint Id, ModeIdx, Tech, Rotation, Scaling, RefreshN, RefreshD, ScanLine; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct ModeInfo { public uint Type; }
    [StructLayout(LayoutKind.Sequential)] struct AdvancedColor {
        public uint Type, Size; public Luid Adapter; public uint Id;
        public uint Value, Encoding, Bits;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] paths, ref uint nm, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);

    public static bool Enabled() {
        try {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return false; // QDC_ONLY_ACTIVE_PATHS
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < np; i++) {
                var a = new AdvancedColor { Type = 9, Size = (uint)Marshal.SizeOf(typeof(AdvancedColor)) }; // GET_ADVANCED_COLOR_INFO
                a.Adapter = paths[i].Target.Adapter;
                a.Id = paths[i].Target.Id;
                if (DisplayConfigGetDeviceInfo(ref a) == 0 && (a.Value & 2) != 0) return true; // advancedColorEnabled
            }
        } catch { }
        return false;
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

    public Hotkeys() { CreateHandle(new CreateParams()); }

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
    DateTime lostAt = DateTime.MinValue;
    SettingsForm settings;

    public App() {
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
        popup.SetHdr(Hdr.Enabled());
        SystemEvents.DisplaySettingsChanged += delegate { popup.SetHdr(Hdr.Enabled()); };
        popup.SliderMoved += v => Want(v, false);
        osd.SliderMoved += v => Want(v, false);
        popup.ButtonClicked += OnButton;

        tv.BacklightChanged += OnBacklight;
        tv.Disconnected += delegate { lostAt = DateTime.UtcNow; SetStatus(L.T("Sin conexión, reintentando…", "Disconnected, retrying…"), false); };
        tv.PairingPrompt += delegate {
            SetStatus(L.T("Aceptá el aviso en el monitor", "Accept the prompt on the monitor"), false);
            tray.ShowBalloonTip(15000, L.T("Brillo del monitor", "Monitor brightness"), L.T("Aceptá el aviso que apareció en el monitor (con el control remoto). Es una sola vez.", "Accept the prompt that appeared on the monitor (with the remote). Only needed once."), ToolTipIcon.Info);
        };

        hk.Pressed += id => Step(id == 1 ? cfg.Paso : -cfg.Paso);
        RegisterHotkeys();

        retry.Tick += async delegate { if (!tv.Connected) await EnsureConnected(); };
        retry.Start();
        var _ = EnsureConnected();
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
        m.Items.Add(new ToolStripSeparator());
        var start = new ToolStripMenuItem(L.T("Iniciar con Windows", "Start with Windows"));
        start.Click += delegate { SetStartup(!IsStartup()); };
        m.Items.Add(start);
        m.Items.Add(L.T("Buscar el monitor de nuevo", "Find the monitor again"), null, async delegate {
            cfg.Ip = "";
            tv.Close();
            await EnsureConnected();
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(L.T("Salir", "Quit"), null, delegate { Quit(); });
        m.Opening += delegate { start.Checked = IsStartup(); };
        foreach (ToolStripItem it in m.Items) it.ForeColor = Ui.Text;
        return m;
    }

    void TogglePopup() {
        if (popup.Visible) { popup.Hide(); return; }
        if (unchecked(Environment.TickCount - popup.HiddenAt) < 250) return; // the click that closed it
        popup.SetHdr(Hdr.Enabled());
        RefreshTimes();
        popup.ShowNear(Cursor.Position);
        if (!tv.Connected) { var _ = EnsureConnected(); }
        else RefreshInput();
    }

    void OpenSettings() {
        popup.Hide();
        if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
        settings = new SettingsForm(cfg, IsStartup());
        var f = settings;
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

    void OnBacklight(int v) {
        applied = v;
        if (!applying) { target = v; ShowValue(v); }
    }

    async void Step(int delta) {
        int b = target >= 0 ? target : applied;
        if (b < 0) {
            if (!await EnsureConnected()) { tray.ShowBalloonTip(4000, L.T("Brillo del monitor", "Monitor brightness"), lastError, ToolTipIcon.Warning); return; }
            b = applied;
            if (b < 0) return;
        }
        Want(b + delta, true);
    }

    void Want(int v, bool fromHotkey) {
        v = Math.Max(0, Math.Min(100, v));
        target = v;
        ShowValue(v);
        if (fromHotkey) osd.ShowValue(v);
        if (!applying) Apply();
    }

    // Always sends the latest requested value; values in between are skipped while one is in flight.
    async void Apply() {
        applying = true;
        try {
            while (target >= 0 && target != applied) {
                if (!await EnsureConnected()) break;
                int v = target;
                try { await tv.SetBacklight(v); applied = v; }
                catch (Exception ex) { lastError = L.T("No se pudo cambiar: ", "Could not change it: ") + ex.Message; SetStatus(lastError, false); break; }
            }
        } finally { applying = false; }
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

    async void ShowInputMenu() {
        if (!await EnsureConnected()) return;
        if (!await LoadInputs()) { SetStatus(L.T("No pude leer las entradas del monitor", "Could not read the monitor's inputs"), false); return; }
        var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ShowCheckMargin = true, ShowImageMargin = false };
        foreach (var inp in inputs) {
            var it = new ToolStripMenuItem(inp.Connected ? inp.Name : inp.Name + L.T("  ·  sin señal", "  ·  no signal")) {
                Checked = inp.AppId == currentApp, ForeColor = inp.Connected ? Ui.Text : Ui.Text2
            };
            string id = inp.Id;
            it.Click += async delegate {
                try { await tv.SwitchInput(id); }
                catch (Exception ex) { SetStatus(L.T("No se pudo cambiar la entrada: ", "Could not switch input: ") + ex.Message, false); return; }
                await Task.Delay(1200);
                RefreshInput();
            };
            m.Items.Add(it);
        }
        m.Closed += delegate { m.BeginInvoke((Action)m.Dispose); };
        popup.ShowMenu(2, m);
    }

    void RefreshTimes() {
        popup.SetTimes(Power.Name(Power.Get(Power.SubVideo, Power.VideoIdle)), Power.Name(Power.Get(Power.SubSleep, Power.StandbyIdle)));
    }

    // Menu with the Windows inactivity timeouts; checks the current one (and adds it if it isn't listed).
    void ShowTimeMenu(int row, Guid sub, Guid setting) {
        int current = Power.Get(sub, setting);
        var times = new List<int>(Power.Times);
        if (current > 0 && !times.Contains(current)) { times.Add(current); times.Sort(); }
        var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ShowCheckMargin = true, ShowImageMargin = false };
        foreach (int s in times) {
            var it = new ToolStripMenuItem(Power.Name(s)) { Checked = s == current, ForeColor = Ui.Text };
            int v = s;
            it.Click += delegate {
                if (!Power.Set(sub, setting, v))
                    tray.ShowBalloonTip(5000, "LGBrillo", L.T("Windows no dejó cambiar ese tiempo.", "Windows did not allow changing that time."), ToolTipIcon.Warning);
                RefreshTimes();
            };
            m.Items.Add(it);
            if (s == 0) m.Items.Add(new ToolStripSeparator());
        }
        m.Closed += delegate { m.BeginInvoke((Action)m.Dispose); };
        popup.ShowMenu(row, m);
    }

    // 0 = Eye Care, 1 = Pixel Refresh (both on the monitor), 2 = video input; Windows power rows.
    async void OnButton(int i) {
        if (i == 2) { ShowInputMenu(); return; }
        if (i == Popup.RowScreen) { ShowTimeMenu(i, Power.SubVideo, Power.VideoIdle); return; }
        if (i == Popup.RowSleep) { ShowTimeMenu(i, Power.SubSleep, Power.StandbyIdle); return; }
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
            if (!ok) {
                SetStatus(L.T("Buscando el monitor en la red…", "Searching the network for the monitor…"), false);
                string ip = await Tv.Discover();
                if (ip == null) lastError = L.T("No encuentro el monitor (¿está prendido?)", "Monitor not found (is it on?)");
                else if (ip != cfg.Ip) {
                    ok = await TryConnect(ip);
                    if (ok) { cfg.Ip = ip; cfg.Save(); }
                }
            }
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
            lastError = L.T("Sin conexión: ", "Not connected: ") + ex.Message;
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
