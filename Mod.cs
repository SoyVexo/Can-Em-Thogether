using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cpp;
using CanEmThogether;

[assembly: MelonInfo(typeof(CanEmThogether.Mod), "Can Em' Thogether", "0.6.6", "Can Em' Thogether contributors")]
[assembly: MelonGame(null, null)]

namespace CanEmThogether
{
    public class Mod : MelonMod
    {
        public static Mod I;

        // ---- estado de red ----
        ITransport net;
        public bool Connected => net != null;
        public bool IsHost => net != null && net.IsHost;
        public bool UsesSteam => net != null && net.IsSteam;
        public bool LocalHitFx => localFxPref == null || localFxPref.Value;
        MelonPreferences_Entry<bool> localFxPref;
        float fps;
        public float Fps { get { return fps; } }
        public bool Synced => synced;
        public bool ShareFactory => shareFactory != null && shareFactory.Value;

        MelonPreferences_Entry<bool> shareFactory;

        readonly string myId = Guid.NewGuid().ToString("N").Substring(0, 8);

        class Peer
        {
            public float Seen;
            public bool InElev;
            public bool IsHost;
            public bool Vote;
            public string Name = "?";
        }

        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();

        // ---- estado de sincronización ----
        bool synced, applying, prevGameReady, inElev;
        public bool Bypass;
        bool goDone, pendingLeave; string leaveReason = "";
        // v0.6.5
        bool myVote;
        object[] lastSuicideArgs;
        MelonPreferences_Entry<int> netFlushMs;
        MelonPreferences_Entry<string> tubeTeleport;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public float PingMs = -1f;
        int simLagMs;                                   // simulador de lag (pruebas en local)
        static readonly int[] SimSteps = { 0, 50, 100, 200, 400 };
        float nextNetLog;
        int msyncMismatch;
        float nextTimeSync; int timeLogs;
        System.Reflection.PropertyInfo timerProp;

        long lastMoney = -1;
        float lastLocalMoneyChange, nextMSync;
        int moneyLogs;
        public bool ApplyingMoney { get { return applying; } }
        float gameReadyAt, nextPing;
        string[] pendingState;

        // ---- flujo de sala -> inicio de partida ----
        public bool InGame;
        ulong pendingJoin;
        bool helloPending, eventsReady;
        float nextReady;

        string startMode;
        string[] saveParts;
        int saveGot;

        readonly Queue<string[]> outQueue = new Queue<string[]>();

        string notice = "";
        float noticeUntil;

        MelonPreferences_Entry<int> maxPlayers;
        bool staleChecked;

        // ---- avatares de otros jugadores ----
        class Avatar
        {
            public GameObject Go;
            public Vector3 Pos;
            public float Yaw;
            public string Name;
        }

        readonly Dictionary<string, Avatar> avatars = new Dictionary<string, Avatar>();

        float nextPos;
        string cachedName;
        // Nombre de Steam (se lee cuando Steam está listo); mientras tanto, el usuario de Windows
        string myName
        {
            get
            {
                if (testName != null) return testName;
                if (cachedName != null) return cachedName;
                try
                {
                    if (SteamManager.IsReady)
                    {
                        var n = Il2CppSteamworks.SteamClient.Name;
                        if (!string.IsNullOrEmpty(n)) { cachedName = n; return n; }
                    }
                }
                catch { }
                return Environment.UserName;
            }
        }

        MelonPreferences_Entry<float> avatarYOffset;

        // ---- preferencias / UI ----
        MelonPreferences_Entry<string> gateMethod;

        bool showUi = true;

        string lobbyIdText = "";
        string status = "Sin conexión";

        bool idInputFocused = false;
        bool ipInputFocused = false;
        bool showIp = false;

        // ---- 2ª ventana de pruebas (mismo PC) ----
        string testName;      // -cemname="Nombre"  (solo en la ventana de prueba)
        string autoJoin;      // -cemjoin=127.0.0.1:7777
        bool testInstance { get { return testName != null; } }
        public string LogTag { get { return testName != null ? testName.Replace(" ", "_") : "principal"; } }
        bool loggedSynced;
        MelonPreferences_Entry<string> testNamePref;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr GetCommandLineW();

        static string RawCmdLine()
        {
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(GetCommandLineW()) ?? ""; }
            catch { return ""; }
        }

        static string CmdArg(string cmd, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(cmd, key + "=(?:\"([^\"]*)\"|(\\S+))");
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        bool guiErrLogged = false;
        string ipText = "";
        MelonPreferences_Entry<int> ipPort;

        Rect win = new Rect(20, 60, 400, 470);

        public override void OnInitializeMelon()
        {
            I = this;

            var cat = MelonPreferences.CreateCategory("CanEmThogether");

            gateMethod = cat.CreateEntry(
                "GateMethod",
                "",
                description: "Método de GameManager a bloquear hasta que todos estén en el ascensor (vacío = desactivado). Mira el log [TRACE] para elegir."
            );

            maxPlayers = cat.CreateEntry("MaxPlayers", 4);
            netFlushMs = cat.CreateEntry("NetFlushMs", 70, description: "Cada cuántos ms se agrupan y envían los datos por Steam (más alto = menos tráfico pero más retraso). IP usa 30.");
            tubeTeleport = cat.CreateEntry("TubeTeleport", "", description: "Punto al que te teletransporta el tubo, 'x,y,z'. Vacío = centro de la fábrica automático.");
            ipPort = cat.CreateEntry("IpPort", 7777, description: "Puerto para la opción opcional de conexión por IP.");
            localFxPref = cat.CreateEntry("GuestLocalHitFx", true, description: "El invitado ve al instante sangre/ragdoll de sus golpes (el líder sigue mandando la verdad).");
            testNamePref = cat.CreateEntry("TestName", "Jugador2", description: "Nombre que usa la 2ª ventana de pruebas (botón en la opción por IP).");

            try
            {
                string cmd = RawCmdLine();
                testName = CmdArg(cmd, "-cemname");
                autoJoin = CmdArg(cmd, "-cemjoin");
                if (testInstance) LoggerInstance.Msg("Ventana de PRUEBA: " + testName + " (unirse a " + autoJoin + ")");
                MpLog.Write("Mod 0.6.6 iniciado. ventana=" + LogTag);
            }
            catch (Exception e) { LoggerInstance.Warning("Argumentos: " + e.Message); }

            shareFactory = cat.CreateEntry(
                "ShareFactory",
                true,
                description: "true = fábrica compartida (el líder simula los bichos). false = cada uno con su fábrica."
            );

            FactoryPatches.Apply(HarmonyInstance);

            avatarYOffset = cat.CreateEntry(
                "AvatarYOffset",
                0f,
                description: "Sube/baja el muñeco de los demás jugadores (prueba 1 si se hunde en el suelo)."
            );

            // Trazas
            foreach (var n in new[]
            {
                "StartGame",
                "OnTriggerInsideElevator",
                "OnTriggerOutsideElevator",
                "OpenElevator",
                "CloseElevator",
                "CloseElevatorInstant",
                "NextDay"
            })
            {
                var m = AccessTools.Method(typeof(GameManager), n);

                if (m != null)
                {
                    HarmonyInstance.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Trace))
                    );
                }
            }

            // Bloqueo opcional
            if (!string.IsNullOrEmpty(gateMethod.Value))
            {
                var m = AccessTools.Method(typeof(GameManager), gateMethod.Value);

                if (m == null)
                {
                    LoggerInstance.Warning("GateMethod no existe: " + gateMethod.Value);
                }
                else if (m.ReturnType != typeof(void))
                {
                    LoggerInstance.Warning(
                        "GateMethod devuelve " + m.ReturnType.Name +
                        ": no se puede bloquear con prefix. Elige un método void."
                    );
                }
                else
                {
                    HarmonyInstance.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.Gate))
                    );
                }
            }

            // Invitación de Steam
            try
            {
                var args = Environment.GetCommandLineArgs();

                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "+connect_lobby" &&
                        ulong.TryParse(args[i + 1], out var lid))
                    {
                        pendingJoin = lid;
                    }
                }
            }
            catch { }

            LoggerInstance.Msg(
                "Can Em' Thogether cargado. F6 = mostrar/ocultar panel."
            );
        }

        // =============== RED ===============

        public void Send(string type, params string[] args)
        {
            if (net == null) return;

            net.Send(
                myId + "|" + type +
                (args.Length > 0 ? "|" + string.Join("|", args) : "")
            );
        }

        void CreateRoom()
        {
            Disconnect();

            try
            {
                if (!SteamManager.IsReady) { status = "Steam aún no está listo. Espera unos segundos e inténtalo de nuevo."; return; }
                net = Wrap(SteamTransport.Create(maxPlayers.Value));
                synced = true;
                status = "Creando sala...";
            }
            catch (Exception e)
            {
                status = "Error Steam: " + e.Message;
                net = null;
            }
        }

        void JoinRoom(ulong id)
        {
            Disconnect();

            try
            {
                if (!SteamManager.IsReady) { status = "Steam aún no está listo. Espera unos segundos e inténtalo de nuevo."; return; }
                net = Wrap(SteamTransport.Join(id));
                helloPending = true;
                status = "Uniéndose...";
            }
            catch (Exception e)
            {
                status = "Error Steam: " + e.Message;
                net = null;
            }
        }

        // ---- OPCIONAL: conexión directa por IP (online; el líder debe abrir el puerto o usar VPN) ----
        void CreateIpRoom()
        {
            Disconnect();
            try { net = Wrap(TcpTransport.Host(ipPort.Value)); synced = true; status = "Sala por IP abierta (puerto " + ipPort.Value + ")"; }
            catch (Exception e) { status = "Error IP: " + e.Message; net = null; }
        }

        void LaunchSecondWindow()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string nm = (testNamePref.Value ?? "Jugador2").Replace("\"", "").Trim();
                if (nm.Length == 0) nm = "Jugador2";
                string args = "-cemname=\"" + nm + "\" -cemjoin=127.0.0.1:" + ipPort.Value +
                              " -screen-fullscreen 0 -screen-width 960 -screen-height 540";
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                System.Diagnostics.Process.Start(psi);
                Notice("Abriendo 2ª ventana (" + nm + ")...");
            }
            catch (Exception e)
            {
                status = "No se pudo abrir la 2ª ventana: " + e.Message;
                LoggerInstance.Error(e.ToString());
            }
        }

        void JoinIpRoom(string text)
        {
            string host = text.Trim(); int port = ipPort.Value;
            int k = host.LastIndexOf(':');
            if (k > 0 && int.TryParse(host.Substring(k + 1), out var pp)) { port = pp; host = host.Substring(0, k); }
            if (host.Length == 0) { status = "Escribe la IP del líder (ej. 85.12.34.56 o 85.12.34.56:7777)"; return; }
            Disconnect();
            try { net = Wrap(TcpTransport.Join(host, port)); helloPending = true; status = "Conectando por IP..."; }
            catch (Exception e) { status = "Error IP: " + e.Message; net = null; }
        }

        void CopyId()
        {
            if (net == null || net.LobbyId == 0) return;
            string id = net.LobbyId.ToString();
            Clip.Set(id);
            Notice("ID copiado: " + id);
        }

        void PasteInto(bool ip)
        {
            string f = FilterInput(Clip.Get(), ip);
            if (ip) ipText = f; else lobbyIdText = f;
            Notice(f == "" ? "El portapapeles no tiene " + (ip ? "una IP" : "un ID") + " válido" : "Pegado: " + f);
        }

        static bool IsAllowed(char c, bool ip) { return char.IsDigit(c) || (ip && (c == '.' || c == ':')); }
        static string FilterInput(string s, bool ip) { return s == null ? "" : new string(s.Trim().Where(c => IsAllowed(c, ip)).ToArray()); }

        void HandleTyping(Event e)
        {
            bool ip = ipInputFocused;
            string text = ip ? ipText : lobbyIdText;
            int max = ip ? 40 : 20;
            if (e.keyCode == KeyCode.Backspace) { if (text.Length > 0) text = text.Substring(0, text.Length - 1); e.Use(); }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { idInputFocused = false; ipInputFocused = false; e.Use(); }
            else if (e.keyCode == KeyCode.V && (e.control || e.command)) { text = FilterInput(Clip.Get(), ip); e.Use(); }
            else if (!char.IsControl(e.character) && e.character != '\0' && IsAllowed(e.character, ip) && text.Length < max) { text += e.character; e.Use(); }
            if (ip) ipText = text; else lobbyIdText = text;
        }

        ITransport Wrap(ITransport t)
        {
            var bt = new BatchTransport(t, t.IsSteam ? Math.Max(20, netFlushMs.Value) / 1000.0 : 0.03);
            bt.SimDelay = simLagMs / 1000.0;
            bt.SimJitter = simLagMs / 1000.0 * 0.25;
            return bt;
        }

        public void Notice(string msg)
        {
            notice = msg;
            noticeUntil = Time.realtimeSinceStartup + 4f;
        }

        void Disconnect()
        {
            if (net != null)
            {
                Send("BYE");
                net.Flush();
                net.Close();
            }

            foreach (var k in avatars.Keys.ToList())
                RemoveAvatar(k);

            net = null;
            peers.Clear();
            synced = false;
            pendingState = null;
            lastMoney = -1;
            status = "Sin conexión";

            outQueue.Clear();
            saveParts = null;
            helloPending = false;

            RestoreSave();
        }

        // =============== BUCLE ===============

        float nextBgCheck;
        bool bgBoost; int bgVSync, bgFps;

        public override void OnUpdate()
        {
            { float dtu = Time.unscaledDeltaTime; if (dtu > 0f) fps = Mathf.Lerp(fps, 1f / dtu, 0.05f); }

            // Ventana SIN foco / minimizada: Unity la frena (vSync bloquea el dibujado y la simulación va a cámara lenta:
            // los bichos, las latas y los envíos del líder llegaban muy tarde). Se quita el vSync y se fija un límite de FPS.
            try
            {
                bool focused = UnityEngine.Application.isFocused;
                if (!focused && !bgBoost)
                {
                    bgBoost = true;
                    bgVSync = UnityEngine.QualitySettings.vSyncCount;
                    bgFps = UnityEngine.Application.targetFrameRate;
                    UnityEngine.QualitySettings.vSyncCount = 0;
                    UnityEngine.Application.targetFrameRate = 60;
                    MpLog.Write("Ventana sin foco: vSync desactivado, límite 60 FPS");
                }
                else if (focused && bgBoost)
                {
                    bgBoost = false;
                    UnityEngine.QualitySettings.vSyncCount = bgVSync;
                    UnityEngine.Application.targetFrameRate = bgFps;
                    MpLog.Write("Ventana con foco: vSync restaurado");
                }
            }
            catch { }

            // Sin esto, Unity congela la ventana que no tiene el foco (el líder dejaría de enviar bichos)
            if (Time.realtimeSinceStartup > nextBgCheck)
            {
                nextBgCheck = Time.realtimeSinceStartup + 3f;
                try { if (!UnityEngine.Application.runInBackground) UnityEngine.Application.runInBackground = true; }
                catch { }
            }

            if (autoJoin != null && net == null && Time.realtimeSinceStartup > 10f)
            {
                var j = autoJoin;
                autoJoin = null;
                JoinIpRoom(j);
            }

            if (!eventsReady && SteamManager.IsReady)
            {
                eventsReady = true;

                try
                {
                    SteamTransport.InitEvents(id =>
                    {
                        pendingJoin = id;
                    });
                }
                catch (Exception e)
                {
                    LoggerInstance.Error("Steam events: " + e);
                }
            }

            if (eventsReady && pendingJoin != 0 && net == null)
            {
                var id = pendingJoin;
                pendingJoin = 0;
                JoinRoom(id);
            }

            if (!staleChecked && SaveManager.Instance != null && net == null)
            {
                staleChecked = true;
                RestoreSave();
            }

            if (net == null) return;

            net.Poll();
            net.Flush();     // envía lo acumulado en el fotograma anterior (agrupado y comprimido)

            {
                var bt = net as BatchTransport;
                if (bt != null && Time.realtimeSinceStartup > nextNetLog)
                {
                    nextNetLog = Time.realtimeSinceStartup + 10f;
                    MpLog.Write("Red: subida " + bt.OutKBs.ToString("F1") + " KB/s, bajada " + bt.InKBs.ToString("F1") + " KB/s, " +
                                bt.PacketsPerSec.ToString("F1") + " paquetes/s, ping " + (PingMs >= 0 ? PingMs.ToString("F0") + " ms" : "?"));
                }
            }

            if (helloPending && net.Ready)
            {
                helloPending = false;
                Send("HELLO");
            }

            if (net.LobbyId != 0)
                status = net.Info + " (ID " + net.LobbyId + ")";
            else
                status = net.Info;

            if (outQueue.Count > 0)
            {
                var c = outQueue.Dequeue();
                Send("SAVE", c);
            }

            string line;

            while (net != null && net.TryReceive(out line))
                Handle(line);

            if (net == null) return;

            float now = Time.realtimeSinceStartup;

            foreach (var k in peers
                .Where(p => now - p.Value.Seen > (p.Value.IsHost ? 6f : 10f))
                .Select(p => p.Key)
                .ToList())
            {
                if (!IsHost && peers[k].IsHost)
                {
                    pendingLeave = true;
                    leaveReason = "Se perdió la conexión con el líder";
                }
                peers.Remove(k);
                RemoveAvatar(k);
            }

            if (pendingLeave)
            {
                pendingLeave = false;
                LeaveSession(leaveReason);
                return;
            }

            if (now > nextPing)
            {
                nextPing = now + 2f;
                Send("PING", myName, IsHost ? "H" : "G");
                Send("LAT", Clock.ElapsedMilliseconds.ToString());
            }

            bool ready =
                InGame &&
                GameManager.Instance != null &&
                MoneyManager.Instance != null;

            if (ready != prevGameReady)
            {
                prevGameReady = ready;
                lastMoney = -1;
                gameReadyAt = now;
                inElev = false;
                FactorySync.Reset();
            }

            // En multijugador la pausa NO detiene el tiempo (si no, la partida se desincroniza con los demás)
            if (ready && Time.timeScale < 1f) Time.timeScale = 1f;

            if (!ready || now - gameReadyAt < 3f) return;

            // El líder manda cada segundo el reloj del día/noche
            if (IsHost && now > nextTimeSync)
            {
                nextTimeSync = now + 1f;
                float? tm = ReadTimer();
                if (tm.HasValue) Send("TIME", tm.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }

            SendPosition(now);
            UpdateAvatars();

            if (!IsHost && !synced && pendingState == null && now > nextReady)
            {
                nextReady = now + 3f;
                Send("READY");
            }

            if (pendingState != null && !IsHost)
            {
                ApplyState(pendingState);
                pendingState = null;
                synced = true;
            }

            if (synced != loggedSynced) { loggedSynced = synced; MpLog.Write("synced=" + synced + " host=" + IsHost + " factory=" + ShareFactory); }

            if (!synced) return;

            if (ShareFactory)
                FactorySync.Tick(now);

            long m = (long)MoneyManager.Instance.Money;

            if (lastMoney >= 0 && m != lastMoney)
            {
                if (!IsHost && ShareFactory && m > lastMoney)
                {
                    // El invitado NO cobra por su cuenta (las ventas las cobra el líder y le llegan por MONEY): se deshace el aumento local.
                    // Esto evita que el dinero de la pantalla parpadee entre dos cifras mientras se suma.
                    long before = lastMoney;
                    applying = true;
                    try { MoneyManager.Instance.SetMoney(before); } finally { applying = false; }
                    if (moneyLogs++ < 60) MpLog.Write("Aumento local de dinero descartado: +" + (m - before));
                    m = (long)MoneyManager.Instance.Money;
                }
                else
                {
                    Send("MONEY", (m - lastMoney).ToString());
                    lastLocalMoneyChange = now;
                    if (moneyLogs++ < 60) MpLog.Write("MONEY enviado " + (m - lastMoney) + " (total " + m + ")");
                }
            }

            lastMoney = m;

            // El líder manda cada segundo su dinero real: corrige cualquier desajuste del invitado
            if (IsHost && now > nextMSync)
            {
                nextMSync = now + 1f;
                Send("MSYNC", m.ToString());
            }
        }

        public override void OnApplicationQuit()
        {
            Disconnect();
        }

        void Handle(string line)
        {
            var p = line.Split('|');

            if (p.Length < 2 || p[0] == myId)
                return;

            string from = p[0];
            string type = p[1];

            string[] a = p.Skip(2).ToArray();

            if (type == "BYE")
            {
                if (!IsHost && peers.TryGetValue(from, out var gone) && gone.IsHost)
                {
                    pendingLeave = true;
                    leaveReason = "El líder ha salido de la sala";
                }
                peers.Remove(from);
                RemoveAvatar(from);
                return;
            }

            if (!peers.TryGetValue(from, out var peer))
            {
                peer = new Peer();
                peers[from] = peer;
            }

            peer.Seen = Time.realtimeSinceStartup;

            switch (type)
            {
                case "PING":
                    if (a.Length > 0)
                        peer.Name = a[0];
                    if (a.Length > 1 && a[1] == "H")
                        peer.IsHost = true;
                    break;

                case "TIME":   // el líder manda el reloj del día: el invitado se ajusta si se desvía
                    if (!IsHost && synced && a.Length > 0)
                    {
                        float tv = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture);
                        float? cur = ReadTimer();
                        if (cur.HasValue && Mathf.Abs(cur.Value - tv) > 0.75f)
                        {
                            WriteTimer(tv);
                            if (timeLogs++ < 25) MpLog.Write("TIME: reloj del invitado " + cur.Value.ToString("F1") + " -> " + tv.ToString("F1"));
                        }
                    }
                    break;

                case "LAT":    // medición de ping: se devuelve tal cual
                    if (a.Length > 0) Send("LATR", a[0], from);
                    break;

                case "LATR":
                    if (a.Length > 1 && a[1] == myId && long.TryParse(a[0], out var t0))
                    {
                        float rtt = (float)(Clock.ElapsedMilliseconds - t0);
                        PingMs = PingMs < 0 ? rtt : Mathf.Lerp(PingMs, rtt, 0.3f);
                    }
                    break;

                case "VOTE":   // votación para saltar el día (sustituye al tubo)
                    if (a.Length > 0)
                    {
                        peer.Vote = a[0] == "1";
                        if (peer.Vote) Notice(peer.Name + " quiere saltar el día (" + VotesYes() + "/" + (peers.Count + 1) + ") - F7 para votar");
                        if (IsHost) TrySkipDay();
                    }
                    break;

                case "SKIPDAY":
                    if (!IsHost) DoSkipDay();
                    break;

                case "NEXTDAY":   // el líder pulsó "Continuar día": todos continúan
                    if (!IsHost && InGame && GameManager.Instance != null)
                    {
                        Bypass = true;
                        try { GameManager.Instance.NextDay(); }
                        catch (Exception e) { LoggerInstance.Error("NEXTDAY: " + e); }
                        finally { Bypass = false; }
                    }
                    break;

                case "POS":
                    if (a.Length >= 5)
                        peer.Name = a[4];

                    OnPos(from, a);
                    break;

                case "HELLO":
                    if (IsHost && InGame)
                        QueueStart(from, true);
                    break;

                case "READY":
                    if (IsHost)
                        SendState(from);
                    break;

                case "START":
                    if (!IsHost && a.Length >= 3 &&
                        (a[0] == myId || a[0] == "*"))
                    {
                        OnStart(a[1], int.Parse(a[2]));
                    }
                    break;

                case "SAVE":
                    if (!IsHost && a.Length >= 4 &&
                        (a[0] == myId || a[0] == "*"))
                    {
                        OnSaveChunk(
                            int.Parse(a[1]),
                            int.Parse(a[2]),
                            a[3]
                        );
                    }
                    break;

                case "STATE":
                    if (a.Length >= 5 && a[0] == myId)
                        pendingState = a;
                    break;

                case "MONEY":
                    if (synced && a.Length > 0 &&
                        MoneyManager.Instance != null)
                    {
                        ApplyMoney(long.Parse(a[0]));
                    }
                    break;

                case "MSYNC":
                    if (!IsHost && synced && a.Length > 0 && MoneyManager.Instance != null)
                    {
                        long abs = long.Parse(a[0]);
                        long cur = (long)MoneyManager.Instance.Money;
                        if (cur == abs) msyncMismatch = 0;
                        // se corrige solo si la diferencia persiste en dos avisos seguidos (evita pelear con un MONEY que viene de camino)
                        else if (++msyncMismatch >= 2 && Time.realtimeSinceStartup - lastLocalMoneyChange > 1f)
                        {
                            msyncMismatch = 0;
                            applying = true;
                            try { MoneyManager.Instance.SetMoney(abs); }
                            finally { applying = false; }
                            lastMoney = (long)MoneyManager.Instance.Money;
                            if (moneyLogs++ < 60) MpLog.Write("MSYNC: corrijo dinero " + cur + " -> " + abs);
                        }
                    }
                    break;

                case "WEAPON":
                    if (synced && a.Length > 0)
                        UnlockWeaponByName(a[0]);
                    break;

                case "SKILL":
                    if (synced && a.Length > 0)
                        ApplySkillById(int.Parse(a[0]));
                    break;

                case "ELEV":
                    if (a.Length > 0)
                    {
                        peer.InElev = a[0] == "1";

                        if (IsHost)
                            TryGo();
                    }
                    break;

                case "WANTGO":
                    if (IsHost)
                        TryGo();
                    break;

                case "GO":
                    DoGo();
                    break;

                default:
                    if (synced && ShareFactory)
                        FactorySync.Handle(type, a);
                    break;
            }
        }

        // =============== INICIO DE PARTIDA ===============

        string SavePath()
        {
            return SaveManager.Instance.SavePath;
        }

        void BackupSave()
        {
            if (testInstance) return;   // la ventana de prueba comparte el guardado con la principal
            try
            {
                string p = SavePath();
                string bak = p + ".mpbackup";
                string none = p + ".mpnone";

                if (File.Exists(bak) || File.Exists(none))
                    return;

                if (File.Exists(p))
                    File.Copy(p, bak);
                else
                    File.WriteAllText(none, "1");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("BackupSave: " + e);
            }
        }

        public void RestoreSave()
        {
            if (testInstance) return;
            try
            {
                if (SaveManager.Instance == null)
                    return;

                string p = SavePath();
                string bak = p + ".mpbackup";
                string none = p + ".mpnone";

                if (File.Exists(bak))
                {
                    File.Copy(bak, p, true);
                    File.Delete(bak);
                }
                else if (File.Exists(none))
                {
                    if (File.Exists(p))
                        File.Delete(p);

                    File.Delete(none);
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Error("RestoreSave: " + e);
            }
        }

        public void LeaderStarting(bool cont)
        {
            QueueStart("*", cont);
        }

        void QueueStart(string target, bool cont)
        {
            string mode = cont ? "C" : "N";
            byte[] data = null;

            if (cont)
            {
                try
                {
                    var p = SavePath();

                    if (File.Exists(p))
                        data = File.ReadAllBytes(p);
                }
                catch { }

                if (data == null)
                    mode = "N";
            }

            var parts = new List<string>();

            if (data != null)
            {
                string b64 = Convert.ToBase64String(data);

                for (int i = 0; i < b64.Length; i += 2800)
                {
                    parts.Add(
                        b64.Substring(i, Math.Min(2800, b64.Length - i))
                    );
                }
            }

            Send("START", target, mode, parts.Count.ToString());

            for (int i = 0; i < parts.Count; i++)
            {
                outQueue.Enqueue(new[]
                {
                    target,
                    i.ToString(),
                    parts.Count.ToString(),
                    parts[i]
                });
            }
        }

        void OnStart(string mode, int total)
        {
            if (InGame)
                return;

            startMode = mode;
            saveGot = 0;

            saveParts = total > 0 ? new string[total] : null;

            Notice("El líder está iniciando la partida...");

            if (total == 0)
                BeginGuestLoad();
        }

        void OnSaveChunk(int idx, int total, string data)
        {
            if (saveParts == null ||
                saveParts.Length != total ||
                idx < 0 ||
                idx >= total)
                return;

            if (saveParts[idx] == null)
            {
                saveParts[idx] = data;
                saveGot++;
            }

            if (saveGot == total)
                BeginGuestLoad();
        }

        void BeginGuestLoad()
        {
            try
            {
                BackupSave();

                if (saveParts != null)
                {
                    if (!testInstance)
                    File.WriteAllBytes(
                        SavePath(),
                        Convert.FromBase64String(string.Concat(saveParts))
                    );

                    var d = SaveManager.Instance.Load();

                    if (d != null)
                        SaveManager.Instance.CurrentSaveData = d;
                }

                saveParts = null;
                Bypass = true;

                try
                {
                    if (startMode == "C")
                        Menu.Instance.ContinueGame();
                    else
                        Menu.Instance.NewGame();
                }
                finally
                {
                    Bypass = false;
                }

                Notice("Cargando la partida del líder...");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("BeginGuestLoad: " + e);
                Notice("Error al cargar la partida del líder (mira el log)");
            }
        }

        float? ReadTimer()
        {
            try
            {
                if (timerProp == null) timerProp = typeof(GameManager).GetProperty("Timer");
                if (timerProp == null || GameManager.Instance == null) return null;
                return Convert.ToSingle(timerProp.GetValue(GameManager.Instance));
            }
            catch { return null; }
        }

        void WriteTimer(float v)
        {
            try
            {
                if (timerProp == null) timerProp = typeof(GameManager).GetProperty("Timer");
                if (timerProp == null || GameManager.Instance == null) return;
                timerProp.SetValue(GameManager.Instance, Convert.ChangeType(v, timerProp.PropertyType));
            }
            catch (Exception e) { if (timeLogs++ < 25) MpLog.Write("ERROR WriteTimer: " + e.Message); }
        }

        /// <summary>El líder se fue: se sale de la sala, se restaura tu partida y se vuelve al menú principal.</summary>
        void LeaveSession(string reason)
        {
            MpLog.Write("Saliendo de la sesión: " + reason);
            bool wasInGame = InGame;
            Disconnect();
            try { Time.timeScale = 1f; } catch { }
            Notice(reason);
            InGame = false;
            if (wasInGame)
            {
                try
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    UnityEngine.SceneManagement.SceneManager.LoadScene(sc.buildIndex);
                }
                catch (Exception e) { LoggerInstance.Error("Volver al menú: " + e); }
            }
        }

        // ---- ascensor: nadie termina el día hasta que TODOS estén dentro ----
        public bool ElevatorEnter()
        {
            if (!Connected || !InGame || Bypass || !synced || goDone) return true;
            string st = "";
            try { st = GameManager.Instance.elevatorState.ToString(); } catch { }
            if (st != "WaitingForPlayerInside") return true;   // al empezar el día no se espera a nadie

            inElev = true;
            Send("ELEV", "1");
            MpLog.Write("Ascensor: entré (esperando a los demás). Todos dentro=" + AllIn());
            if (IsHost) TryGo();
            if (!goDone) Notice("Esperando a que todos entren al ascensor...");
            return false;      // el día no termina todavía para mí
        }

        public bool NextDayRequested()
        {
            if (!Connected || !InGame || Bypass) return true;
            if (!IsHost) { Notice("Solo el líder puede continuar el día"); return false; }
            Send("NEXTDAY");
            return true;
        }

        // =============== TUBO -> TELETRANSPORTE + VOTACIÓN PARA SALTAR EL DÍA ===============

        int VotesYes()
        {
            int n = myVote ? 1 : 0;
            foreach (var p in peers.Values) if (p.Vote) n++;
            return n;
        }

        void ToggleVote()
        {
            if (!Connected || !InGame) return;
            myVote = !myVote;
            Send("VOTE", myVote ? "1" : "0");
            Notice(myVote ? "Has votado SALTAR EL DÍA (" + VotesYes() + "/" + (peers.Count + 1) + ")" : "Has retirado tu voto");
            if (IsHost) TrySkipDay();
        }

        void TrySkipDay()
        {
            if (!IsHost || !myVote) return;
            foreach (var p in peers.Values) if (!p.Vote) return;
            Send("SKIPDAY");
            DoSkipDay();
        }

        void ResetVotes()
        {
            myVote = false;
            foreach (var p in peers.Values) p.Vote = false;
        }

        /// <summary>Ejecuta el "acabar el día" original del juego (el del tubo) en este cliente.</summary>
        void DoSkipDay()
        {
            ResetVotes();
            if (GameManager.Instance == null || !InGame) return;
            try
            {
                var m = typeof(GameManager).GetMethod("Suicide");
                if (m == null) { MpLog.Write("ERROR: GameManager.Suicide no existe"); return; }
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (lastSuicideArgs != null && i < lastSuicideArgs.Length && lastSuicideArgs[i] != null && pt.IsInstanceOfType(lastSuicideArgs[i])) args[i] = lastSuicideArgs[i];
                    else if (pt.IsEnum) { try { args[i] = Enum.Parse(pt, "UnauthorizedTubeUsage"); } catch { args[i] = Activator.CreateInstance(pt); } }
                    else if (pt == typeof(float)) args[i] = 0f;
                    else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                MpLog.Write("Votación aprobada: se salta el día");
                Bypass = true;
                try { m.Invoke(GameManager.Instance, args); }
                finally { Bypass = false; }
            }
            catch (Exception e) { LoggerInstance.Error("DoSkipDay: " + e); }
        }

        /// <summary>Alguien se tira por el tubo: en multijugador NO acaba el día; se le teletransporta al centro de la fábrica.</summary>
        public bool OnTubeUsed(object[] args)
        {
            if (!Connected || !InGame || Bypass) return true;
            lastSuicideArgs = args;
            TeleportToCenter();
            Notice("El tubo está desactivado en multijugador. Para saltar el día, vota con F7");
            return false;
        }

        Vector3? FactoryCenter()
        {
            try
            {
                string cfg = tubeTeleport.Value;
                if (!string.IsNullOrWhiteSpace(cfg))
                {
                    var c = cfg.Split(',');
                    if (c.Length == 3)
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        return new Vector3(float.Parse(c[0], ci), float.Parse(c[1], ci), float.Parse(c[2], ci));
                    }
                }
                var pts = new List<Vector3>();
                foreach (var cp in UnityEngine.Object.FindObjectsOfType<CanProcessor>()) { try { pts.Add(cp.point.position); } catch { } }
                foreach (var rm in UnityEngine.Object.FindObjectsOfType<RewardMachine>()) { try { pts.Add(rm.transform.position); } catch { } }
                if (pts.Count == 0) return null;
                Vector3 sum = Vector3.zero;
                foreach (var p in pts) sum += p;
                Vector3 center = sum / pts.Count;
                RaycastHit hit;
                if (Physics.Raycast(center + Vector3.up * 15f, Vector3.down, out hit, 60f)) center = hit.point;
                return center + Vector3.up * 1.2f;
            }
            catch (Exception e) { MpLog.Write("FactoryCenter: " + e.Message); return null; }
        }

        void TeleportToCenter()
        {
            try
            {
                var pos = FactoryCenter();
                if (!pos.HasValue || Player.Instance == null) { MpLog.Write("Tubo: no se pudo calcular el destino"); return; }
                var go = new GameObject("MP_Teleport");
                go.transform.position = pos.Value;
                go.transform.rotation = Player.Instance.transform.rotation;
                Player.Instance.Teleport(go.transform);
                UnityEngine.Object.Destroy(go, 2f);
                MpLog.Write("Tubo: teletransportado a " + pos.Value);
            }
            catch (Exception e) { LoggerInstance.Error("TeleportToCenter: " + e); }
        }

        void DrawVoteBanner()
        {
            if (!Connected || !InGame) return;
            int yes = VotesYes();
            if (yes == 0) return;
            GUI.Box(new Rect(Screen.width / 2f - 230f, 80f, 460f, 30f), "Votación: saltar el día  " + yes + "/" + (peers.Count + 1) + "   (F7 para votar)");
        }

        /// <summary>El invitado no puede ganar dinero por su cuenta: si el suyo SUBE sin que lo mande el líder, se deshace.</summary>
        public void RestoreMoneyTo(long v)
        {
            try
            {
                if (MoneyManager.Instance == null || (long)MoneyManager.Instance.Money <= v) return;
                applying = true;
                try { MoneyManager.Instance.SetMoney(v); } finally { applying = false; }
                lastMoney = (long)MoneyManager.Instance.Money;
            }
            catch { }
        }

        public void OnNextDayStarted()
        {
            ResetVotes();
            goDone = false; inElev = false;
            foreach (var p in peers.Values) p.InElev = false;
        }

        public void OnGameStarted()
        {
            InGame = true;
            OnNextDayStarted();
        }

        public void OnMenuShown()
        {
            InGame = false;
        }

        // =============== AVATARES ===============

        void SendPosition(float now)
        {
            if (now < nextPos || Player.Instance == null)
                return;

            nextPos = now + 0.1f;

            try
            {
                var t = Player.Instance.transform;

                float yaw = t.eulerAngles.y;

                var cam = Player.Instance.mainCamera;

                if (cam != null)
                    yaw = cam.transform.eulerAngles.y;

                var ci = System.Globalization.CultureInfo.InvariantCulture;

                Send(
                    "POS",
                    t.position.x.ToString("F2", ci),
                    t.position.y.ToString("F2", ci),
                    t.position.z.ToString("F2", ci),
                    yaw.ToString("F0", ci),
                    myName
                );
            }
            catch { }
        }

        void OnPos(string from, string[] a)
        {
            if (a.Length < 5)
                return;

            var ci = System.Globalization.CultureInfo.InvariantCulture;

            if (!avatars.TryGetValue(from, out var av))
            {
                av = new Avatar();
                avatars[from] = av;
            }

            av.Pos = new Vector3(
                float.Parse(a[0], ci),
                float.Parse(a[1], ci),
                float.Parse(a[2], ci)
            );

            av.Yaw = float.Parse(a[3], ci);
            av.Name = a[4];
        }

        void UpdateAvatars()
        {
            foreach (var av in avatars.Values)
            {
                if (av.Go == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);

                    go.name = "MP_Avatar";

                    var col = go.GetComponent<Collider>();

                    if (col != null)
                        UnityEngine.Object.Destroy(col);

                    go.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);

                    var r = go.GetComponent<Renderer>();

                    if (r != null)
                        r.material.color = new Color(1f, 0.5f, 0.1f);

                    go.transform.position = av.Pos;

                    av.Go = go;
                }

                var target = av.Pos + Vector3.up * avatarYOffset.Value;

                av.Go.transform.position = Vector3.Lerp(
                    av.Go.transform.position,
                    target,
                    0.35f
                );

                av.Go.transform.rotation = Quaternion.Euler(
                    0f,
                    av.Yaw,
                    0f
                );
            }
        }

        void RemoveAvatar(string id)
        {
            if (avatars.TryGetValue(id, out var av))
            {
                if (av.Go != null)
                    UnityEngine.Object.Destroy(av.Go);

                avatars.Remove(id);
            }
        }

        // =============== ESTADO DEL LÍDER ===============

        void SendState(string target)
        {
            if (GameManager.Instance == null ||
                MoneyManager.Instance == null)
                return;

            long money = (long)MoneyManager.Instance.Money;

            int day = 0;

            try
            {
                day = (int)SaveManager.Instance.CurrentSaveData.day;
            }
            catch { }

            var weapons = new List<string>();

            try
            {
                foreach (var w in Inventory.Instance.unlockedWeapons)
                    weapons.Add(w.name);
            }
            catch { }

            var skills = new List<string>();

            foreach (var s in Resources.FindObjectsOfTypeAll<Skill>())
            {
                if (s.gameObject.scene.IsValid() && s.isPurchased)
                    skills.Add(((int)s.skillId).ToString());
            }

            Send(
                "STATE",
                target,
                money.ToString(),
                day.ToString(),
                string.Join(",", weapons),
                string.Join(",", skills)
            );
        }

        void ApplyState(string[] a)
        {
            applying = true;

            try
            {
                MoneyManager.Instance.SetMoney(long.Parse(a[1]));

                try
                {
                    SaveManager.Instance.CurrentSaveData.day = int.Parse(a[2]);
                }
                catch { }

                foreach (var w in a[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    UnlockWeaponByName(w);

                foreach (var s in a[4].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    ApplySkillById(int.Parse(s));
            }
            catch (Exception e)
            {
                LoggerInstance.Error("ApplyState: " + e);
            }
            finally
            {
                applying = false;
            }

            lastMoney = (long)MoneyManager.Instance.Money;
            status = "Sincronizado con el líder";
        }

        void ApplyMoney(long delta)
        {
            applying = true;

            try
            {
                MoneyManager.Instance.ChangeMoney(delta);
            }
            finally
            {
                applying = false;
            }

            lastMoney = (long)MoneyManager.Instance.Money;
            if (moneyLogs++ < 60) MpLog.Write("MONEY recibido " + delta + " (total " + lastMoney + ")");
        }

        void UnlockWeaponByName(string name)
        {
            if (Inventory.Instance == null)
                return;

            bool prev = applying;
            applying = true;

            try
            {
                foreach (var w in Resources.FindObjectsOfTypeAll<Weapon>())
                {
                    if (w.name == name && w.gameObject.scene.IsValid())
                    {
                        Inventory.Instance.UnlockWeapon(w);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Error("UnlockWeapon: " + e);
            }
            finally
            {
                applying = prev;
            }
        }

        void ApplySkillById(int id)
        {
            bool prev = applying;
            applying = true;

            try
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<Skill>())
                {
                    if (s.gameObject.scene.IsValid() &&
                        (int)s.skillId == id &&
                        !s.isPurchased)
                    {
                        s.isPurchased = true;
                        s.ApplySkill();
                    }
                }

                foreach (var n in Resources.FindObjectsOfTypeAll<SkillNode>())
                {
                    if (n.gameObject.scene.IsValid())
                        n.RefreshLockState();
                }

                foreach (var tv in Resources.FindObjectsOfTypeAll<SkillTreeView>())
                {
                    if (tv.gameObject.scene.IsValid())
                        tv.RefreshNodeLockStates();
                }

                foreach (var pu in Resources.FindObjectsOfTypeAll<PermanentUpgrade>())
                {
                    if (pu.gameObject.scene.IsValid())
                        pu.ApplyInitialState();
                }
                MpLog.Write("Mejora aplicada desde otro jugador: skillId=" + id);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("ApplySkill: " + e);
            }
            finally
            {
                applying = prev;
            }
        }

        // =============== HOOKS LOCALES ===============

        public void OnLocalWeapon(string name)
        {
            if (Connected && synced && !applying)
                Send("WEAPON", name);
        }

        public void OnLocalSkill(int id)
        {
            if (Connected && synced && !applying)
            {
                Send("SKILL", id.ToString());
                MpLog.Write("Mejora comprada: skillId=" + id + " (enviada a todos)");
            }
        }

        public void OnLocalElevator(bool inside)
        {
            if (!Connected || goDone)
                return;

            inElev = inside;

            Send("ELEV", inside ? "1" : "0");

            if (IsHost)
                TryGo();
        }

        // =============== ASCENSOR ===============

        public bool AllIn()
        {
            return inElev && peers.Values.All(p => p.InElev);
        }

        public void RequestGo()
        {
            if (IsHost)
                TryGo();
            else
                Send("WANTGO");
        }

        void TryGo()
        {
            if (goDone || !AllIn())
                return;

            Send("GO");
            DoGo();
        }

        void DoGo()
        {
            if (goDone && Bypass) return;
            if (GameManager.Instance == null || !InGame)
                return;

            goDone = true;
            MpLog.Write("Ascensor: todos dentro, se termina el día");
            Bypass = true;

            try
            {
                GameManager.Instance.OnTriggerInsideElevator();
            }
            catch (Exception e)
            {
                LoggerInstance.Error("GO: " + e);
            }
            finally
            {
                Bypass = false;
            }
        }

        // =============== PANEL IMGUI ===============

        public override void OnGUI()
        {
            var e = Event.current;

            if (e != null &&
                e.type == EventType.KeyDown &&
                e.keyCode == KeyCode.F6)
            {
                showUi = !showUi;
                e.Use();
            }

            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F7 && Connected && InGame)
            {
                ToggleVote();
                e.Use();
            }

            if (e != null && e.type == EventType.KeyDown && showUi && (idInputFocused || ipInputFocused))
                HandleTyping(e);

            if (e != null && e.type == EventType.Repaint)
            {
                DrawVoteBanner();

                if (Camera.main != null)
                {
                    foreach (var av in avatars.Values)
                    {
                        if (av.Go == null)
                            continue;

                        var sp = Camera.main.WorldToScreenPoint(
                            av.Go.transform.position + Vector3.up * 1.4f
                        );

                        if (sp.z > 0f)
                        {
                            GUI.Label(
                                new Rect(
                                    sp.x - 70f,
                                    Screen.height - sp.y - 10f,
                                    140f,
                                    22f
                                ),
                                av.Name
                            );
                        }
                    }
                }

                if (Time.realtimeSinceStartup < noticeUntil)
                {
                    GUI.Box(
                        new Rect(
                            Screen.width / 2f - 200f,
                            40f,
                            400f,
                            32f
                        ),
                        notice
                    );
                }
            }

            if (!showUi)
                return;

            // GUILayout.Window NO existe en este juego (MissingMethodException): usamos GUI.Window
            // con una altura fija según lo que se esté mostrando.
            win.height = !Connected
                ? (showIp ? 760f : 470f)
                : 560f + 24f * peers.Count;

            try
            {
                win = GUI.Window(
                    94001,
                    win,
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<GUI.WindowFunction>(
                        (Action<int>)DrawWindow
                    ),
                    testInstance ? "Multijugador (F6) - PRUEBA: " + testName : "Multijugador (F6)"
                );
            }
            catch (Exception ex)
            {
                if (!guiErrLogged) { guiErrLogged = true; LoggerInstance.Error("OnGUI: " + ex); }
            }
        }

        // =============== VENTANA DEL MOD ===============

        bool drawErrLogged;

        // GUILayout.Space está "stripped" en este juego: una etiqueta vacía hace de separador
        void Gap() { GUILayout.Label(" "); }

        void DrawWindow(int id)
        {
            try { DrawWindowInner(id); }
            catch (Exception e)
            {
                if (!drawErrLogged) { drawErrLogged = true; MelonLogger.Error("DrawWindow: " + e); }
            }
        }

        void DrawWindowInner(int id)
        {
            if (!Connected)
            {
                GUILayout.Label("Sin sala. Crea una o únete a la de un amigo.");

                if (GUILayout.Button("Crear sala (Steam)"))
                    CreateRoom();

                Gap();
                GUILayout.Label("UNIRSE POR ID DE SALA (Steam)");
                GUILayout.Label("ID: " + (string.IsNullOrEmpty(lobbyIdText) ? "(vacío)" : lobbyIdText));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Pegar ID"))
                    PasteInto(false);
                if (GUILayout.Button(idInputFocused ? "Terminar de escribir" : "Escribir ID"))
                {
                    idInputFocused = !idInputFocused;
                    ipInputFocused = false;
                }
                if (GUILayout.Button("Borrar"))
                    lobbyIdText = "";
                GUILayout.EndHorizontal();

                if (idInputFocused)
                    GUILayout.Label("Escribe los números con el teclado (Ctrl+V pega, Enter termina).");

                if (GUILayout.Button("UNIRSE A LA SALA"))
                {
                    if (ulong.TryParse(lobbyIdText.Trim(), out var jid) && jid != 0)
                    {
                        idInputFocused = false;
                        JoinRoom(jid);
                    }
                    else status = "Introduce un ID válido.";
                }

                Gap();
                if (GUILayout.Button(showIp ? "▼ Ocultar opción por IP" : "▶ Opción por IP (avanzado, opcional)"))
                    showIp = !showIp;

                if (showIp)
                {
                    GUILayout.Label("Online por IP: el líder debe abrir el puerto " + ipPort.Value + " en su router (o usar Radmin/Hamachi).");

                    if (GUILayout.Button("Crear sala por IP (soy el líder)"))
                        CreateIpRoom();

                    if (GUILayout.Button("Probar en este PC: crear sala + abrir 2ª ventana"))
                    {
                        CreateIpRoom();
                        if (net != null) LaunchSecondWindow();
                    }

                    GUILayout.Label("IP del líder: " + (string.IsNullOrEmpty(ipText) ? "(vacío)" : ipText));

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Pegar IP"))
                        PasteInto(true);
                    if (GUILayout.Button(ipInputFocused ? "Terminar de escribir" : "Escribir IP"))
                    {
                        ipInputFocused = !ipInputFocused;
                        idInputFocused = false;
                    }
                    if (GUILayout.Button("Borrar"))
                        ipText = "";
                    GUILayout.EndHorizontal();

                    if (GUILayout.Button("UNIRSE POR IP"))
                    {
                        ipInputFocused = false;
                        JoinIpRoom(ipText);
                    }
                }

                GUILayout.Label("Estado: " + status);
            }
            else
            {
                GUILayout.Label(status);
                GUILayout.Label("Rol: " + (IsHost ? "LÍDER (su partida manda)" : "Invitado"));

                if (net.IsSteam)
                {
                    if (net.LobbyId != 0)
                    {
                        GUILayout.Label("ID DE SALA (un clic copia):");

                        if (GUILayout.Button(net.LobbyId.ToString()))
                            CopyId();

                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("Copiar ID"))
                            CopyId();
                        if (IsHost && GUILayout.Button("Invitar amigos (Steam)"))
                            net.OpenInvite();
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        GUILayout.Label("Creando/uniéndose a la sala en Steam...");
                    }
                }
                else
                {
                    GUILayout.Label("Conexión por IP: " + net.Info);
                }

                Gap();
                GUILayout.Label("Jugadores (" + (peers.Count + 1) + "):");
                GUILayout.Label("  • " + myName + (IsHost ? " (líder, tú)" : " (tú)"));
                foreach (var p in peers.Values)
                    GUILayout.Label("  • " + p.Name);

                if (!InGame)
                {
                    GUILayout.Label(IsHost
                        ? "Pulsa Continuar o Nueva partida: se cargará a todos."
                        : "Esperando a que el líder inicie la partida...");
                }
                else
                {
                    GUILayout.Label("En el ascensor: " + (inElev ? "SÍ" : "no") + " | Todos dentro: " + (AllIn() ? "SÍ" : "no"));
                }

                {
                    var bt = net as BatchTransport;
                    if (bt != null && GUILayout.Button("Simular lag (solo pruebas): " + (simLagMs == 0 ? "NO" : simLagMs + " ms por sentido (ping +" + simLagMs * 2 + ")")))
                    {
                        int idx = Array.IndexOf(SimSteps, simLagMs);
                        simLagMs = SimSteps[(idx + 1) % SimSteps.Length];
                        bt.SimDelay = simLagMs / 1000.0;
                        bt.SimJitter = simLagMs / 1000.0 * 0.25;
                        Notice(simLagMs == 0 ? "Lag simulado desactivado" : "Lag simulado: +" + simLagMs * 2 + " ms de ping en esta ventana");
                    }

                    if (bt != null)
                        GUILayout.Label("Red: ↑" + bt.OutKBs.ToString("F1") + " KB/s  ↓" + bt.InKBs.ToString("F1") + " KB/s  " + bt.PacketsPerSec.ToString("F0") + " paq/s  Ping: " + (PingMs >= 0 ? PingMs.ToString("F0") + " ms" : "?"));
                }

                if (InGame && GUILayout.Button((myVote ? "[SÍ] " : "[NO] ") + "Votar saltar el día (F7)  " + VotesYes() + "/" + (peers.Count + 1)))
                    ToggleVote();

                GUILayout.Label("Sync: " + (synced ? "OK" : "esperando al líder...") + " | Fábrica compartida: " + (ShareFactory ? "sí" : "NO (ShareFactory=false)"));
                if (ShareFactory && synced)
                    GUILayout.Label(FactorySync.Status());

                if (GUILayout.Button("Salir de la sala"))
                    Disconnect();
            }

            GUI.DragWindow();
        }
    }

    /// <summary>Portapapeles fiable en Windows (el de Unity a veces falla dentro del juego).</summary>
    public static class Clip
    {
        public static void Set(string text)
        {
            try { GUIUtility.systemCopyBuffer = text; } catch { }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command Set-Clipboard -Value '" + text.Replace("'", "") + "'")
                { CreateNoWindow = true, UseShellExecute = false };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        public static string Get()
        {
            string t = "";
            try { t = GUIUtility.systemCopyBuffer; } catch { }
            if (!string.IsNullOrEmpty(t)) return t;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command Get-Clipboard")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    return o ?? "";
                }
            }
            catch { return ""; }
        }
    }

    // =====================================================================
    // PARCHES HARMONY
    // =====================================================================

    [HarmonyPatch]
    public static class Patches
    {
        public static void Trace(MethodBase __originalMethod)
        {
            MelonLogger.Msg(
                "[TRACE] GameManager." +
                __originalMethod.Name +
                " (Bypass=" +
                (Mod.I != null && Mod.I.Bypass) +
                ")"
            );
        }

        public static bool Gate(MethodBase __originalMethod)
        {
            var m = Mod.I;

            if (m == null || !m.Connected || m.Bypass)
                return true;

            if (m.AllIn() && m.IsHost)
            {
                m.RequestGo();
                return false;
            }

            m.RequestGo();
            return false;
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnTriggerInsideElevator))]
        [HarmonyPrefix]
        public static bool ElevInPre()
        {
            return Mod.I == null || Mod.I.ElevatorEnter();
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.NextDay))]
        [HarmonyPrefix]
        public static bool NextDayPre()
        {
            return Mod.I == null || Mod.I.NextDayRequested();
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.NextDay))]
        [HarmonyPostfix]
        public static void NextDayPost()
        {
            Mod.I?.OnNextDayStarted();
        }

        [HarmonyPatch(typeof(GameManager), "Suicide")]
        [HarmonyPrefix]
        public static bool SuicidePre(object[] __args)
        {
            return Mod.I == null || Mod.I.OnTubeUsed(__args);
        }

        [HarmonyPatch(typeof(GameManager), "UpdatePause")]
        [HarmonyPostfix]
        public static void PausePost()
        {
            var m = Mod.I;
            if (m != null && m.Connected && m.InGame)
                Time.timeScale = 1f;
        }

        [HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.OnClick))]
        [HarmonyPrefix]
        public static void PermPre(PermanentUpgrade __instance, out bool __state)
        {
            __state = __instance.skill != null && __instance.skill.isPurchased;
        }

        [HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.OnClick))]
        [HarmonyPostfix]
        public static void PermPost(PermanentUpgrade __instance, bool __state)
        {
            if (!__state && __instance.skill != null && __instance.skill.isPurchased)
                Mod.I?.OnLocalSkill((int)__instance.skill.skillId);
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnTriggerOutsideElevator))]
        [HarmonyPostfix]
        public static void ElevOut()
        {
            Mod.I?.OnLocalElevator(false);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.UnlockWeapon))]
        [HarmonyPostfix]
        public static void Unlock(object[] __args)
        {
            try
            {
                var w = __args[0] as UnityEngine.Object;

                if (w != null)
                    Mod.I?.OnLocalWeapon(w.name);
            }
            catch { }
        }

        [HarmonyPatch(typeof(SkillNode), nameof(SkillNode.OnClick))]
        [HarmonyPrefix]
        public static void SkillPre(
            SkillNode __instance,
            out bool __state)
        {
            __state =
                __instance.skill != null &&
                __instance.skill.isPurchased;
        }

        [HarmonyPatch(typeof(SkillNode), nameof(SkillNode.OnClick))]
        [HarmonyPostfix]
        public static void SkillPost(
            SkillNode __instance,
            bool __state)
        {
            if (!__state &&
                __instance.skill != null &&
                __instance.skill.isPurchased)
            {
                Mod.I?.OnLocalSkill((int)__instance.skill.skillId);
            }
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.ContinueGame))]
        [HarmonyPrefix]
        public static bool MenuContinue()
        {
            return MenuStart(true);
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.NewGame))]
        [HarmonyPrefix]
        public static bool MenuNew()
        {
            return MenuStart(false);
        }

        static bool MenuStart(bool cont)
        {
            var m = Mod.I;

            if (m == null || !m.Connected || m.Bypass)
                return true;

            if (!m.IsHost)
            {
                m.Notice("Solo el líder puede iniciar la partida");
                return false;
            }

            m.LeaderStarting(cont);
            return true;
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartGame))]
        [HarmonyPostfix]
        public static void GameStarted()
        {
            Mod.I?.OnGameStarted();
        }

        [HarmonyPatch(typeof(Menu), "Awake")]
        [HarmonyPostfix]
        public static void MenuAwake()
        {
            Mod.I?.OnMenuShown();
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
        [HarmonyPrefix]
        public static bool NoSaveForGuests()
        {
            return !(Mod.I != null &&
                     Mod.I.Connected &&
                     !Mod.I.IsHost);
        }
    }
}