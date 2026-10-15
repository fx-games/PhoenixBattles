using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Base.Core;
using Base.Levels;
using Base.Serialization;
using Base.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.ActorDeployment;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace PhoenixBattles
{
    // Online play over the Go relay (server/): newline JSON on TCP. The game is not deterministic, so turns are not
    // replayed: the side whose turn just ended ships a full tactical save and the other side loads it. While the
    // opponent plays, the local turn loop is parked (TurnHandover below), so nothing moves until the save arrives.
    public static class Net
    {
        public static string Status = "";
        public static int Seat = -1;
        public static bool Connected => _tcp != null && _tcp.Connected;
        public static bool InBattle => Connected && Battle.Active && Seat >= 0;
        public static string MyFaction => Battle.SeatFaction[Seat];
        static TcpClient _tcp; static StreamWriter _w;
        static readonly ConcurrentQueue<string> _in = new ConcurrentQueue<string>();
        static readonly string Key = Guid.NewGuid().ToString("N");
        static SerializationComponent Ser => GameUtl.GameComponent<SerializationComponent>();
        static string Ext => SerializationComponent.DefaultExtension;
        static Timing Timing => GameUtl.GameComponent<Base.Core.Game>().Timing;

        public static void Connect(string hostPort, string room)
        {
            Disconnect(); _closing = false;
            var hp = hostPort.Split(':');
            _tcp = new TcpClient(hp[0], hp.Length > 1 ? int.Parse(hp[1]) : 8378);
            _w = new StreamWriter(_tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            Send(new JObject { ["t"] = "join", ["room"] = room, ["key"] = Key });
            Send(new JObject { ["t"] = "hello", ["hash"] = "", ["build"] = "pb1" });
            var r = new StreamReader(_tcp.GetStream(), Encoding.UTF8);
            new Thread(() => { try { string l; while ((l = r.ReadLine()) != null) _in.Enqueue(l); } catch (Exception e) { if (!_closing) _in.Enqueue("{\"t\":\"error\",\"msg\":\"" + e.Message + "\"}"); } }) { IsBackground = true }.Start();
            Status = "connecting to " + room; Log.Line("net: " + Status);
        }

        static volatile bool _closing;
        public static void Disconnect() { _closing = true; try { _tcp?.Close(); } catch { } _tcp = null; Seat = -1; }

        public static void Send(JObject o) { lock (_in) { try { _w.Write(o.ToString(Newtonsoft.Json.Formatting.None) + "\n"); } catch (Exception e) { Log.Line("net send: " + e.Message); } } }

        // Main thread: dispatch everything the reader thread queued.
        public static void Poll()
        {
            while (_in.TryDequeue(out var line))
            {
                try { Handle(JObject.Parse(line)); } catch (Exception e) { Log.Line("net handle: " + e); }
            }
        }

        static void Handle(JObject m)
        {
            var t = (string)m["t"];
            if (t != "state" && t != "start") Log.Line("net <- " + m.ToString(Newtonsoft.Json.Formatting.None));
            switch (t)
            {
                case "welcome": Seat = (int)m["seat"]; Status = $"seat {Seat}, waiting for opponent"; break;
                case "equip": Status = "opponent here: edit your squad, then FIGHT"; if (!Squad.Editing) Squad.Open(); break;
                case "start": Timing.Start(StartCrt((string)m["loadouts"][0], (string)m["loadouts"][1], (int)(ulong.Parse((string)m["seed"]) & 0x7fffffff))); break;
                case "state": Timing.Start(LoadCrt((string)m["data"])); break;
                case "act": if ((int)m["seat"] != Seat && (string)m["a"]["k"] == "ability") ApplyAbility((JObject)m["a"]); break;
                case "over":
                    int w = (int)m["winner"]; Status = w < 0 ? "draw" : w == Seat ? "YOU WON" : "YOU LOST"; Status += " (" + (string)m["reason"] + ")";
                    Disconnect(); Status += " - connect with a new room code to play again";
                    if (Battle.Active && !_localGameOver && Level != null)
                    {
                        // Parked side: end the local copy the game's own way so the battle summary shows the verdict
                        foreach (var f in Level.Factions) if (Battle.SeatFaction.Contains(f.Faction.FactionDef.name))
                            f.State = f.Faction.FactionDef.name == Battle.SeatFaction[w] ? TacFactionState.Won : TacFactionState.Defeated;
                        Level.GameOver();
                    }
                    break;
                case "error": Status = "error: " + (string)m["msg"]; break;
            }
        }

        // ---- loadout exchange: the squad is serialized with the game's own save serializer ----
        public static void SendLoadout(List<ActorDeployData> squad) { Timing.Start(SendLoadoutCrt(squad)); Status = "loadout sent, waiting for opponent"; }
        // Record files are opened share-read, and a reader left behind blocks the next writer, so every record gets a fresh
        // name, outgoing data is captured in a MemoryStream alongside the file, and records are deleted once used.
        static int _n;
        static string Rec(string kind) => $"pb_{kind}{Seat}_{++_n}";
        static IEnumerator<NextUpdate> SendLoadoutCrt(List<ActorDeployData> squad)
        {
            var meta = new SavegameMetaData(Rec("lo"), Ser.SavegameVersion);
            var ok = new ByRef<bool>(false); var ms = new MemoryStream();
            yield return Timing.Current.Call(Ser.WriteSavegame(meta, new[] { new SavegameContentSection("Contents", new object[] { squad.ToArray() }) }, Ext, new Stream[] { ms }, ok));
            yield return Timing.Current.Call(Ser.DeleteSavegame(meta, new ByRef<bool>()));
            Log.Line($"net: loadout {ms.Length} bytes ok={ok.Value}");
            Send(new JObject { ["t"] = "loadout", ["data"] = Convert.ToBase64String(ms.ToArray()) });
        }
        static IEnumerator<NextUpdate> StartCrt(string lo0, string lo1, int seed)
        {
            var squads = new List<ActorDeployData>[2];
            for (int i = 0; i < 2; i++)
            {
                string rec = Rec("in") + Ext;
                yield return Timing.Current.Call(WriteRecord(rec, Convert.FromBase64String(i == 0 ? lo0 : lo1)));
                var sec = new SavegameContentSection("Contents");
                yield return Timing.Current.Call(Ser.ReadContentSection(rec, sec, new TimeSlice(Ser.SerializeTimeSlice)));
                yield return Timing.Current.Call(Ser.PlatformData.DeleteRecord(rec, new ByRef<bool>()));
                squads[i] = sec.ContentObjects?.OfType<ActorDeployData[]>().FirstOrDefault()?.ToList();
                Log.Line($"net: loadout {i}: {(squads[i] == null ? "FAILED" : squads[i].Count + " soldiers")}");
            }
            _localGameOver = false;
            Status = "battle: seat " + Seat;
            if (Squad.Editing) { Squad.Editing = false; Squad.Geo?.View?.ResetViewState(); }
            Battle.Start("SCV_PLT_56x56_A_PlotDef", seed, squads[0], squads[1]);
        }
        static IEnumerator<NextUpdate> WriteRecord(string name, byte[] data)
        {
            var ok = new ByRef<bool>(false);
            yield return Timing.Current.Call(Ser.PlatformData.WithRecord(name, Base.Platforms.PlatformData.OpenMode.Write, s => Write(s, data), ok));
        }
        static IEnumerator<NextUpdate> Write(Stream s, byte[] data) { s.Write(data, 0, data.Length); yield break; }

        // ---- turn handover ----
        // Runs in place of the opponent faction's turn: save, ship, then park until their save arrives (which reloads the level).
        public static IEnumerator<NextUpdate> HandoverCrt(TacticalLevelController level)
        {
            Status = "opponent's turn";
            // Fresh battle, nobody has moved: both sides hold the same state already, only the first mover acts
            if (!level.HasAnyTurnStarted && !level.IsFromSaveGame) { Log.Line("net: parked at battle start"); while (true) yield return NextUpdate.NextFrame; }
            // A mid-turn save keeps the current faction's AP as saved (DoNotResetThisTurn), and the opponent's AP is
            // stale here because their StartTurn never ran on this side: refill it so they can act after loading.
            foreach (var a in level.CurrentFaction.TacticalActors.ToList()) if (a.IsAlive) a.ForceRestartTurn();
            var meta = new SavegameMetaData(Rec("out"), Ser.SavegameVersion);
            var ok = new ByRef<bool>(false); var ms = new MemoryStream();
            yield return Timing.Current.Call(level.GetComponent<Level>().WriteSavegame(meta, Ext, null, new Stream[] { ms }, ok));
            yield return Timing.Current.Call(Ser.DeleteSavegame(meta, new ByRef<bool>()));
            Log.Line($"net: turn save {ms.Length} bytes ok={ok.Value}");
            Send(new JObject { ["t"] = "act", ["a"] = new JObject { ["k"] = "endturn" } });
            Send(new JObject { ["t"] = "state", ["seq"] = 0, ["data"] = Convert.ToBase64String(ms.ToArray()) });
            while (true) yield return NextUpdate.NextFrame;
        }
        static IEnumerator<NextUpdate> LoadCrt(string data)
        {
            string rec = Rec("state") + Ext; // stays on disk: the level loads lazily from it
            yield return Timing.Current.Call(WriteRecord(rec, Convert.FromBase64String(data)));
            var meta = new ByRef<SavegameMetaData>();
            yield return Timing.Current.Call(Ser.ReadMetaData(rec, meta, new TimeSlice(Ser.SerializeTimeSlice)));
            Log.Line("net: loading opponent's save, meta " + (meta.Value == null ? "MISSING" : meta.Value.Name));
            Status = "your turn";
            GameUtl.GameComponent<PhoenixGame>().FinishLevel(new LoadLevelGameResult(meta.Value.GetLevelSceneBindingToLoad()));
        }

        // ---- live view of the opponent's turn ----
        // Every ability the active player triggers through the UI (move, shoot, throw, ...) is sent as it happens and
        // re-executed on the parked side. Outcomes (hit rolls, reactions) can differ there; the save at turn end fixes
        // that. It only exists so the parked player watches the enemy act instead of getting a new board.
        static TacticalLevelController Level => GameUtl.CurrentLevel()?.GetComponent<TacticalLevelController>();
        static JToken Vec(Vector3 v) => float.IsNaN(v.x) ? null : new JArray(v.x, v.y, v.z);
        static Vector3 Vec(JToken t) => t == null || t.Type == JTokenType.Null ? TacticalAbilityTarget.InvalidPosition : new Vector3((float)t[0], (float)t[1], (float)t[2]);
        public static string Id(TacticalActorBase a) => a == null ? "-" : ((int)a.GeoUnitId).ToString();
        static JToken Ref(TacticalActorBase a) => a == null ? null : new JArray(a.TacticalFaction.Faction.FactionDef.name, (int)a.GeoUnitId, a.name);
        static TacticalActorBase Deref(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            string f = (string)t[0]; int id = (int)t[1]; string n = (string)t[2];
            var all = Level.Map.GetActors<TacticalActorBase>().Where(a => a.TacticalFaction?.Faction?.FactionDef?.name == f).ToList();
            return all.FirstOrDefault(a => id != 0 && (int)a.GeoUnitId == id) ?? all.FirstOrDefault(a => a.name == n);
        }
        public static void SendAbility(TacticalAbility ability, TacticalAbilityTarget t)
        {
            if (!InBattle || ability?.TacticalActorBase == null || ability.TacticalActorBase.TacticalFaction.Faction.FactionDef.name != MyFaction) return;
            var a = new JObject
            {
                ["k"] = "ability", ["actor"] = Ref(ability.TacticalActorBase), ["def"] = ability.TacticalAbilityDef.name,
                ["src"] = ability.EquipmentSource?.EquipmentDef?.name, ["over"] = ability.OverrideEquipment?.EquipmentDef?.name,
                ["tActor"] = Ref(t.Actor), ["tShoot"] = Ref(t.ShootTargetActor), ["grid"] = Vec(t.ActorGridPosition), ["pos"] = Vec(t.PositionToApply),
                ["from"] = Vec(t.ShootFromPos), ["dir"] = Vec(t.Direction), ["attack"] = (int)t.AttackType,
                ["eq"] = t.Equipment?.EquipmentDef?.name, ["item"] = t.TacticalItem?.TacticalItemDef?.name,
            };
            Send(new JObject { ["t"] = "act", ["a"] = a });
        }
        static void ApplyAbility(JObject a)
        {
            try
            {
                var actor = Deref(a["actor"]) as TacticalActor;
                if (actor == null) { Log.Line("net: ability actor not found " + a["actor"]); return; }
                string def = (string)a["def"], src = (string)a["src"];
                var ability = actor.GetAbilities<TacticalAbility>().FirstOrDefault(x => x.TacticalAbilityDef.name == def && (src == null || x.EquipmentSource?.EquipmentDef?.name == src))
                           ?? actor.GetAbilities<TacticalAbility>().FirstOrDefault(x => x.TacticalAbilityDef.name == def);
                if (ability == null) { Log.Line("net: ability not found " + def); return; }
                Equipment Eq(string n) => n == null ? null : actor.Equipments.Equipments.FirstOrDefault(e => e.EquipmentDef.name == n);
                if (a["over"]?.Type == JTokenType.String) ability.OverrideEquipment = Eq((string)a["over"]);
                var t = new TacticalAbilityTarget
                {
                    Actor = Deref(a["tActor"]), ShootTargetActor = Deref(a["tShoot"]), ActorGridPosition = Vec(a["grid"]), PositionToApply = Vec(a["pos"]),
                    ShootFromPos = Vec(a["from"]), Direction = Vec(a["dir"]), AttackType = (AttackType)(int)a["attack"], Equipment = Eq((string)a["eq"]),
                };
                t.TacticalItem = t.Equipment ?? Eq((string)a["item"]);
                Log.Line($"net: replay {def} by #{(int)actor.GeoUnitId} -> #{Id(t.ShootTargetActor ?? t.Actor)} {t}");
                ability.Activate(t);
            }
            catch (Exception e) { Log.Line("net: replay failed: " + e); }
        }

        // ---- game over: whoever sees it reports; the relay tells both ----
        static bool _localGameOver;
        public static void OnGameOver(TacticalLevelController level)
        {
            if (!InBattle) return;
            _localGameOver = true;
            var mine = level.Factions.First(f => f.Faction.FactionDef.name == MyFaction);
            string outcome = mine.State == PhoenixPoint.Tactical.Levels.TacFactionState.Won ? "win" : mine.State == PhoenixPoint.Tactical.Levels.TacFactionState.Defeated ? "lose" : "draw";
            Log.Line("net: game over, " + outcome);
            Send(new JObject { ["t"] = "result", ["outcome"] = outcome });
        }
    }

    [HarmonyPatch(typeof(TacticalViewState), "ActivateAbility")]
    static class StreamAbility
    {
        static void Postfix(TacticalAbility ability, TacticalAbilityTarget target) { if (Net.InBattle) Net.SendAbility(ability, target); }
    }

    [HarmonyPatch(typeof(TacticalFaction), "PlayTurnCrt")]
    static class TurnHandover
    {
        static bool Prefix(TacticalFaction __instance, ref IEnumerator<NextUpdate> __result)
        {
            if (!Net.InBattle || __instance.Faction.FactionDef.name != Battle.SeatFaction[1 - Net.Seat]) return true;
            Log.Line("net: handing turn to " + __instance.Faction.FactionDef.name);
            __instance.TurnNumber++; // what PlayTurnCrt would do; the save records it minus one and the load re-adds it
            __result = Net.HandoverCrt(__instance.TacticalLevel);
            return false;
        }
    }
}
