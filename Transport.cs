using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppSteamworks;
using Il2CppSteamworks.Data;
using MelonLoader;

namespace CanEmThogether
{
    /// <summary>Canal de mensajes de texto (una línea por mensaje). Lo usan Steam (método principal) y TCP por IP (opcional).</summary>
    public interface ITransport
    {
        bool IsHost { get; }
        bool IsSteam { get; }
        bool Ready { get; }          // true cuando ya se puede enviar (sala creada/unida o socket conectado)
        string Info { get; }
        ulong LobbyId { get; }       // solo Steam
        void Send(string line);
        bool TryReceive(out string line);
        void Poll();
        void OpenInvite();
        void Flush();
        void Close();
    }

    /// <summary>Salas de Steam usando el Facepunch.Steamworks que ya lleva el juego.</summary>
    public class SteamTransport : ITransport
    {
        static SteamTransport Current;
        static bool eventsHooked;
        // Lobby recibido por el evento de Steam (plan B si el resultado de la tarea llega con ID 0)
        static Lobby enteredLobby;
        static bool enteredValid;

        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        Lobby lobby;
        bool hasLobby;
        Func<bool> pending;
        Func<string> describe;
        DateTime startedAt;
        bool pumped, pumpFailed, timedOut, diagLogged;

        public bool IsHost { get; private set; }
        public bool IsSteam { get { return true; } }
        public bool Ready { get { return hasLobby; } }
        public string Info { get; private set; } = "";
        public ulong LobbyId { get { return hasLobby ? lobby.Id.Value : 0UL; } }

        public static void InitEvents(Action<ulong> onJoinRequested)
        {
            if (eventsHooked) return;
            eventsHooked = true;

            SteamMatchmaking.OnChatMessage += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Lobby, Friend, string>>(
                new Action<Lobby, Friend, string>((l, f, msg) =>
                {
                    var c = Current;
                    if (c != null && c.hasLobby && l.Id.Value == c.lobby.Id.Value) c.inbox.Enqueue(msg);
                }));

            SteamMatchmaking.OnLobbyEntered += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Lobby>>(
                new Action<Lobby>(l =>
                {
                    try
                    {
                        enteredLobby = l;
                        enteredValid = l.Id.Value != 0UL;
                        MelonLogger.Msg("Steam: OnLobbyEntered, ID " + l.Id.Value);
                    }
                    catch (Exception ex) { MelonLogger.Warning("OnLobbyEntered: " + ex.Message); }
                }));

            SteamFriends.OnGameLobbyJoinRequested += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Lobby, SteamId>>(
                new Action<Lobby, SteamId>((l, friend) => { onJoinRequested(l.Id.Value); }));
        }

        public static SteamTransport Create(int maxPlayers)
        {
            var t = new SteamTransport { IsHost = true, Info = "Creando sala...", startedAt = DateTime.UtcNow };
            enteredValid = false;
            MelonLogger.Msg("Steam: creando sala (SteamManager.IsReady=" + Il2Cpp.SteamManager.IsReady + ")");
            try
            {
                var task = SteamMatchmaking.CreateLobbyAsync(maxPlayers);
                t.describe = () => task.Status.ToString();
                t.pending = () =>
                {
                    if (!task.IsCompleted) return false;
                    try
                    {
                        if (task.IsFaulted) { t.Info = "Error Steam: " + task.Exception; MelonLogger.Error(t.Info); return true; }
                        if (task.IsCanceled) { t.Info = "Steam: creación cancelada"; MelonLogger.Error(t.Info); return true; }
                        var r = task.Result;
                        if (!r.HasValue) { t.Info = "Steam: no se pudo crear la sala"; MelonLogger.Error(t.Info); return true; }
                        var lb = r.Value;
                        ulong id = lb.Id.Value;
                        if (!t.diagLogged) { t.diagLogged = true; MelonLogger.Msg("Steam: tarea de crear sala terminada, ID=" + id); }
                        if (id == 0UL)
                        {
                            if (!enteredValid) return false;   // esperamos al evento OnLobbyEntered
                            lb = enteredLobby;
                            MelonLogger.Msg("Steam: ID tomado del evento: " + lb.Id.Value);
                        }
                        t.lobby = lb;
                        t.hasLobby = true;
                        t.lobby.SetData("cem", "1");
                        t.lobby.SetFriendsOnly();
                        t.lobby.SetJoinable(true);
                        t.Info = "Sala creada";
                        MelonLogger.Msg("Lobby creado. ID: " + t.LobbyId);
                        return true;
                    }
                    catch (Exception ex) { t.Info = "Error al obtener lobby: " + ex.Message; MelonLogger.Error(ex.ToString()); return true; }
                };
            }
            catch (Exception ex) { t.Info = "Error al iniciar Steam: " + ex.Message; MelonLogger.Error(ex.ToString()); }
            Current = t;
            return t;
        }

        public static SteamTransport Join(ulong id)
        {
            var t = new SteamTransport { IsHost = false, Info = "Uniéndose...", startedAt = DateTime.UtcNow };
            enteredValid = false;
            MelonLogger.Msg("Steam: uniéndose a la sala " + id);
            try
            {
                var task = SteamMatchmaking.JoinLobbyAsync(new SteamId { Value = id });
                t.describe = () => task.Status.ToString();
                t.pending = () =>
                {
                    if (!task.IsCompleted) return false;
                    try
                    {
                        if (task.IsFaulted) { t.Info = "Error Steam: " + task.Exception; MelonLogger.Error(t.Info); return true; }
                        var r = task.Result;
                        if (!r.HasValue) { t.Info = "Steam: no se pudo unir a la sala (¿ID correcto? ¿sois amigos en Steam?)"; MelonLogger.Error(t.Info); return true; }
                        var lb = r.Value;
                        if (lb.Id.Value == 0UL)
                        {
                            if (!enteredValid) return false;
                            lb = enteredLobby;
                            MelonLogger.Msg("Steam: ID tomado del evento: " + lb.Id.Value);
                        }
                        t.lobby = lb;
                        t.hasLobby = true;
                        t.Info = "Conectado a la sala";
                        return true;
                    }
                    catch (Exception ex) { t.Info = "Error al unirse: " + ex.Message; MelonLogger.Error(ex.ToString()); return true; }
                };
            }
            catch (Exception ex) { t.Info = "Error al iniciar Steam: " + ex.Message; MelonLogger.Error(ex.ToString()); }
            Current = t;
            return t;
        }

        void PumpCallbacks()
        {
            // Si el juego no procesa los callbacks de Steam, la tarea nunca termina: los procesamos nosotros.
            try
            {
                SteamClient.RunCallbacks();
                if (!pumped) { pumped = true; MelonLogger.Msg("Steam: procesando callbacks manualmente"); }
            }
            catch (Exception e)
            {
                if (!pumpFailed) { pumpFailed = true; MelonLogger.Warning("Steam RunCallbacks: " + e.Message); }
            }
        }

        public void Poll()
        {
            if (pending == null) return;
            double secs = (DateTime.UtcNow - startedAt).TotalSeconds;
            if (secs > 3.0) PumpCallbacks();
            if (pending()) { pending = null; return; }
            if (secs > 12.0 && !timedOut)
            {
                timedOut = true;
                Info = "Steam no responde (estado: " + (describe != null ? describe() : "?") + "). Sal de la sala y reinténtalo; abre el juego desde Steam.";
                MelonLogger.Error(Info);
            }
        }

        public void Flush() { }

        public void OpenInvite()
        {
            if (!hasLobby) return;
            try { SteamFriends.OpenGameInviteOverlay(lobby.Id); }
            catch (Exception ex) { MelonLogger.Error("Error al abrir invitaciones: " + ex); }
        }

        public void Send(string line) { if (hasLobby) lobby.SendChatString(line); }
        public bool TryReceive(out string line) { return inbox.TryDequeue(out line); }

        public void Close()
        {
            try { if (hasLobby) lobby.Leave(); }
            catch (Exception ex) { MelonLogger.Error("Error al salir de la sala: " + ex); }
            hasLobby = false;
            pending = null;
            if (Current == this) Current = null;
        }
    }
}
