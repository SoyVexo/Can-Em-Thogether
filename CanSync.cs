using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MelonLoader;
using UnityEngine;
using Il2Cpp;

namespace CanEmThogether
{
    /// <summary>
    /// LATAS COMPARTIDAS (v0.6, SIN PROBAR).
    /// El LÍDER manda las latas reales (las que salen de SU máquina). El invitado ve copias ("marionetas") y su máquina
    /// local NO crea latas. Si el invitado coge una lata o la aspira, avisa al líder: la lata real desaparece para todos
    /// y reaparece donde el invitado la suelta/lanza.
    /// </summary>
    public static class CanSync
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        static Mod M { get { return Mod.I; } }
        static float P(string s) { return float.Parse(s, CI); }
        static string F(float f) { return f.ToString("0.###", CI); }
        static string Fp(float f) { return f.ToString("0.##", CI); }

        // ---------- LÍDER ----------
        static readonly Dictionary<IntPtr, int> hostIds = new Dictionary<IntPtr, int>();
        static readonly Dictionary<int, Can> hostById = new Dictionary<int, Can>();
        static readonly HashSet<int> storedIds = new HashSet<int>();
        static readonly Dictionary<int, float> remoteHeld = new Dictionary<int, float>();
        static readonly Dictionary<int, string> lastLine = new Dictionary<int, string>();
        static readonly Dictionary<int, float> lastFull = new Dictionary<int, float>();
        static readonly Dictionary<int, Vector3> lastPos = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, float> lastPosT = new Dictionary<int, float>();
        static int nextId = 1;
        static float lastScan, lastSend;

        // ---------- INVITADO ----------
        class CP
        {
            public Can C; public Rigidbody RB; public Vector3 Pos, Vel; public Quaternion Rot;
            public float Seen, OwnedUntil, NextBody; public bool WasOwned;
        }
        static readonly Dictionary<int, CP> puppets = new Dictionary<int, CP>();
        static readonly HashSet<IntPtr> puppetPtrs = new HashSet<IntPtr>();
        static readonly Dictionary<IntPtr, KeyValuePair<int, CP>> detached = new Dictionary<IntPtr, KeyValuePair<int, CP>>();
        static readonly Dictionary<int, float> ghosts = new Dictionary<int, float>();
        static readonly Dictionary<IntPtr, KeyValuePair<float, Vector3>> thrown = new Dictionary<IntPtr, KeyValuePair<float, Vector3>>();
        static RewardMachine[] rewards = new RewardMachine[0];
        static float lastRewardScan, lastSweep;
        static int logs, recv;
        static bool createErr;

        public static string Status()
        {
            return M != null && M.IsHost ? "Latas: " + hostById.Count : "Latas: marionetas=" + puppets.Count + " recibidos=" + recv;
        }

        public static void Reset()
        {
            hostIds.Clear(); hostById.Clear(); storedIds.Clear(); remoteHeld.Clear();
            lastLine.Clear(); lastFull.Clear(); lastPos.Clear(); lastPosT.Clear(); nextId = 1;
            puppets.Clear(); puppetPtrs.Clear(); detached.Clear(); ghosts.Clear(); thrown.Clear();
            rewards = new RewardMachine[0]; lastRewardScan = 0f;
        }

        public static void Tick(float now)
        {
            if (M == null) return;
            if (M.IsHost) HostTick(now); else GuestTick(now);
        }

        // =====================================================================  LÍDER
        static void HostTick(float now)
        {
            if (now - lastScan > 0.5f) { lastScan = now; HostScan(); }
            foreach (var id in remoteHeld.Where(k => now - k.Value > 1f).Select(k => k.Key).ToList()) HostRelease(id, Vector3.zero, false);
            FixFloating(now);

            float iv = M.UsesSteam ? 0.12f : 0.05f;
            if (now - lastSend < iv) return;
            lastSend = now;

            var sb = new StringBuilder(); int count = 0;
            foreach (var kv in hostById.ToList())
            {
                var c = kv.Value;
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                var t = c.transform; var p = t.position; var q = t.rotation;

                Vector3 vel = Vector3.zero;
                if (lastPos.TryGetValue(kv.Key, out var lp) && lastPosT.TryGetValue(kv.Key, out var lt) && now > lt + 0.001f)
                    vel = (p - lp) / (now - lt);
                if (vel.sqrMagnitude > 900f) vel = Vector3.zero;
                lastPos[kv.Key] = p; lastPosT[kv.Key] = now;

                bool shiny = false, smoked = false, hio = false; int price = 0, qty = 0;
                try { shiny = c.IsShiny; } catch { }
                try { smoked = c.IsSmoked; } catch { }
                try { hio = c.HoleInOne; } catch { }
                try { price = (int)c.price; } catch { }
                try { qty = (int)c.quantity; } catch { }

                string ent = kv.Key + "," + Fp(p.x) + "," + Fp(p.y) + "," + Fp(p.z) + "," + F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w) + "," +
                             Fp(vel.x) + "," + Fp(vel.y) + "," + Fp(vel.z) + "," + price + "," + qty + "," + (shiny ? "1" : "0") + "," + (smoked ? "1" : "0") + "," + (hio ? "1" : "0");

                if (lastLine.TryGetValue(kv.Key, out var prev) && prev == ent && lastFull.TryGetValue(kv.Key, out var lf) && now - lf < 2f) continue;
                lastLine[kv.Key] = ent; lastFull[kv.Key] = now;

                if (sb.Length > 0) sb.Append(';');
                sb.Append(ent);
                if (++count >= 14) { M.Send("CSYNC", sb.ToString()); sb.Clear(); count = 0; }
            }
            if (sb.Length > 0) M.Send("CSYNC", sb.ToString());
        }

        // ---- anti-flotación: latas que se quedan "flotando" (cinemáticas o sin gravedad) más de 5 s se sueltan ----
        static readonly Dictionary<int, float> floatSince = new Dictionary<int, float>();
        static float lastFloatCheck;

        static void FixFloating(float now)
        {
            if (now - lastFloatCheck < 0.5f) return;
            lastFloatCheck = now;
            foreach (var kv in hostById.ToList())
            {
                int id = kv.Key; var c = kv.Value;
                if (c == null || !c.gameObject.activeInHierarchy) { floatSince.Remove(id); continue; }
                bool held = false; try { held = c.isHeld; } catch { }
                if (held || remoteHeld.ContainsKey(id) || storedIds.Contains(id)) { floatSince.Remove(id); continue; }
                var rb = c.GetComponent<Rigidbody>();
                if (rb == null) continue;
                if (rb.isKinematic || !rb.useGravity)
                {
                    float t0;
                    if (!floatSince.TryGetValue(id, out t0)) floatSince[id] = now;
                    else if (now - t0 > 5f)
                    {
                        rb.isKinematic = false; rb.useGravity = true; floatSince.Remove(id);
                        if (logs++ < 30) MpLog.Write("Anti-flotación: lata " + id + " liberada");
                    }
                }
                else floatSince.Remove(id);
            }
        }

        static void HostScan()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Can>();
            var alive = new HashSet<IntPtr>();
            foreach (var c in all)
            {
                alive.Add(c.Pointer);
                if (!hostIds.ContainsKey(c.Pointer)) { int id = nextId++; hostIds[c.Pointer] = id; hostById[id] = c; }
            }
            foreach (var kv in hostIds.Where(k => !alive.Contains(k.Key)).ToList())
            {
                if (storedIds.Contains(kv.Value)) continue;            // guardada en la aspiradora de un invitado
                hostIds.Remove(kv.Key); hostById.Remove(kv.Value); remoteHeld.Remove(kv.Value);
                lastLine.Remove(kv.Value); lastFull.Remove(kv.Value); lastPos.Remove(kv.Value); lastPosT.Remove(kv.Value);
                M.Send("CDEL", kv.Value.ToString());
            }
        }

        static void HostRelease(int id, Vector3 vel, bool wasThrown)
        {
            remoteHeld.Remove(id);
            if (hostById.TryGetValue(id, out var c) && c != null)
            {
                var rb = c.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = vel; }
                if (wasThrown) { try { c.OnThrown(vel); } catch { } }
                lastLine.Remove(id); lastFull.Remove(id);
            }
        }

        static void HostStore(int id)
        {
            if (!hostById.TryGetValue(id, out var c) || c == null) return;
            storedIds.Add(id); remoteHeld.Remove(id);
            try { c.gameObject.SetActive(false); } catch { }
            M.Send("CDEL", id.ToString());       // las demás pantallas la quitan
            if (logs++ < 30) MpLog.Write("Lata " + id + " guardada en la aspiradora de un invitado");
        }

        static void HostThrow(int id, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (!hostById.TryGetValue(id, out var c) || c == null) return;
            storedIds.Remove(id); remoteHeld.Remove(id);
            try
            {
                c.gameObject.SetActive(true);
                c.transform.position = pos; c.transform.rotation = rot;
                var rb = c.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = false; rb.velocity = vel; }
                c.OnThrown(vel);
                lastLine.Remove(id); lastFull.Remove(id);
                if (logs++ < 30) MpLog.Write("Lata " + id + " lanzada por un invitado, vel=" + vel);
            }
            catch (Exception ex) { MpLog.Write("ERROR CTHROW " + ex); }
        }

        // =====================================================================  INVITADO
        static bool NearReward(Vector3 pos)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastRewardScan > 5f)
            {
                lastRewardScan = now;
                try { rewards = UnityEngine.Object.FindObjectsOfType<RewardMachine>(); } catch { }
            }
            foreach (var r in rewards)
            {
                try { if (r != null && (r.transform.position - pos).magnitude < 6f) return true; } catch { }
            }
            return false;
        }

        static void Upsert(int id, Vector3 pos, Quaternion rot, Vector3 vel, int price, int qty, bool shiny, bool smoked, bool hio)
        {
            if (ghosts.ContainsKey(id)) return;
            if (detached.Values.Any(d => d.Key == id)) return;
            float now = Time.realtimeSinceStartup;
            if (!puppets.TryGetValue(id, out var p))
            {
                var gm = GameManager.Instance;
                if (gm == null) return;
                Can c = null;
                try { c = gm.GetCanFromPool(gm.pooledCanPrefab, pos, rot); }
                catch (Exception ex) { if (!createErr) { createErr = true; MpLog.Write("ERROR creando lata marioneta: " + ex.Message); } return; }
                if (c == null) return;
                try { c.Initialize(price, shiny, smoked); } catch { }
                try { c.quantity = qty; c.price = price; } catch { }
                try { c.HoleInOne = hio; } catch { }
                p = new CP { C = c, RB = c.GetComponent<Rigidbody>() };
                puppets[id] = p; puppetPtrs.Add(c.Pointer);
                try { c.transform.position = pos; c.transform.rotation = rot; } catch { }
                if (logs++ < 20) MpLog.Write("Lata marioneta creada id=" + id);
            }
            p.Pos = pos; p.Rot = rot; p.Vel = vel; p.Seen = now;
        }

        static void RemovePuppet(int id)
        {
            if (!puppets.TryGetValue(id, out var p)) return;
            puppets.Remove(id);
            if (p.C != null)
            {
                puppetPtrs.Remove(p.C.Pointer);
                try { if (p.C.gameObject.activeSelf && GameManager.Instance != null) GameManager.Instance.ReturnCanToPool(p.C); } catch { }
            }
        }

        static void GuestTick(float now)
        {
            float dt = Time.deltaTime;
            float k = 1f - Mathf.Exp(-16f * dt);
            foreach (var gk in ghosts.Where(g => now - g.Value > 25f).Select(g => g.Key).ToList()) ghosts.Remove(gk);

            bool sucking = false; Vector3 nozzle = Vector3.zero; float range = 0f;
            try
            {
                var vac = Vacuum.Instance;
                if (vac != null && vac.gameObject.activeInHierarchy && (int)vac.state == 1 && vac.point != null)
                { sucking = true; nozzle = vac.point.position; range = vac.suckRange * 1.15f; }
            }
            catch { }

            // Barrido: cualquier lata que NO sea una marioneta (p. ej. una que mi máquina local haya creado) se devuelve al pool
            if (now - lastSweep > 0.5f)
            {
                lastSweep = now;
                try
                {
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Can>())
                    {
                        if (puppetPtrs.Contains(c.Pointer)) continue;
                        bool held = false; try { held = c.isHeld; } catch { }
                        if (held) continue;
                        try { GameManager.Instance.ReturnCanToPool(c); } catch { }
                    }
                }
                catch { }
            }

            foreach (var kv in puppets.ToList())
            {
                int id = kv.Key; var p = kv.Value;
                if (p.C == null) { puppets.Remove(id); continue; }
                if (now - p.Seen > 4f) { RemovePuppet(id); continue; }
                var go = p.C.gameObject;

                if (!go.activeInHierarchy)
                {
                    puppets.Remove(id); puppetPtrs.Remove(p.C.Pointer);
                    if (p.OwnedUntil > 0f && now - p.OwnedUntil < 1.5f)
                    {   // la ha guardado MI aspiradora
                        detached[p.C.Pointer] = new KeyValuePair<int, CP>(id, p);
                        M.Send("CSTORE", id.ToString());
                        if (logs++ < 30) MpLog.Write("Guardé la lata " + id + " en mi aspiradora");
                    }
                    else if (NearReward(go.transform.position)) ghosts[id] = now;   // la vendió mi máquina local
                    continue;                                                         // (si no, se recrea con el siguiente CSYNC)
                }

                var tr = go.transform;
                bool heldNow = false; try { heldNow = p.C.isHeld; } catch { }
                bool inSuck = sucking && (tr.position - nozzle).sqrMagnitude < range * range;
                if (heldNow || inSuck) p.OwnedUntil = now + 0.4f;
                bool owned = heldNow || now < p.OwnedUntil;

                if (owned)
                {
                    if (p.RB != null && inSuck && p.RB.isKinematic) p.RB.isKinematic = false;
                    if (now >= p.NextBody)
                    {
                        p.NextBody = now + 0.05f;
                        var ps = tr.position; var q = tr.rotation; var v = p.RB != null ? p.RB.velocity : Vector3.zero;
                        M.Send("CBODY", id.ToString(), F(ps.x), F(ps.y), F(ps.z), F(q.x), F(q.y), F(q.z), F(q.w), F(v.x), F(v.y), F(v.z));
                    }
                    p.WasOwned = true;
                    continue;
                }

                if (p.WasOwned)
                {
                    p.WasOwned = false;
                    var ps = tr.position; var q = tr.rotation; var v = p.RB != null ? p.RB.velocity : Vector3.zero;
                    bool th = false;
                    if (thrown.TryGetValue(p.C.Pointer, out var tv) && now - tv.Key < 0.8f) { th = true; v = tv.Value; }
                    M.Send("CREL", id.ToString(), F(ps.x), F(ps.y), F(ps.z), F(q.x), F(q.y), F(q.z), F(q.w), F(v.x), F(v.y), F(v.z), th ? "1" : "0");
                    p.Seen = now;
                }

                if (p.RB != null && !p.RB.isKinematic) p.RB.isKinematic = true;
                float age = Mathf.Min(now - p.Seen, 0.25f);
                var target = p.Pos + p.Vel * age;
                if ((tr.position - target).sqrMagnitude > 9f) tr.position = target; else tr.position = Vector3.Lerp(tr.position, target, k);
                tr.rotation = Quaternion.Slerp(tr.rotation, p.Rot, k);
            }
        }

        /// <summary>MI aspiradora (o mi mano) lanza una lata.</summary>
        public static void OnLocalThrown(Can c, Vector3 vel)
        {
            if (M == null || M.IsHost || c == null) return;
            float now = Time.realtimeSinceStartup;
            thrown[c.Pointer] = new KeyValuePair<float, Vector3>(now, vel);
            if (detached.TryGetValue(c.Pointer, out var kvp))
            {   // la lata estaba guardada en mi aspiradora: el líder la reactiva donde yo la lance
                detached.Remove(c.Pointer);
                int id = kvp.Key; var p = kvp.Value;
                try
                {
                    var t = c.transform; var ps = t.position; var q = t.rotation;
                    M.Send("CTHROW", id.ToString(), F(ps.x), F(ps.y), F(ps.z), F(q.x), F(q.y), F(q.z), F(q.w), F(vel.x), F(vel.y), F(vel.z));
                    p.C = c; p.RB = c.GetComponent<Rigidbody>(); p.Seen = now; p.OwnedUntil = now + 0.6f; p.WasOwned = true;
                    puppets[id] = p; puppetPtrs.Add(c.Pointer);
                    if (logs++ < 30) MpLog.Write("Lancé la lata " + id + " desde mi aspiradora");
                }
                catch (Exception ex) { MpLog.Write("ERROR OnLocalThrown(lata) " + ex.Message); }
            }
        }

        // =====================================================================  MENSAJES
        public static void Handle(string type, string[] a)
        {
            try
            {
                switch (type)
                {
                    case "CSYNC":
                        if (M.IsHost || a.Length < 1) return;
                        recv++;
                        foreach (var ent in a[0].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var f = ent.Split(',');
                            if (f.Length < 16) continue;
                            Upsert(int.Parse(f[0]),
                                new Vector3(P(f[1]), P(f[2]), P(f[3])), new Quaternion(P(f[4]), P(f[5]), P(f[6]), P(f[7])),
                                new Vector3(P(f[8]), P(f[9]), P(f[10])), int.Parse(f[11]), int.Parse(f[12]),
                                f[13] == "1", f[14] == "1", f[15] == "1");
                        }
                        break;
                    case "CDEL":
                        if (!M.IsHost && a.Length > 0) { int id = int.Parse(a[0]); ghosts.Remove(id); RemovePuppet(id); }
                        break;
                    case "CBODY":
                        if (M.IsHost && a.Length >= 8 && hostById.TryGetValue(int.Parse(a[0]), out var hc) && hc != null)
                        {
                            int id = int.Parse(a[0]);
                            remoteHeld[id] = Time.realtimeSinceStartup;
                            var rb = hc.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
                            hc.transform.position = new Vector3(P(a[1]), P(a[2]), P(a[3]));
                            hc.transform.rotation = new Quaternion(P(a[4]), P(a[5]), P(a[6]), P(a[7]));
                        }
                        break;
                    case "CREL":
                        if (M.IsHost && a.Length >= 12)
                        {
                            int id = int.Parse(a[0]);
                            if (hostById.TryGetValue(id, out var rc) && rc != null)
                            {
                                rc.transform.position = new Vector3(P(a[1]), P(a[2]), P(a[3]));
                                rc.transform.rotation = new Quaternion(P(a[4]), P(a[5]), P(a[6]), P(a[7]));
                            }
                            HostRelease(id, new Vector3(P(a[8]), P(a[9]), P(a[10])), a[11] == "1");
                        }
                        break;
                    case "CSTORE":
                        if (M.IsHost && a.Length >= 1) HostStore(int.Parse(a[0]));
                        break;
                    case "CTHROW":
                        if (M.IsHost && a.Length >= 11)
                            HostThrow(int.Parse(a[0]), new Vector3(P(a[1]), P(a[2]), P(a[3])), new Quaternion(P(a[4]), P(a[5]), P(a[6]), P(a[7])),
                                      new Vector3(P(a[8]), P(a[9]), P(a[10])));
                        break;
                }
            }
            catch (Exception e) { MpLog.Write("ERROR CanSync.Handle(" + type + ") " + e); }
        }
    }
}
