using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CanEmThogether
{
    /// <summary>
    /// OPCIONAL: conexión directa por IP (TCP), también sirve para jugar online.
    /// El líder debe tener el puerto abierto en su router (port forwarding) o usar una VPN (Radmin/Hamachi).
    /// El método principal sigue siendo el ID de sala de Steam.
    /// </summary>
    public class TcpTransport : ITransport
    {
        class Conn { public TcpClient C; public StreamWriter W; }

        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        readonly List<Conn> conns = new List<Conn>();
        TcpListener listener;
        volatile bool running = true;
        volatile bool connected;
        string connectError;

        public bool IsHost { get; private set; }
        public bool IsSteam { get { return false; } }
        public bool Ready { get { return connected; } }
        public ulong LobbyId { get { return 0UL; } }
        public string Info { get; private set; } = "";

        public static TcpTransport Host(int port)
        {
            var t = new TcpTransport { IsHost = true, Info = "Sala por IP abierta en el puerto " + port };
            t.listener = new TcpListener(IPAddress.Any, port);
            t.listener.Start();
            t.connected = true;
            new Thread(t.AcceptLoop) { IsBackground = true }.Start();
            return t;
        }

        /// <summary>Se conecta en segundo plano (no congela el juego). El estado se ve en Info/Ready.</summary>
        public static TcpTransport Join(string host, int port)
        {
            var t = new TcpTransport { IsHost = false, Info = "Conectando a " + host + ":" + port + "..." };
            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient();
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(8000)) throw new TimeoutException("tiempo agotado (¿IP/puerto correctos y puerto abierto?)");
                    c.EndConnect(ar);
                    t.AddConn(c);
                    t.Info = "Conectado por IP a " + host + ":" + port;
                    t.connected = true;
                }
                catch (Exception e) { t.connectError = e.Message; t.Info = "No se pudo conectar: " + e.Message; }
            }) { IsBackground = true }.Start();
            return t;
        }

        void AcceptLoop()
        {
            while (running)
            {
                try { AddConn(listener.AcceptTcpClient()); }
                catch { if (!running) break; }
            }
        }

        void AddConn(TcpClient c)
        {
            c.NoDelay = true;
            var cn = new Conn { C = c, W = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true } };
            lock (conns) conns.Add(cn);
            new Thread(() => ReadLoop(cn)) { IsBackground = true }.Start();
        }

        void ReadLoop(Conn cn)
        {
            try
            {
                var r = new StreamReader(cn.C.GetStream(), Encoding.UTF8);
                string l;
                while (running && (l = r.ReadLine()) != null)
                {
                    inbox.Enqueue(l);
                    if (IsHost) WriteAll(l, cn);   // el líder reenvía a los demás
                }
            }
            catch { }
            lock (conns) conns.Remove(cn);
        }

        void WriteAll(string l, Conn except)
        {
            Conn[] arr; lock (conns) arr = conns.ToArray();
            foreach (var c in arr)
            {
                if (c == except) continue;
                try { lock (c) c.W.WriteLine(l); } catch { }
            }
        }

        public void Send(string line) { if (connected) WriteAll(line, null); }
        public bool TryReceive(out string line) { return inbox.TryDequeue(out line); }
        public void Poll() { }
        public void OpenInvite() { }
        public void Flush() { }

        public void Close()
        {
            running = false; connected = false;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (conns) { foreach (var c in conns) { try { c.C.Close(); } catch { } } conns.Clear(); }
        }
    }
}
