using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.ActorDeployment;
using PhoenixPoint.Common.Levels.MapGeneration;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.ActorsInstance;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.FactionObjectives;

namespace PhoenixBattles
{
    // One PvP battle: seat 0 = Phoenix (mission "Player"), seat 1 = New Jericho (mission "Intruder"). Both human.
    public static class Battle
    {
        public static bool Active;
        public static readonly string[] SeatFaction = { "Phoenix_FactionDef", "NewJericho_FactionDef" };
        static DefRepository Repo => GameUtl.GameComponent<DefRepository>();
        public static T Def<T>(string name) where T : BaseDef => Repo.GetAllDefs<T>().First(d => d.name == name);

        // The PvP mission type is a runtime clone of an ambush mission; saves do not carry it back intact, so it is rebuilt on load (see FixMissionOnLoad).
        public static CustomMissionTypeDef MakeMission()
        {
            var baseType = Def<CustomMissionTypeDef>("AmbushNJ_CustomMissionTypeDef");
            var mission = (CustomMissionTypeDef)Repo.CreateRuntimeDef(baseType, typeof(CustomMissionTypeDef));
            mission.ParticipantsRelations = new[] { new MutualParticipantsRelations { FirstParticipant = TacMissionParticipant.Player, SecondParticipant = TacMissionParticipant.Intruder, MutualRelation = FactionRelation.Enemy } };
            mission.CustomObjectives = new FactionObjectiveDef[] { Def<WipeEnemyFactionObjectiveDef>("WipeEnemy_CustomMissionObjective"), Def<WipeEnemyFactionObjectiveDef>("IWipeEnemy_CustomMissionObjective") };
            mission.MapPlotDef = null;
            mission.SkipDeploymentSelection = true;
            return mission;
        }

        public static void Start(string plotName, int seed, List<ActorDeployData> squad0, List<ActorDeployData> squad1)
        {
            var mission = MakeMission();
            var md = new TacMissionData { MissionType = mission, MissionParticipants = new List<TacMissionFactionData>(), MissionId = MissionId.CreateNew() };
            md.AddPlayer(Def<PPFactionDef>(SeatFaction[0]), squad0);
            md.AddIntruders(Def<PPFactionDef>(SeatFaction[1]), squad1, 100000, 0, 0);

            var shared = GameUtl.Game().GetComponent<SharedData>();
            var def = (TacticalGameParams)shared.TacticalLevelDef.DefaultLevelParamsDef.LevelParams;
            var plot = Def<MapPlotDef>(plotName);
            var gp = new TacticalGameParams
            {
                MissionData = md,
                MapPlotGenerationData = new MapPlotGenerationData { RandomSeed = seed, LocalTime = def.MapPlotGenerationData.LocalTime },
                GlobalTime = def.GlobalTime,
                LocationName = "Battle",
                Statistics = (PhoenixStatistics)GameUtl.GameComponent<PhoenixStatisticsManager>().GetStatistics(),
            };
            gp.MapPlotInstanceData = plot.GenerateInstanceData(gp.MapPlotGenerationData, new System.Random(seed));
            Active = true;
            Log.Line($"battle: plot {plot.name} seed {seed} squads {squad0.Count}/{squad1.Count}");
            GameUtl.GameComponent<PhoenixGame>().FinishLevel(new LoadLevelGameResult(plot.LevelSceneDef.Binding.CloneAndAddLevelParams(gp)));
        }

        // A soldier built from one of the game's character templates (its own gear unless overridden by the gear screen).
        public static ActorDeployData Soldier(string templateName, string name) => Soldier(Def<TacCharacterDef>(templateName), name);
        public static ActorDeployData Soldier(TacCharacterDef tpl, string name)
        {
            var data = new TacCharacterData(tpl.Data) { Name = name };
            return new ActorDeployData(data)
            {
                InstanceDef = tpl,
                ActorTags = tpl.DefaultDeploymentTags.ToList(),
                Unique = true, DeployCost = 0f, ChanceWeight = 1f, CanFailToSpawn = false, DeployWithPriority = true, CanBeInitialDeployment = true,
            };
        }

        public static List<ActorDeployData> DefaultSquad(int seat)
        {
            var names = seat == 0
                ? new[] { "PX_AssaultStarting_CharacterTemplateDef", "PX_HeavyStarting_CharacterTemplateDef", "PX_SniperStarting_CharacterTemplateDef", "PX_AssaultStarting_CharacterTemplateDef" }
                : new[] { "NJ_Assault3_CharacterTemplateDef", "NJ_Heavy4_CharacterTemplateDef", "NJ_Sniper3_CharacterTemplateDef", "NJ_Technician3_CharacterTemplateDef" };
            return names.Select((n, i) => Soldier(n, (seat == 0 ? "PX " : "NJ ") + (i + 1))).ToList();
        }
    }

    // The battle summary's continue button would load the geoscape; there is none, go to the main menu.
    [HarmonyPatch(typeof(PhoenixPoint.Tactical.View.TacticalView), "GoToGeoscape")]
    static class SummaryToMenu
    {
        static bool Prefix() { if (!Battle.Active) return true; Battle.Active = false; GameUtl.GameComponent<PhoenixGame>().FinishLevelAndGoToLobby(); return false; }
    }

    [HarmonyPatch(typeof(PhoenixPoint.Tactical.Levels.Missions.TacMission), "Init")]
    static class FixMissionOnLoad
    {
        static void Prefix(TacticalLevelController level)
        {
            if (Battle.Active && level.TacticalGameParams.MissionData.MissionType.ParticipantsRelations == null)
            { level.TacticalGameParams.MissionData.MissionType = Battle.MakeMission(); Log.Line("mission type rebuilt after load"); }
        }
    }

    // Both battle factions are human-controlled (the New Jericho def says AI).
    [HarmonyPatch(typeof(TacticalLevelController), "CreateFactions")]
    static class HumanFactions
    {
        static void Postfix(TacticalLevelController __instance)
        {
            if (!Battle.Active) return;
            foreach (var f in __instance.Factions)
                if (Battle.SeatFaction.Contains(f.Faction.FactionDef.name)) f.SetControlType(FactionControlType.Human);
            Log.Line("factions: " + string.Join(", ", __instance.Factions.Select(f => f.Faction.FactionDef.name + "=" + f.ControlType)));
            __instance.GameOverEvent += Net.OnGameOver;
        }
    }
}

namespace PhoenixBattles
{
    using PhoenixPoint.Tactical.Levels.ActorDeployment;
    using PhoenixPoint.Tactical.Levels.Missions;
    using UnityEngine;

    // Plots only carry Intruder zones (the player normally lands by aircraft). Split the intruder zones into two
    // groups along the map's widest axis and hand one group to each seat, so squads start on opposite sides.
    [HarmonyPatch(typeof(TacMission), "InitDeployZones")]
    static class SplitDeployZones
    {
        static void Postfix(TacMission __instance)
        {
            if (!Battle.Active) return;
            var level = __instance.TacticalLevel;
            var zones = level.Map.GetActors<TacticalDeployZone>().Where(z => z.MissionParticipant == TacMissionParticipant.Intruder).ToList();
            if (zones.Count < 2) { Log.Line("deploy: only " + zones.Count + " intruder zones"); return; }
            var min = zones.Select(z => z.Pos).Aggregate(Vector3.Min); var max = zones.Select(z => z.Pos).Aggregate(Vector3.Max);
            bool byX = max.x - min.x >= max.z - min.z;
            zones.Sort((a, b) => byX ? a.Pos.x.CompareTo(b.Pos.x) : a.Pos.z.CompareTo(b.Pos.z));
            var seat0 = level.GetTacticalFaction(Battle.Def<PPFactionDef>(Battle.SeatFaction[0]));
            var seat1 = level.GetTacticalFaction(Battle.Def<PPFactionDef>(Battle.SeatFaction[1]));
            for (int i = 0; i < zones.Count; i++)
            {
                bool first = i < zones.Count / 2;
                zones[i].SetFaction(first ? seat0 : seat1, first ? TacMissionParticipant.Player : TacMissionParticipant.Intruder);
                // Plot zones are tiny (one tile): widen them so a whole squad fits around the spot
                var box = zones[i].GetComponent<BoxCollider>();
                if (box != null) box.size = new Vector3(Mathf.Max(box.size.x, 8f), Mathf.Max(box.size.y, 4f), Mathf.Max(box.size.z, 8f));
            }
            Log.Line($"deploy: {zones.Count} zones split {(byX ? "by x" : "by z")}: {zones.Count / 2} to seat 0, rest to seat 1");
        }
    }

    // Zone tag filters (grunt/elite/monster) mean nothing in a PvP battle: every zone takes every soldier.
    [HarmonyPatch(typeof(MissionDeployCondition), "IncludesActor")]
    static class AnyActorInZone
    {
        static bool Prefix(ref bool __result) { if (!Battle.Active) return true; __result = true; return false; }
    }
}
