using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2Cpp;

namespace CanEmThogether
{
    /// <summary>Log propio de cada ventana: UserData\CanEmThogether_&lt;principal|nombre de prueba&gt;.log (el Latest.log de MelonLoader no sirve con dos ventanas).</summary>
    public static class MpLog
    {
        static string path;
        static readonly object gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    if (path == null)
                    {
                        string dir;
                        try { dir = MelonLoader.Utils.MelonEnvironment.UserDataDirectory; }
                        catch { dir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "UserData"); }
                        System.IO.Directory.CreateDirectory(dir);
                        string tag = Mod.I != null ? Mod.I.LogTag : "principal";
                        path = System.IO.Path.Combine(dir, "CanEmThogether_" + tag + ".log");
                        System.IO.File.WriteAllText(path, "");   // se empieza de cero en cada partida
                    }
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// FÁBRICA COMPARTIDA (v0.2, SIN PROBAR).
    /// El LÍDER simula los bichos de verdad (camión, IA, muerte, máquinas trituradoras).
    /// Los INVITADOS ven "marionetas": copias de los bichos cuya posición/vida/muerte manda el líder.
    /// - Los golpes/disparos del invitado sobre una marioneta se reenvían al líder (EFWD).
    /// - Si el invitado carga un cadáver, él manda su posición al líder (EBODY) y al soltarlo se lanza (EREL).
    /// - Las latas y la venta las gestiona el líder; el dinero se reparte con el sistema de dinero ya existente.
    /// NO sincroniza: latas físicas, torretas/robots, cinta transportadora, efectos de impacto.
    /// </summary>
    public static class FactorySync
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        static Mod M => Mod.I;

        // ---------- LÍDER ----------
        static readonly Dictionary<IntPtr, int> hostIds = new Dictionary<IntPtr, int>();
        static readonly Dictionary<int, Enemy> hostById = new Dictionary<int, Enemy>();
        static readonly Dictionary<int, float> remoteHeld = new Dictionary<int, float>(); // id -> última vez que el invitado lo sujetó
        static readonly HashSet<int> storedIds = new HashSet<int>();                      // bichos guardados en la aspiradora de un invitado
        static readonly Dictionary<int, Vector3> lastPosH = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, float> lastPosT = new Dictionary<int, float>();
        static int nextId = 1;
        static float lastScan, lastSend, lastIds;
        static readonly Dictionary<int, string> lastLine = new Dictionary<int, string>();   // último dato enviado por bicho
        static readonly Dictionary<int, float> lastFull = new Dictionary<int, float>();     // cuándo se envió por última vez
        static int sentCount, recvCount, createdLogs, eventLogs, lastVacCount = -1, ragErr, fxDepth;
        static float lastDiag, lastVacDiag;
        public static string LastError = "";

        /// <summary>Resumen corto para el panel F6 (diagnóstico en pantalla).</summary>
        public static string Status()
        {
            if (M == null) return "";
            string s = M.IsHost
                ? "Fábrica: bichos=" + hostById.Count + " enviados=" + sentCount + " guardados=" + storedIds.Count
                : "Fábrica: marionetas=" + puppets.Count + " ESYNC recibidos=" + recvCount + " en mi aspiradora=" + detached.Count;
            s += " | " + CanSync.Status() + " | FPS=" + (int)M.Fps;
            if (missingPrefabs.Count > 0) s += " | sin prefab: " + string.Join(",", missingPrefabs.ToArray());
            if (LastError.Length > 0) s += " | error: " + LastError;
            return s;
        }

        // ---------- INVITADO ----------
        class Puppet
        {
            public Enemy E; public Rigidbody RB;
            public Vector3 Pos; public Quaternion Rot; public Vector3 Vel; public float RecvT;
            public bool Dead, HostHeld; public float CMS, Blend;
            public float Seen, NextBody, OwnedUntil, LastGrabEvent;
            public bool WasOwned, DeathKick, HasV;
            public Quaternion VRot; public float NearSince;
            public float GDmg; public bool Shiny;
        }
        static readonly Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();
        static readonly HashSet<IntPtr> puppetPtrs = new HashSet<IntPtr>();
        // bichos que están guardados en MI aspiradora (ya no los gestionamos hasta que los lance)
        static readonly Dictionary<IntPtr, KeyValuePair<int, Puppet>> detached = new Dictionary<IntPtr, KeyValuePair<int, Puppet>>();
        static readonly HashSet<string> missingPrefabs = new HashSet<string>();
        static bool animErr;

        // ---- v0.5: las máquinas funcionan también en la pantalla del invitado ----
        static readonly Dictionary<int, float> ghosts = new Dictionary<int, float>();   // bichos que mi trituradora local ya procesó (ignoro al líder hasta su EDEL)
        static CanProcessor[] procs = new CanProcessor[0];
        static float lastProcScan;
        public static int machineDepth;                                                // >0 mientras la trituradora local hace daño
        public static int rewardDepth;                                                 // >0 mientras la máquina de recompensa local vende una lata

        static bool NearProcessor(Vector3 pos, float extra)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastProcScan > 5f)
            {
                lastProcScan = now;
                try { procs = UnityEngine.Object.FindObjectsOfType<CanProcessor>(); } catch { }
            }
            foreach (var pr in procs)
            {
                try
                {
                    if (pr == null || pr.point == null) continue;
                    if ((pr.point.position - pos).magnitude < pr.radius + extra) return true;
                }
                catch { }
            }
            return false;
        }

        public static bool IsPuppet(Enemy e) { return e != null && puppetPtrs.Contains(e.Pointer); }
        static bool GuestOn() { return M != null && M.Connected && !M.IsHost; }

        public static void Reset()
        {
            foreach (var id in puppets.Keys.ToList()) { try { RemovePuppet(id); } catch { } }
            hostIds.Clear(); hostById.Clear(); remoteHeld.Clear(); storedIds.Clear(); nextId = 1;
            lastPosH.Clear(); lastPosT.Clear();
            lastLine.Clear(); lastFull.Clear(); sentCount = 0; recvCount = 0;
            puppets.Clear(); puppetPtrs.Clear(); detached.Clear(); lastVacCount = -1;
            ghosts.Clear(); procs = new CanProcessor[0]; lastProcScan = 0f; machineDepth = 0; rewardDepth = 0; lastIds = 0f;
            CanSync.Reset();
        }

        static void Diag(float now)
        {
            if (now - lastDiag < 10f) return;
            lastDiag = now;
            MpLog.Write(Status());
            if (M.IsHost) MelonLogger.Msg("Fábrica (líder): bichos=" + hostById.Count + ", enviados=" + sentCount);
            else MelonLogger.Msg("Fábrica (invitado): marionetas=" + puppets.Count + ", mensajes ESYNC recibidos=" + recvCount);
        }

        // =====================================================================
        public static void Tick(float now)
        {
            if (M == null) return;
            try { if (M.IsHost) HostTick(now); else GuestTick(now); CanSync.Tick(now); Diag(now); }
            catch (Exception e) { LastError = "Tick: " + e.Message; MpLog.Write("ERROR " + e); MelonLogger.Error("FactorySync.Tick: " + e); }
        }

        // ---------------------------------------------------------------- LÍDER
        static void HostTick(float now)
        {
            if (now - lastScan > 0.5f) { lastScan = now; HostScan(); }

            // Cada 3 s el líder dice qué bichos existen de verdad: el invitado borra las marionetas "huérfanas" (arregla bichos fantasma)
            if (now - lastIds > 3f)
            {
                lastIds = now;
                var live = new List<string>();
                foreach (var kv2 in hostById) if (kv2.Value != null && kv2.Value.gameObject.activeInHierarchy) live.Add(kv2.Key.ToString());
                M.Send("EIDS", string.Join(",", live.ToArray()));
            }

            FixFloating(now);

            // cuerpos sujetados por un invitado: si dejan de llegar datos, soltar
            foreach (var id in remoteHeld.Where(k => now - k.Value > 1f).Select(k => k.Key).ToList()) { HostRelease(id, Vector3.zero); }

            float iv = M.UsesSteam ? 0.12f : 0.05f;     // ~8 envíos/s por Steam, 20 por IP
            if (now - lastSend < iv) return;
            lastSend = now;

            var sb = new StringBuilder(); int count = 0;
            foreach (var kv in hostById.ToList())
            {
                var e = kv.Value;
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                var t = e.transform; var p = t.position; var q = t.rotation;
                string name = CleanName(e.gameObject.name);
                int st = 0; bool shiny = false, held = false; float gd = 0f, cms = 0f, blend = 0f;
                try { st = (int)e.CurrentState; } catch { }
                try { shiny = e.IsShiny; } catch { }
                try { held = e.isHeld; } catch { }
                try { gd = e.CurrentGlobalDamage; } catch { }
                try { cms = e.currentMoveSpeed; blend = e.currentAnimationBlend; } catch { }

                // velocidad medida (para que el invitado extrapole y no vaya a tirones)
                Vector3 vel = Vector3.zero;
                if (lastPosH.TryGetValue(kv.Key, out var lp) && lastPosT.TryGetValue(kv.Key, out var lt) && now > lt + 0.001f)
                    vel = (p - lp) / (now - lt);
                if (vel.sqrMagnitude > 1600f) vel = Vector3.zero;   // saltos/teletransportes
                lastPosH[kv.Key] = p; lastPosT[kv.Key] = now;

                Quaternion vq = q;
                try { var vt = e.visual; if (vt != null) vq = vt.rotation; } catch { }

                var one = new StringBuilder();
                one.Append(kv.Key).Append(',').Append(name).Append(',')
                   .Append(Fp(p.x)).Append(',').Append(Fp(p.y)).Append(',').Append(Fp(p.z)).Append(',')
                   .Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(',')
                   .Append(F(e.health)).Append(',').Append(e.IsDead ? "1" : "0").Append(',')
                   .Append(st).Append(',').Append(shiny ? "1" : "0").Append(',').Append(F(gd)).Append(',')
                   .Append(Fp(vel.x)).Append(',').Append(Fp(vel.y)).Append(',').Append(Fp(vel.z)).Append(',')
                   .Append(F(cms)).Append(',').Append(held ? "1" : "0").Append(',').Append(F(blend)).Append(',')
                   .Append(F(vq.x)).Append(',').Append(F(vq.y)).Append(',').Append(F(vq.z)).Append(',').Append(F(vq.w));
                string ent = one.ToString();

                // solo se envía si cambió algo; cada 2 s se reenvía todo (por si el invitado entra tarde)
                if (lastLine.TryGetValue(kv.Key, out var prev) && prev == ent &&
                    lastFull.TryGetValue(kv.Key, out var lf) && now - lf < 2f) continue;
                lastLine[kv.Key] = ent; lastFull[kv.Key] = now; sentCount++;

                if (sb.Length > 0) sb.Append(';');
                sb.Append(ent);
                if (++count >= 12) { M.Send("ESYNC", sb.ToString()); sb.Clear(); count = 0; } // trozos < 4 KB (límite del chat de Steam)
            }
            if (sb.Length > 0) M.Send("ESYNC", sb.ToString());
        }

        // ---- anti-flotación: cadáveres que se quedan "volando" (cinemáticos o sin gravedad) mucho rato se sueltan ----
        static readonly Dictionary<int, float> floatSince = new Dictionary<int, float>();
        static float lastFloatCheck;
        static int floatLogs;

        static void FixFloating(float now)
        {
            if (now - lastFloatCheck < 0.5f) return;
            lastFloatCheck = now;
            foreach (var kv in hostById.ToList())
            {
                int id = kv.Key; var e = kv.Value;
                if (e == null || !e.gameObject.activeInHierarchy) { floatSince.Remove(id); continue; }
                bool dead = false, held = false;
                try { dead = e.IsDead; } catch { }
                try { held = e.isHeld; } catch { }
                if (!dead || held || remoteHeld.ContainsKey(id) || storedIds.Contains(id)) { floatSince.Remove(id); continue; }
                var rb = e.GetComponent<Rigidbody>();
                if (rb == null) continue;
                if (rb.isKinematic || !rb.useGravity)
                {
                    float t0;
                    if (!floatSince.TryGetValue(id, out t0)) floatSince[id] = now;
                    else if (now - t0 > 5f)
                    {
                        rb.isKinematic = false; rb.useGravity = true; floatSince.Remove(id);
                        if (floatLogs++ < 30) MpLog.Write("Anti-flotación: bicho " + id + " liberado");
                    }
                }
                else floatSince.Remove(id);
            }
        }

        static void HostScan()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Enemy>();
            var alive = new HashSet<IntPtr>();
            foreach (var e in all)
            {
                alive.Add(e.Pointer);
                if (!hostIds.ContainsKey(e.Pointer)) { int id = nextId++; hostIds[e.Pointer] = id; hostById[id] = e; }
            }
            foreach (var kv in hostIds.Where(k => !alive.Contains(k.Key)).ToList())
            {
                if (storedIds.Contains(kv.Value)) continue;   // guardado en la aspiradora de un invitado: sigue existiendo
                hostIds.Remove(kv.Key); hostById.Remove(kv.Value); remoteHeld.Remove(kv.Value);
                lastLine.Remove(kv.Value); lastFull.Remove(kv.Value); lastPosH.Remove(kv.Value); lastPosT.Remove(kv.Value);
                M.Send("EDEL", kv.Value.ToString());
            }
        }

        static void HostRelease(int id, Vector3 vel)
        {
            remoteHeld.Remove(id);
            if (hostById.TryGetValue(id, out var e) && e != null)
            {
                var rb = e.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = vel; }
            }
        }

        // un invitado ha guardado el bicho en SU aspiradora: desaparece para todos (si no, saldría duplicado)
        static void HostStore(int id)
        {
            if (!hostById.TryGetValue(id, out var e) || e == null) return;
            storedIds.Add(id); remoteHeld.Remove(id);
            try { e.gameObject.SetActive(false); } catch { }
            M.Send("ESTORE", id.ToString());
            if (eventLogs++ < 30) MpLog.Write("Bicho " + id + " guardado en la aspiradora de un invitado");
        }

        // el invitado lo lanza: el bicho real reaparece en el líder con su física, y se ve en todas las pantallas
        static void HostThrow(int id, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (!hostById.TryGetValue(id, out var e) || e == null) return;
            storedIds.Remove(id); remoteHeld.Remove(id);
            try
            {
                e.gameObject.SetActive(true);
                e.transform.position = pos; e.transform.rotation = rot;
                var rb = e.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = vel; }
                e.OnThrown(vel);
                lastLine.Remove(id); lastFull.Remove(id);
                if (eventLogs++ < 30) MpLog.Write("Bicho " + id + " lanzado por un invitado, vel=" + vel);
            }
            catch (Exception ex) { LastError = "ETHROW: " + ex.Message; MpLog.Write("ERROR ETHROW " + ex); }
        }

        // ---------------------------------------------------------------- INVITADO
        static void ApplyAnim(Puppet p)
        {
            try
            {
                p.E.currentMoveSpeed = p.CMS;
                p.E.currentAnimationBlend = p.Blend;
                p.E.currentMoveDirection = p.Vel.sqrMagnitude > 0.01f ? p.Vel.normalized : Vector3.zero;
                var mpb = p.E.MaterialPropertyBlock;
                var mr = p.E.meshRenderer;
                if (mpb != null && mr != null) { mpb.SetFloat(Enemy.AnimationBlend, p.Blend); mr.SetPropertyBlock(mpb); }
            }
            catch (Exception ex) { if (!animErr) { animErr = true; MpLog.Write("Anim: " + ex.Message); } }
        }

        static void GuestTick(float now)
        {
            float dt = Time.deltaTime;
            float k = 1f - Mathf.Exp(-16f * dt);    // suavizado independiente de los FPS
            fxDepth = 0;                            // seguridad: por si una excepción dejó el contador sin bajar
            machineDepth = 0; rewardDepth = 0;
            foreach (var gk in ghosts.Where(g => now - g.Value > 25f).Select(g => g.Key).ToList()) ghosts.Remove(gk);

            // ¿mi aspiradora está aspirando? (los bichos al alcance los controla mi ventana)
            bool sucking = false; Vector3 nozzle = Vector3.zero; float range = 0f, near = 0.6f; Vacuum vac = null;
            try
            {
                vac = Vacuum.Instance;
                if (vac != null)
                {
                    int n = vac.grabbables.Count;
                    if (n != lastVacCount) { lastVacCount = n; MpLog.Write("Aspiradora: guardados=" + n); }
                    if (vac.gameObject.activeInHierarchy && (int)vac.state == 1 && vac.point != null)
                    {
                        sucking = true; nozzle = vac.point.position; range = vac.suckRange * 1.15f;
                        near = Mathf.Max(0.6f, vac.suckDisableDistance * 1.5f);
                    }
                }
            }
            catch { }
            bool vacLog = sucking && now - lastVacDiag > 0.5f;
            if (vacLog) lastVacDiag = now;

            foreach (var kv in puppets.ToList())
            {
                int id = kv.Key; var p = kv.Value;
                if (p.E == null) { puppets.Remove(id); continue; }
                if (now - p.Seen > 4f) { RemovePuppet(id); continue; }   // el líder dejó de enviarlo

                var go = p.E.gameObject;

                // Mi aspiradora lo ha guardado (el objeto se desactiva): lo "separo" y aviso al líder
                if (!go.activeInHierarchy)
                {
                    // ¿lo acaba de procesar mi trituradora local? entonces NO es de la aspiradora: lo suelto sin avisar al líder
                    if (NearProcessor(go.transform.position, 4f))
                    {
                        puppets.Remove(id); puppetPtrs.Remove(p.E.Pointer);
                        ghosts[id] = now;
                        if (eventLogs++ < 30) MpLog.Write("Bicho " + id + " procesado por mi trituradora local");
                        continue;
                    }
                    puppets.Remove(id); puppetPtrs.Remove(p.E.Pointer);
                    // Solo es de mi aspiradora si yo lo estaba aspirando/sujetando hace un momento. Si no (p. ej. murió por la onda
                    // expansiva y el juego lo desactivó), NO se avisa al líder: la marioneta se recrea con el siguiente ESYNC.
                    if (p.OwnedUntil > 0f && now - p.OwnedUntil < 1.5f)
                    {
                        detached[p.E.Pointer] = new KeyValuePair<int, Puppet>(id, p);
                        M.Send("ESTORE", id.ToString());
                        if (eventLogs++ < 30) MpLog.Write("Guardé el bicho " + id + " en mi aspiradora");
                    }
                    else if (eventLogs++ < 30) MpLog.Write("Marioneta " + id + " desactivada sin aspiradora (efecto/muerte): se recrea");
                    continue;
                }

                var rb = p.RB;
                var tr = go.transform;
                bool held = false;
                try { held = p.E.isHeld; } catch { }
                bool isDead = false;
                try { isDead = p.E.IsDead; } catch { }
                bool deadNow = p.Dead || isDead;

                float d2 = (tr.position - nozzle).sqrMagnitude;
                bool inSuck = sucking && d2 < range * range;
                if (inSuck || held) p.OwnedUntil = now + 0.4f;
                bool owned = held || now < p.OwnedUntil;

                if (vacLog && inSuck)
                {
                    try
                    {
                        MpLog.Write("Aspirando: id=" + id + " dist=" + Mathf.Sqrt(d2).ToString("F2", CI) + " CanGrab=" + p.E.CanGrab +
                                    " held=" + held + " muerto=" + isDead + " kinematic=" + (rb != null && rb.isKinematic) +
                                    " enRango=" + vac.grabbablesInRange.Count + " guardados=" + vac.grabbables.Count);
                    }
                    catch { }
                }

                // Si se queda pegado a la boquilla sin entrar, lo guardo yo (la aspiradora no lo terminaba de meter)
                if (inSuck && !held && d2 < near * near)
                {
                    if (p.NearSince <= 0f) p.NearSince = now;
                    else if (now - p.NearSince > 0.35f && AssistStore(p, vac))
                    {
                        if (eventLogs++ < 30) MpLog.Write("Guardado asistido: " + id);
                        continue;   // en el siguiente frame se detecta como guardado
                    }
                }
                else p.NearSince = 0f;

                // muerte decidida por el líder: ragdoll con la velocidad con la que murió
                if (p.Dead && !isDead)
                {
                    try { p.E.EnableRagdoll(); }
                    catch (Exception ex) { if (ragErr++ < 5) MpLog.Write("EnableRagdoll falló: " + ex.Message); }
                    p.DeathKick = true;
                }
                if (p.DeathKick)
                {
                    p.DeathKick = false;
                    if (rb != null && !owned)
                    {
                        tr.position = p.Pos; tr.rotation = p.Rot;
                        rb.isKinematic = false; rb.velocity = p.Vel;
                    }
                }

                if (owned)
                {
                    // lo llevo / lo estoy aspirando yo: mi ventana lo controla y el líder copia su posición
                    if (inSuck && !held && rb != null && rb.isKinematic) rb.isKinematic = false;
                    if (now >= p.NextBody)
                    {
                        p.NextBody = now + 0.05f;
                        var q = tr.rotation; var ps = tr.position;
                        M.Send("EBODY", id.ToString(), F(ps.x), F(ps.y), F(ps.z), F(q.x), F(q.y), F(q.z), F(q.w));
                    }
                }
                else
                {
                    if (p.WasOwned && now - p.LastGrabEvent > 0.3f)
                    {
                        Vector3 vv = rb != null ? rb.velocity : Vector3.zero;
                        M.Send("EREL", id.ToString(), F(vv.x), F(vv.y), F(vv.z));
                    }

                    // posición esperada = última recibida + velocidad (extrapolación corta)
                    Vector3 target = p.Pos + p.Vel * Mathf.Min(now - p.RecvT, 0.25f);

                    if (deadNow && !p.HostHeld)
                    {
                        // cadáver: físicas locales (ragdoll natural). Solo se corrige si se aleja mucho del líder.
                        if (rb != null && rb.isKinematic) rb.isKinematic = false;
                        if (rb != null && !rb.useGravity) rb.useGravity = true;   // anti-flotación
                        if ((tr.position - target).sqrMagnitude > 1.44f)
                        {
                            tr.position = target; tr.rotation = p.Rot;
                            if (rb != null) rb.velocity = p.Vel;
                        }
                    }
                    else
                    {
                        if (rb != null && !rb.isKinematic) rb.isKinematic = true;
                        if ((tr.position - target).sqrMagnitude > 9f) tr.position = target;
                        else tr.position = Vector3.Lerp(tr.position, target, k);
                        tr.rotation = Quaternion.Slerp(tr.rotation, p.Rot, k);
                        if (p.HasV)
                        {
                            // hacia dónde mira el cuerpo (si no, andaban de lado o hacia atrás)
                            try
                            {
                                var vt = p.E.visual;
                                if (vt != null) vt.rotation = Quaternion.Slerp(vt.rotation, p.VRot, k);
                                p.E.currentRot = p.VRot; p.E.targetRot = p.VRot;
                            }
                            catch { }
                        }
                        if (!deadNow) ApplyAnim(p);
                    }
                }
                p.WasOwned = owned;
            }
        }

        // Aspiradora: mete el bicho en el inventario si el juego no lo hizo solo
        static bool AssistStore(Puppet p, Vacuum v)
        {
            try
            {
                if (v == null) return false;
                int cap = v.maxCapacity * Math.Max(1, v.maxCapacityMultiplier);
                if (cap > 0 && v.grabbables.Count >= cap) return false;
                var ig = p.E.TryCast<IGrabbable>();
                if (ig == null) return false;
                try { p.E.OnGrabbed(); } catch { }
                p.E.gameObject.SetActive(false);
                v.grabbables.Add(ig);
                return true;
            }
            catch (Exception ex) { if (ragErr++ < 5) MpLog.Write("AssistStore falló: " + ex.Message); return false; }
        }

        // Llamado cuando MI aspiradora lanza un bicho que tenía guardado
        public static void OnLocalThrown(Enemy e, Vector3 vel)
        {
            if (!GuestOn() || e == null) return;
            if (!detached.TryGetValue(e.Pointer, out var kvp)) return;
            detached.Remove(e.Pointer);
            int id = kvp.Key; var p = kvp.Value;
            var t = e.transform; var ps = t.position; var q = t.rotation;
            float now = Time.realtimeSinceStartup;
            p.RB = e.GetComponent<Rigidbody>();
            p.Pos = ps; p.Rot = q; p.Vel = vel; p.RecvT = now;
            p.Seen = now; p.OwnedUntil = now + 1.5f; p.LastGrabEvent = now; p.WasOwned = true;
            puppets[id] = p; puppetPtrs.Add(e.Pointer);
            M.Send("ETHROW", id.ToString(), F(ps.x), F(ps.y), F(ps.z), F(q.x), F(q.y), F(q.z), F(q.w), F(vel.x), F(vel.y), F(vel.z));
            if (eventLogs++ < 30) MpLog.Write("Lancé el bicho " + id + " vel=" + vel);
        }

        static Enemy FindPrefab(string name)
        {
            var truck = Truck.Instance; if (truck == null) return null;
            Enemy best = null;
            foreach (var pf in truck.allEnemyPrefabs)
            {
                if (pf == null) continue;
                if (pf.name == name) return pf;
                if (best == null && name.StartsWith(pf.name)) best = pf;
            }
            return best;
        }

        static System.Reflection.PropertyInfo stateProp;
        static bool stateLookedUp;

        // el enum de estado (esperando en camión / saliendo / paseando) se pone por reflexión para no depender de su nombre
        static void SetState(Enemy e, int st)
        {
            try
            {
                if (!stateLookedUp) { stateLookedUp = true; stateProp = typeof(Enemy).GetProperty("state"); }
                if (stateProp == null || !stateProp.CanWrite) return;
                if ((int)e.CurrentState == st) return;
                stateProp.SetValue(e, Enum.ToObject(stateProp.PropertyType, st));
            }
            catch { }
        }

        static void Upsert(int id, string name, Vector3 pos, Quaternion rot, float hp, bool dead, int st, bool shiny, float gd,
                           Vector3 vel, float cms, bool hhel, float blend, Quaternion vrot, bool hasV)
        {
            if (detached.Values.Any(d => d.Key == id)) return;   // está en mi aspiradora: ignoro lo que diga el líder
            if (ghosts.ContainsKey(id)) return;                  // ya lo trituró mi máquina local: espero a que el líder lo retire
            if (!puppets.TryGetValue(id, out var p))
            {
                if (Truck.Instance == null) { LastError = "Truck.Instance nulo (¿estás en la fábrica?)"; MpLog.Write(LastError); return; }
                var prefab = FindPrefab(name);
                if (prefab == null) { if (missingPrefabs.Add(name)) { MelonLogger.Warning("Sin prefab de bicho para '" + name + "'"); MpLog.Write("Sin prefab de bicho para '" + name + "'"); } return; }
                var e = Truck.Instance.GetEnemy(prefab, pos, rot);
                if (e == null) { LastError = "GetEnemy devolvió null (" + name + ")"; MpLog.Write(LastError); return; }
                try { if (!e.gameObject.activeSelf) e.gameObject.SetActive(true); } catch { }
                p = new Puppet { E = e, RB = e.GetComponent<Rigidbody>(), Pos = pos, Rot = rot };
                puppets[id] = p; puppetPtrs.Add(e.Pointer);
                if (createdLogs++ < 15) MpLog.Write("Marioneta creada: id=" + id + " nombre=" + name + " pos=" + pos);
                if (p.RB != null) p.RB.isKinematic = true;
            }
            float now = Time.realtimeSinceStartup;
            p.Pos = pos; p.Rot = rot; p.Vel = vel; p.RecvT = now; p.CMS = cms; p.Blend = blend; p.HostHeld = hhel; p.VRot = vrot; p.HasV = hasV;
            p.Dead = dead; p.Seen = now;
            try { p.E.health = hp; } catch { }
            if (st >= 0) SetState(p.E, st);
            try { if (p.Shiny != shiny) { p.Shiny = shiny; p.E.SetShiny(shiny); } } catch { }
            try { if (Math.Abs(p.GDmg - gd) > 0.01f) { p.GDmg = gd; p.E.SetGlobalDamageVisual(gd); } } catch { }
        }

        static void RemovePuppet(int id)
        {
            if (!puppets.TryGetValue(id, out var p)) return;
            puppets.Remove(id);
            if (p.E != null)
            {
                puppetPtrs.Remove(p.E.Pointer);
                try { if (p.E.gameObject.activeSelf) Truck.Instance.ReturnEnemy(p.E); } catch { }
            }
        }

        // =====================================================================
        //  MENSAJES
        // =====================================================================
        public static void Handle(string type, string[] a)
        {
            try
            {
                if (type.Length > 1 && type[0] == 'C') { CanSync.Handle(type, a); return; }   // CSYNC, CDEL, CBODY, CREL, CSTORE, CTHROW
                switch (type)
                {
                    case "EIDS": // líder -> invitados: lista de bichos que existen de verdad
                        if (!M.IsHost && a.Length >= 1)
                        {
                            var alive = new HashSet<int>();
                            foreach (var s in a[0].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) alive.Add(int.Parse(s));
                            float tnow = Time.realtimeSinceStartup;
                            foreach (var pk in puppets.Where(q => !alive.Contains(q.Key) && tnow - q.Value.Seen > 0.5f && tnow >= q.Value.OwnedUntil).Select(q => q.Key).ToList())
                            { if (eventLogs++ < 30) MpLog.Write("Marioneta huérfana " + pk + " eliminada"); RemovePuppet(pk); }
                            foreach (var gk2 in ghosts.Keys.Where(g => !alive.Contains(g)).ToList()) ghosts.Remove(gk2);
                        }
                        break;
                    case "ESYNC": // líder -> invitados
                        if (M.IsHost || a.Length < 1) return;
                        recvCount++;
                        foreach (var ent in a[0].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var f = ent.Split(',');
                            if (f.Length < 11) continue;
                            int st = f.Length > 11 ? int.Parse(f[11]) : -1;
                            bool shiny = f.Length > 12 && f[12] == "1";
                            float gd = f.Length > 13 ? P(f[13]) : 0f;
                            Vector3 vel = f.Length > 16 ? new Vector3(P(f[14]), P(f[15]), P(f[16])) : Vector3.zero;
                            float cms = f.Length > 17 ? P(f[17]) : 0f;
                            bool hhel = f.Length > 18 && f[18] == "1";
                            float blend = f.Length > 19 ? P(f[19]) : 0f;
                            bool hasV = f.Length > 23;
                            Quaternion vrot = hasV ? new Quaternion(P(f[20]), P(f[21]), P(f[22]), P(f[23])) : Quaternion.identity;
                            Upsert(int.Parse(f[0]), f[1],
                                new Vector3(P(f[2]), P(f[3]), P(f[4])),
                                new Quaternion(P(f[5]), P(f[6]), P(f[7]), P(f[8])),
                                P(f[9]), f[10] == "1", st, shiny, gd, vel, cms, hhel, blend, vrot, hasV);
                        }
                        break;
                    case "EDEL": if (!M.IsHost && a.Length > 0) { int did = int.Parse(a[0]); ghosts.Remove(did); RemovePuppet(did); } break;

                    case "ESTORE": // alguien guardó el bicho en su aspiradora
                        if (a.Length < 1) break;
                        if (M.IsHost) HostStore(int.Parse(a[0]));
                        else { int sid = int.Parse(a[0]); if (puppets.ContainsKey(sid)) RemovePuppet(sid); }
                        break;
                    case "ETHROW": // un invitado lanza un bicho que tenía guardado
                        if (M.IsHost && a.Length >= 11)
                            HostThrow(int.Parse(a[0]),
                                new Vector3(P(a[1]), P(a[2]), P(a[3])),
                                new Quaternion(P(a[4]), P(a[5]), P(a[6]), P(a[7])),
                                new Vector3(P(a[8]), P(a[9]), P(a[10])));
                        break;

                    case "EFWD": // invitado -> líder: reaplicar daño/efecto sobre el bicho real
                        if (M.IsHost) HostApplyForward(a);
                        break;
                    case "EBODY": // invitado sujeta o aspira un bicho
                        if (M.IsHost && a.Length >= 8 && hostById.TryGetValue(int.Parse(a[0]), out var he) && he != null)
                        {
                            int id = int.Parse(a[0]);
                            remoteHeld[id] = Time.realtimeSinceStartup;
                            var rb = he.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
                            he.transform.position = new Vector3(P(a[1]), P(a[2]), P(a[3]));
                            he.transform.rotation = new Quaternion(P(a[4]), P(a[5]), P(a[6]), P(a[7]));
                        }
                        break;
                    case "EREL": // invitado suelta el bicho
                        if (M.IsHost && a.Length >= 4) HostRelease(int.Parse(a[0]), new Vector3(P(a[1]), P(a[2]), P(a[3])));
                        break;
                }
            }
            catch (Exception e) { LastError = type + ": " + e.Message; MpLog.Write("ERROR Handle(" + type + ") " + e); MelonLogger.Error("FactorySync.Handle(" + type + "): " + e); }
        }

        // ---- reenvío de llamadas (Damage, AddBurn...) de invitado a líder ----
        public static bool ForwardFromPuppet(Enemy inst, MethodBase m, object[] args)
        {
            if (!IsPuppet(inst)) return true;                // enemigo normal: ejecutar
            if (machineDepth > 0) return true;               // daño de MI trituradora local (solo visual): el líder ya hace el suyo
            if (fxDepth > 0) { fxDepth++; return true; }     // llamada interna del propio efecto local: no se reenvía otra vez
            int id = puppets.FirstOrDefault(k => k.Value.E != null && k.Value.E.Pointer == inst.Pointer).Key;
            if (id == 0) return false;
            var parts = new List<string> { id.ToString(), m.Name };
            foreach (var o in args) parts.Add(Ser(o));
            M.Send("EFWD", parts.ToArray());                 // el líder aplica el daño de verdad
            if (M != null && M.LocalHitFx) { fxDepth = 1; return true; }   // y yo veo ya la reacción (sangre, ragdoll...)
            return false;
        }

        public static void ForwardPost() { if (fxDepth > 0) fxDepth--; }

        static void HostApplyForward(string[] a)
        {
            if (a.Length < 2 || !hostById.TryGetValue(int.Parse(a[0]), out var e) || e == null) return;
            var m = typeof(Enemy).GetMethod(a[1]);
            if (m == null) return;
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = De(ps[i].ParameterType, i + 2 < a.Length ? a[i + 2] : "");
            m.Invoke(e, args);
        }

        static string Ser(object o)
        {
            if (o == null) return "n=";
            if (o is float f) return "f=" + F(f);
            if (o is bool b) return "b=" + (b ? "1" : "0");
            if (o is int i) return "i=" + i;
            if (o is Vector3 v) return "v=" + F(v.x) + "," + F(v.y) + "," + F(v.z);
            if (o is UnityEngine.Object u) return "w=" + u.name;
            return "n=";
        }

        static object De(Type t, string s)
        {
            int k = s.IndexOf('='); string val = k >= 0 ? s.Substring(k + 1) : "";
            if (t == typeof(float)) return val == "" ? 0f : P(val);
            if (t == typeof(bool)) return val == "1";
            if (t == typeof(int)) return val == "" ? 0 : int.Parse(val);
            if (t == typeof(Vector3)) { var c = val.Split(','); return c.Length == 3 ? new Vector3(P(c[0]), P(c[1]), P(c[2])) : Vector3.zero; }
            if (t == typeof(Weapon))
            {
                if (val == "") return null;
                foreach (var w in Resources.FindObjectsOfTypeAll<Weapon>()) if (w.name == val && w.gameObject.scene.IsValid()) return w;
                return null;
            }
            return null;
        }

        static float P(string s) { return float.Parse(s, CI); }
        static string F(float f) { return f.ToString("0.###", CI); }
        static string Fp(float f) { return f.ToString("0.##", CI); }   // posiciones y velocidades: 1 cm basta
        static string CleanName(string n) { return n.Replace("(Clone)", "").Replace("|", "").Replace(",", "").Replace(";", "").Trim(); }
    }

    // =========================================================================
    //  PARCHES DE LA FÁBRICA COMPARTIDA
    // =========================================================================
    public static class FactoryPatches
    {
        static bool GuestSharing() { return Mod.I != null && Mod.I.Connected && !Mod.I.IsHost && Mod.I.ShareFactory && Mod.I.Synced; }

        public static void Apply(HarmonyLib.Harmony h)
        {
            // La IA/movimiento de una marioneta lo decide el líder
            foreach (var n in new[] { "FollowPath", "Move", "RotateTowardsCurrentPath", "TryPickNewPath", "UpdatePanic", "StartPanic", "ExitTruck" })
                Patch(h, typeof(Enemy), n, nameof(BlockPuppet));

            // El daño/efectos sobre una marioneta se reenvían al líder
            foreach (var n in new[] { "Damage", "ShockwaveDamage", "AddBurn", "StartBleeding", "AddGlobalDamage" })
            {
                Patch(h, typeof(Enemy), n, nameof(ForwardPre));
                PatchPost(h, typeof(Enemy), n, nameof(ForwardPost));
            }

            // Cuando MI aspiradora lanza un bicho guardado, se avisa al líder (ETHROW)
            Patch(h, typeof(Enemy), "OnThrown", nameof(ThrownPre));

            // El invitado no genera bichos ni tritura (lo hace el líder)
            Patch(h, typeof(Truck), "SpawnEnemiesInsideTruck", nameof(BlockOnGuest));

            // v0.5: la trituradora, la cinta y la máquina de latas CORREN en el invitado (se ven girar, procesar y soltar latas).
            // Se evita que dupliquen daño o dinero:
            Patch(h, typeof(CanProcessor), "Damage", nameof(MachineDamagePre));
            PatchPost(h, typeof(CanProcessor), "Damage", nameof(MachineDamagePost));
            Patch(h, typeof(RewardMachine), "RewardCan", nameof(RewardPre));
            PatchPost(h, typeof(RewardMachine), "RewardCan", nameof(RewardPost));
            Patch(h, typeof(MoneyManager), "AddMoney", nameof(AddMoneyPre));
            Patch(h, typeof(MoneyManager), "ChangeMoney", nameof(ChangeMoneyPre));

            // v0.6: las latas las crea SOLO el líder; la máquina local del invitado no crea latas propias
            Patch(h, typeof(CanProcessor), "SpawnCan", nameof(BlockOnGuest));
            // Cuando MI aspiradora/mano lanza una lata, se avisa al líder
            Patch(h, typeof(Can), "OnThrown", nameof(CanThrownPre));
        }

        static void Patch(HarmonyLib.Harmony h, Type t, string method, string prefix)
        {
            try
            {
                var m = AccessTools.Method(t, method);
                if (m == null) { MelonLogger.Warning("No existe " + t.Name + "." + method); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(FactoryPatches), prefix));
            }
            catch (Exception e) { MelonLogger.Warning("No se pudo parchear " + t.Name + "." + method + ": " + e.Message); }
        }

        static void PatchPost(HarmonyLib.Harmony h, Type t, string method, string postfix)
        {
            try
            {
                var m = AccessTools.Method(t, method);
                if (m == null) return;
                h.Patch(m, postfix: new HarmonyMethod(typeof(FactoryPatches), postfix));
            }
            catch (Exception e) { MelonLogger.Warning("No se pudo parchear (post) " + t.Name + "." + method + ": " + e.Message); }
        }

        public static void ForwardPost() { FactorySync.ForwardPost(); }

        public static bool BlockPuppet(Enemy __instance) { return !FactorySync.IsPuppet(__instance); }
        public static bool BlockOnGuest() { return !GuestSharing(); }

        public static bool MachineDamagePre() { if (GuestSharing()) FactorySync.machineDepth++; return true; }
        public static void MachineDamagePost() { if (FactorySync.machineDepth > 0) FactorySync.machineDepth--; }
        static long rewardBefore = -1;
        public static bool RewardPre()
        {
            if (GuestSharing())
            {
                FactorySync.rewardDepth++;
                try { rewardBefore = (long)MoneyManager.Instance.Money; } catch { rewardBefore = -1; }
            }
            return true;
        }
        public static void RewardPost()
        {
            if (FactorySync.rewardDepth > 0) FactorySync.rewardDepth--;
            if (GuestSharing() && rewardBefore >= 0 && FactorySync.rewardDepth == 0)
            {
                Mod.I.RestoreMoneyTo(rewardBefore);    // si la venta local sumó dinero por cualquier vía, se deshace
                rewardBefore = -1;
            }
        }

        // En el invitado el dinero de las ventas lo cobra el LÍDER (le llega por MONEY/MSYNC): aquí solo se ve el "+$" flotante.
        public static bool AddMoneyPre(MoneyManager __instance, object[] __args)
        {
            if (!GuestSharing() || Mod.I.ApplyingMoney) return true;
            try
            {
                // CoinTextType es un tipo anidado del juego: se llama por reflexión para no tener que nombrarlo
                var m = typeof(MoneyManager).GetMethod("SpawnFloatingText");
                if (m != null) m.Invoke(__instance, new object[] { __args[0], __args[1] });
            }
            catch { }
            return false;
        }

        public static bool ChangeMoneyPre(object[] __args)
        {
            if (!GuestSharing() || Mod.I.ApplyingMoney || FactorySync.rewardDepth <= 0) return true;
            try { if ((long)__args[0] > 0) return false; } catch { }
            return true;
        }
        public static bool CanThrownPre(Can __instance, object[] __args)
        {
            try { if (__args != null && __args.Length > 0 && __args[0] is Vector3) CanSync.OnLocalThrown(__instance, (Vector3)__args[0]); }
            catch { }
            return true;
        }
        public static bool ThrownPre(Enemy __instance, object[] __args)
        {
            try { if (__args != null && __args.Length > 0 && __args[0] is Vector3) FactorySync.OnLocalThrown(__instance, (Vector3)__args[0]); }
            catch { }
            return true;   // el lanzamiento local se ejecuta siempre
        }
        public static bool ForwardPre(Enemy __instance, MethodBase __originalMethod, object[] __args)
        {
            return FactorySync.ForwardFromPuppet(__instance, __originalMethod, __args);
        }
    }
}
