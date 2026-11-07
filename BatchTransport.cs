using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CanEmThogether
{
    /// <summary>
    /// Optimización de red (v0.6.5). Envuelve cualquier transporte (Steam o IP) y:
    ///  1. AGRUPA todos los mensajes de un mismo instante en UN solo envío (antes: decenas de llamadas por segundo).
    ///  2. COMPRIME el paquete (Deflate + Base64): los mensajes son texto muy repetitivo, se reduce 3-6 veces.
    ///  3. Limita la frecuencia de envío (por defecto ~14 paquetes/s). Los eventos urgentes (golpes, soltar/lanzar,
    ///     comprar mejoras, ascensor...) salen en el siguiente fotograma sin esperar.
    /// El chat de las salas de Steam tiene límites de tamaño (4 KB) y de ritmo; con esto se evita el retraso acumulado.
    /// </summary>
    public class BatchTransport : ITransport
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static double Now { get { return Clock.Elapsed.TotalSeconds; } }

        // Se envían tal cual (ya son grandes/opacos) y sin esperar
        static readonly HashSet<string> RawTypes = new HashSet<string> { "SAVE", "START" };
        // Provocan un envío inmediato (en el siguiente fotograma)
        static readonly HashSet<string> UrgentTypes = new HashSet<string>
        {
            "EFWD", "ESTORE", "ETHROW", "EREL", "CREL", "CSTORE", "CTHROW", "GO", "NEXTDAY", "SKILL", "WEAPON",
            "MONEY", "ELEV", "WANTGO", "VOTE", "SKIPDAY", "BYE", "HELLO", "READY", "STATE", "LAT", "LATR"
        };

        readonly ITransport inner;
        readonly List<string> pending = new List<string>();
        readonly Queue<string> rx = new Queue<string>();
        readonly double interval;
        bool urgent;
        double lastFlush;

        // ---- estadísticas (para el panel F6 y el log) ----
        public long BytesOut, BytesIn, PacketsOut, LinesOut, LinesIn;
        public float OutKBs, InKBs, PacketsPerSec;
        double rateT; long rateOut0, rateIn0, ratePk0;

        // ---- SIMULADOR DE LAG (solo para pruebas en local): retrasa lo que ESTA ventana envía y lo que recibe ----
        public double SimDelay;       // segundos por sentido (0 = desactivado)
        public double SimJitter;      // variación aleatoria extra, en segundos
        readonly Queue<KeyValuePair<double, string>> txDelayed = new Queue<KeyValuePair<double, string>>();
        readonly Queue<KeyValuePair<double, string>> rxDelayed = new Queue<KeyValuePair<double, string>>();
        double lastTxDue, lastRxDue;
        static readonly Random Rng = new Random();

        public BatchTransport(ITransport inner, double intervalSeconds)
        {
            this.inner = inner;
            this.interval = Math.Max(0.02, intervalSeconds);
        }

        public bool IsHost { get { return inner.IsHost; } }
        public bool IsSteam { get { return inner.IsSteam; } }
        public bool Ready { get { return inner.Ready; } }
        public string Info { get { return inner.Info; } }
        public ulong LobbyId { get { return inner.LobbyId; } }
        public void OpenInvite() { inner.OpenInvite(); }

        public void Poll()
        {
            inner.Poll();
            double now = Now;
            while (txDelayed.Count > 0 && txDelayed.Peek().Key <= now) RealSend(txDelayed.Dequeue().Value);
            if (now - rateT >= 2.0)
            {
                double dt = now - rateT; if (rateT == 0) dt = 2.0;
                OutKBs = (float)((BytesOut - rateOut0) / 1024.0 / dt);
                InKBs = (float)((BytesIn - rateIn0) / 1024.0 / dt);
                PacketsPerSec = (float)((PacketsOut - ratePk0) / dt);
                rateOut0 = BytesOut; rateIn0 = BytesIn; ratePk0 = PacketsOut; rateT = now;
            }
        }

        static string TypeOf(string line)
        {
            int i = line.IndexOf('|');
            if (i < 0) return "";
            int j = line.IndexOf('|', i + 1);
            return j < 0 ? line.Substring(i + 1) : line.Substring(i + 1, j - i - 1);
        }

        public void Send(string line)
        {
            string type = TypeOf(line);
            if (RawTypes.Contains(type)) { SendPacket(line); LinesOut++; return; }
            pending.Add(line);
            if (UrgentTypes.Contains(type)) urgent = true;
        }

        public void Flush() { Flush(false); }

        public void Flush(bool force)
        {
            if (pending.Count == 0) return;
            double now = Now;
            if (!force && !urgent && now - lastFlush < interval) return;
            lastFlush = now; urgent = false;
            var lines = new List<string>(pending);
            pending.Clear();
            SendLines(lines);
        }

        void SendLines(List<string> lines)
        {
            string s = Encode(lines);
            if (s.Length > 3600 && lines.Count > 1)
            {   // el chat de Steam admite ~4 KB por mensaje: se parte en dos
                int h = lines.Count / 2;
                SendLines(lines.GetRange(0, h));
                SendLines(lines.GetRange(h, lines.Count - h));
                return;
            }
            SendPacket(s);
            LinesOut += lines.Count;
        }

        void SendPacket(string s)
        {
            if (SimDelay > 0)
            {
                double due = Math.Max(lastTxDue, Now + SimDelay + Rng.NextDouble() * SimJitter);   // el orden se conserva
                lastTxDue = due;
                txDelayed.Enqueue(new KeyValuePair<double, string>(due, s));
            }
            else RealSend(s);
        }

        void RealSend(string s)
        {
            inner.Send(s);
            PacketsOut++;
            BytesOut += s.Length;
        }

        static string Encode(List<string> lines)
        {
            string joined = lines.Count == 1 ? lines[0] : string.Join("\n", lines.ToArray());
            if (lines.Count == 1 && joined.Length < 300) return joined;     // pequeño: sin comprimir (empieza por una cifra hex, nunca por 'Z')
            byte[] raw = Encoding.UTF8.GetBytes(joined);
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true)) { ds.Write(raw, 0, raw.Length); }
                string z = "Z" + Convert.ToBase64String(ms.ToArray());
                if (lines.Count == 1 && z.Length >= joined.Length) return joined;
                return z;
            }
        }

        static void Decode(string s, Queue<string> into)
        {
            if (s.Length > 1 && s[0] == 'Z')
            {
                byte[] data = Convert.FromBase64String(s.Substring(1));
                using (var ms = new MemoryStream(data))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var sr = new StreamReader(ds, Encoding.UTF8))
                {
                    string all = sr.ReadToEnd();
                    foreach (var l in all.Split('\n')) if (l.Length > 0) into.Enqueue(l);
                }
            }
            else into.Enqueue(s);
        }

        public bool TryReceive(out string line)
        {
            string raw;
            while (inner.TryReceive(out raw))
            {
                BytesIn += raw.Length;
                if (SimDelay > 0 || rxDelayed.Count > 0)
                {
                    double due = Math.Max(lastRxDue, Now + SimDelay + Rng.NextDouble() * SimJitter);
                    lastRxDue = due;
                    rxDelayed.Enqueue(new KeyValuePair<double, string>(due, raw));
                }
                else
                {
                    try { Decode(raw, rx); } catch { /* paquete dañado: se ignora */ }
                }
            }

            double now = Now;
            while (rxDelayed.Count > 0 && rxDelayed.Peek().Key <= now)
            {
                try { Decode(rxDelayed.Dequeue().Value, rx); } catch { }
            }

            if (rx.Count > 0) { line = rx.Dequeue(); LinesIn++; return true; }
            line = null;
            return false;
        }

        public void Close()
        {
            try { Flush(true); } catch { }
            inner.Close();
        }
    }
}
