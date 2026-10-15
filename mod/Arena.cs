using System;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using Base.Defs;
using Base.Levels;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.MapGeneration;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Home.View.ViewStates;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace PhoenixBattles
{
    // Main menu hook: the first time the main menu shows, create our persistent menu object.
    [HarmonyPatch(typeof(UIStateMainMenu), "EnterState")]
    static class MainMenuHook
    {
        static void Postfix()
        {
            Battle.Active = false; Squad.Editing = false; // whatever got us here, the battle is over
            if (GameObject.Find("PhoenixBattles") != null) return;
            var go = new GameObject("PhoenixBattles");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ArenaMenu>();
            Log.Line("menu object created");
        }
    }

    // Battles only: no campaign, no geoscape. Leave Options and Exit, hide the rest of the vanilla menu.
    [HarmonyPatch(typeof(PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons), "Init")]
    static class HideCampaignButtons
    {
        static void Postfix(PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons __instance)
        {
            foreach (var f in typeof(PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons).GetFields())
            {
                if (f.Name == "OptionsButton" || f.Name == "ExitButton") continue;
                if (f.GetValue(__instance) is PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton b && b != null) b.gameObject.SetActive(false);
            }
            // New-game buttons are spawned per game mode, not fields: kill their parent too
            if (__instance.GameModueButtonsGroup != null) __instance.GameModueButtonsGroup.SetActive(false);
        }
    }

    // No DLC ad on the home screen.
    [HarmonyPatch(typeof(global::UIModuleAdPopupDLC), "UpdatePopupState")]
    static class HideDlcAd
    {
        static bool Prefix(global::UIModuleAdPopupDLC __instance) { __instance.Root.SetActive(false); return false; }
    }

    public class ArenaMenu : MonoBehaviour
    {
        static DefRepository Repo => GameUtl.GameComponent<DefRepository>();
        bool InMenu => GameUtl.CurrentLevel()?.LevelParams is MenuLevelParams;

        bool _autoDone; int _endTurns; float _nextEndTurn, _fightAt, _netEndAt;
        string _server = Environment.GetEnvironmentVariable("PB_SERVER") ?? "127.0.0.1:8378", _room = Environment.GetEnvironmentVariable("PB_ROOM") ?? "test";
        // PB_AUTO=dump|default runs that menu action by itself on the first main menu (shell-driven testing).
        static readonly bool Windowed = Environment.GetEnvironmentVariable("PB_WINDOW") != null;
        void Update()
        {
            // The game's options re-apply the saved fullscreen mode at boot, overriding Unity's -screen-fullscreen flag
            if (Windowed && Screen.fullScreen) { Screen.SetResolution(1280, 720, false); Log.Line("forced windowed mode"); }
            // test hook: PB_AUTO=battle-endturn ends the first two human turns by itself 20 s apart
            if (_autoDone && Battle.Active && _endTurns < 2 && Time.time > _nextEndTurn && Environment.GetEnvironmentVariable("PB_AUTO") == "battle-endturn")
            {
                var lvl = GameUtl.CurrentLevel()?.GetComponent<TacticalLevelController>();
                if (lvl != null && lvl.CurrentFaction != null && lvl.CurrentFaction.IsControlledByPlayer)
                {
                    Log.Line("auto end turn for " + lvl.CurrentFaction.TacticalFactionDef.name + " viewer=" + lvl.View?.ViewerFaction?.TacticalFactionDef?.name);
                    lvl.CurrentFaction.RequestEndTurn(); _endTurns++; _nextEndTurn = Time.time + 20;
                }
            }
            Squad.Tick(); Net.Poll();
            // test hook: PB_AUTO=net ends every own turn 15 s after it starts
            if (Net.InBattle && Environment.GetEnvironmentVariable("PB_AUTO") == "net")
            {
                var lvl = GameUtl.CurrentLevel()?.GetComponent<TacticalLevelController>();
                var f = lvl?.CurrentFaction;
                if (f != null && f.IsPlayingTurn && f.Faction.FactionDef.name == Net.MyFaction)
                {
                    if (_netEndAt == 0) _netEndAt = Time.realtimeSinceStartup + 15;
                    else if (Time.realtimeSinceStartup > _netEndAt)
                    {
                        _netEndAt = 0;
                        // PB_KILL=1: seat 0 wipes the enemy on its second turn instead of ending it (game-over test)
                        if (Environment.GetEnvironmentVariable("PB_KILL") != null && Net.Seat == 0 && f.TurnNumber >= 2)
                        { Log.Line("auto kill enemy (net)"); foreach (var a in lvl.Factions.First(x => x.Faction.FactionDef.name == Battle.SeatFaction[1]).TacticalActors.ToList()) a.Health.Set(0f); }
                        else if (!_netActed) { _netActed = true; _netEndAt = Time.realtimeSinceStartup + 12; Safe(() => AutoPlay(lvl, f)); }
                        else { _netActed = false; Log.Line("auto end turn (net)"); f.RequestEndTurn(); }
                    }
                }
            }
            // test hook: PB_AUTO=squad|net fights 30 s after the editor is up
            if (Squad.Editing && (Environment.GetEnvironmentVariable("PB_AUTO") == "squad" || Environment.GetEnvironmentVariable("PB_AUTO") == "net") && Squad.Geo?.View?.CurrentViewState is PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier)
            { if (_fightAt == 0) _fightAt = Time.realtimeSinceStartup + 30; else if (Time.realtimeSinceStartup > _fightAt) { _fightAt = float.MaxValue; Safe(Squad.Fight); } }
            if (_autoDone || !InMenu) return;
            _autoDone = true; _nextEndTurn = Time.time + 20;
            var auto = Environment.GetEnvironmentVariable("PB_AUTO");
            Log.Line("main menu up, PB_AUTO=" + auto);
            if (auto == "dump") { Safe(DumpDefs); Application.Quit(); }
            else if (auto == "default") Safe(StartDefault);
            else if (auto == "squad") Safe(Squad.Open);
            else if (auto == "net") Safe(() => Net.Connect(_server, _room));
            else if (auto != null && auto.StartsWith("battle")) Safe(() => Battle.Start("SCV_PLT_56x56_A_PlotDef", 1, Battle.DefaultSquad(0), Battle.DefaultSquad(1)));
        }

        void OnGUI()
        {
            // Retina / high-dpi: scale IMGUI so it stays readable
            float k = Screen.height / 720f; GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            if (InMenu)
            {
                GUILayout.BeginArea(new Rect(20, 20, 300, 260), "PHOENIX BATTLES", GUI.skin.window);
                GUILayout.Label("Online: server / room code");
                _server = GUILayout.TextField(_server); _room = GUILayout.TextField(_room);
                if (!Net.Connected && GUILayout.Button("Connect")) Safe(() => Net.Connect(_server, _room));
                if (Net.Connected && GUILayout.Button("Disconnect")) Net.Disconnect();
                GUILayout.Label(Net.Status);
                GUILayout.Space(8);
                if (GUILayout.Button("Edit squad (hotseat)")) Safe(Squad.Open);
                if (GUILayout.Button("Hotseat battle (default squads)")) Safe(() => Battle.Start("SCV_PLT_56x56_A_PlotDef", new System.Random().Next(), Battle.DefaultSquad(0), Battle.DefaultSquad(1)));
                if (GUILayout.Button("Dump defs")) Safe(DumpDefs);
                GUILayout.EndArea();
            }
            else if (Net.InBattle || Squad.Editing && Net.Connected)
            {
                GUI.Label(new Rect(Screen.width / k / 2 - 150, 4, 300, 24), Net.Status, new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter });
            }
        }

        bool _netActed;
        // Test autopilot for one turn: each soldier shoots a visible enemy, otherwise walks toward the enemy; acts are streamed like UI ones.
        static void AutoPlay(TacticalLevelController lvl, TacticalFaction f)
        {
            var enemy = lvl.Factions.First(x => x.Faction.FactionDef.name == Battle.SeatFaction[1 - Net.Seat]);
            var center = enemy.TacticalActors.Where(a => a.IsAlive).Select(a => a.Pos).Aggregate(Vector3.zero, (a, b) => a + b) / Mathf.Max(1, enemy.TacticalActors.Count(a => a.IsAlive));
            foreach (var actor in f.TacticalActors.Where(a => a.IsAlive).ToList())
            {
                var shoot = actor.Equipments.SelectedWeapon?.DefaultShootAbility;
                var shootTarget = shoot != null && shoot.IsEnabled() ? shoot.GetTargets().FirstOrDefault(t => t.Actor != null && t.Actor.TacticalFaction == enemy) : null;
                TacticalAbility ability; TacticalAbilityTarget target;
                if (shootTarget != null) { ability = shoot; target = shootTarget; }
                else
                {
                    var move = actor.GetAbilities<MoveAbility>().FirstOrDefault();
                    var best = move != null && move.IsEnabled() ? move.GetTargetsData().OrderBy(d => (d.Position - center).sqrMagnitude).FirstOrDefault() : null;
                    if (best == null) { Log.Line($"autoplay: #{(int)actor.GeoUnitId} idle: move={(move == null ? "none" : move.GetDisabledState().ToString())} ap={actor.CharacterStats.ActionPoints} alive={actor.IsAlive}"); continue; }
                    ability = move; target = new TacticalAbilityTarget(best.Position);
                }
                Log.Line($"autoplay: #{(int)actor.GeoUnitId} {ability.TacticalAbilityDef.name} -> #{Net.Id(target.ShootTargetActor ?? target.Actor)} {target}");
                ability.Activate(target); Net.SendAbility(ability, target);
            }
        }

        static string N(UnityEngine.Object o) => o ? o.name : "-";
        static string Names<T>(System.Collections.Generic.IEnumerable<T> xs) where T : UnityEngine.Object => xs == null ? "" : string.Join(",", xs.Select(N));
        static void Safe(Action a) { try { a(); } catch (Exception e) { Log.Line(e.ToString()); } }

        // Writes what the def repository holds that matters to us: mission types, plots, soldier templates, items.
        static void DumpDefs()
        {
            var sb = new StringBuilder();
            foreach (var m in Repo.GetAllDefs<TacMissionTypeDef>())
            {
                sb.Append("MISSION ").Append(m.name).Append(" playable=").Append(m.IsPlayableMission)
                  .Append(" skipDeploy=").Append(m.SkipDeploymentSelection).Append(" maxUnits=").Append(m.MaxPlayerUnits)
                  .Append(" plot=").Append(m.MapPlotDef ? m.MapPlotDef.name : "-").Append('\n');
                foreach (var p in m.ParticipantsData)
                    sb.Append("   part ").Append(p.ParticipantKind).Append(' ').Append(p.FactionDef ? p.FactionDef.name : "-").Append('\n');
                if (m.ParticipantsRelations != null) foreach (var r in m.ParticipantsRelations) sb.Append("   rel ").Append(r).Append('\n');
                if (m.CustomObjectives != null) foreach (var o in m.CustomObjectives) sb.Append("   obj ").Append(o ? o.name : "-").Append('\n');
            }
            foreach (var p in Repo.GetAllDefs<MapPlotDef>())
                sb.Append("PLOT ").Append(p.name).Append(" scene=").Append(p.LevelSceneDef ? p.LevelSceneDef.name : "-")
                  .Append(" tags=").Append(Names(p.MissionTags)).Append(" parcels=").Append(p.ParcelPositions.Count).Append('\n');
            foreach (var f in Repo.GetAllDefs<TacticalFactionDef>())
                sb.Append("FACTION ").Append(f.name).Append(" control=").Append(f.ControlType).Append(" pp=").Append(f.FactionDef ? f.FactionDef.name : "-").Append('\n');
            foreach (var c in Repo.GetAllDefs<TacCharacterDef>())
                sb.Append("CHAR ").Append(c.name).Append(" human=").Append(c.IsHuman).Append(" class=").Append(N(c.ClassTag))
                  .Append(" generic=").Append(c.AvailableForGenericDeployment).Append(" cost=").Append(c.DeploymentCost)
                  .Append(" eq=").Append(Names(c.Data.EquipmentItems))
                  .Append(" body=").Append(Names(c.Data.BodypartItems)).Append('\n');
            foreach (var i in Repo.GetAllDefs<TacticalItemDef>())
                sb.Append("ITEM ").Append(i.GetType().Name).Append(' ').Append(i.name).Append(" tags=").Append(Names(i.Tags)).Append('\n');
            File.WriteAllText(Path.Combine(Log.Dir, "defs.txt"), sb.ToString());
            Log.Line("defs dumped");
        }

        // Mirrors the game's own "loadmap" console command: default tactical params on the default plot.
        static void StartDefault()
        {
            var shared = GameUtl.Game().GetComponent<SharedData>();
            var def = (TacticalGameParams)shared.TacticalLevelDef.DefaultLevelParamsDef.LevelParams;
            var plot = Repo.GetAllDefs<MapPlotDef>().First(p => p.LevelSceneDef != null && p.ParcelPositions.Count > 0);
            var gp = new TacticalGameParams
            {
                MissionData = def.MissionData,
                MapPlotGenerationData = new MapPlotGenerationData { RandomSeed = 1, LocalTime = def.MapPlotGenerationData.LocalTime },
                GlobalTime = def.GlobalTime,
                LocationName = "Battle",
            };
            gp.MapPlotInstanceData = plot.GenerateInstanceData(gp.MapPlotGenerationData, new System.Random(1));
            gp.MissionData.MissionId = MissionId.CreateNew();
            Log.Line("starting plot " + plot.name + " mission " + gp.MissionData.MissionType?.name);
            GameUtl.GameComponent<PhoenixGame>().FinishLevel(new LoadLevelGameResult(plot.LevelSceneDef.Binding.CloneAndAddLevelParams(gp)));
        }
    }
}

namespace PhoenixBattles
{
    // The home screen plays a ~55 s cutscene before the main menu; the game has a console flag for skipping it.
    [HarmonyPatch(typeof(PhoenixPoint.Home.View.ViewStates.UIStateHomeScreenCutscene), "EnterState")]
    static class SkipHomeCutscene
    {
        static void Prefix() { PhoenixPoint.Home.View.ViewStates.UIStateHomeScreenCutscene.SkipCutscenesIntro = true; }
    }

    // Skip the boot intro level: boot straight into the menu.
    [HarmonyPatch(typeof(PhoenixGame), "BootCrt")]
    static class SkipIntro
    {
        static bool Prefix(PhoenixGame __instance, ref System.Collections.Generic.IEnumerator<Base.Core.NextUpdate> __result)
        {
            __instance.Curtain.SetLoadingScreenVisible(true);
            __result = (System.Collections.Generic.IEnumerator<Base.Core.NextUpdate>)AccessTools.Method(typeof(PhoenixGame), "MenuCrt")
                .Invoke(__instance, new object[] { null, MenuEnterReason.None });
            return false;
        }
    }
}
