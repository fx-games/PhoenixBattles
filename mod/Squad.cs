using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Defs;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels;
using PhoenixPoint.Common.Levels.ActorDeployment;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Equipments;
using UnityEngine;

namespace PhoenixBattles
{
    // Squad editing uses the game's own soldier equip screen. That screen only exists in the geoscape, so we start a
    // throwaway new game, hand the Phoenix stores every item, drop the player into the roster editor and build the
    // battle from the resulting soldiers. Nothing of the campaign is ever shown.
    // The geoscape nav bar (GEOSCAPE / BASES / PERSONNEL / ...) becomes a single FIGHT button while editing.
    [HarmonyLib.HarmonyPatch(typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleGeoSectionBar), "Show")]
    static class FightBar
    {
        static void Postfix(PhoenixPoint.Geoscape.View.ViewModules.UIModuleGeoSectionBar __instance) { if (Squad.Editing) Apply(__instance); }
        public static void Apply(PhoenixPoint.Geoscape.View.ViewModules.UIModuleGeoSectionBar bar)
        {
            foreach (var b in bar.Buttons) if (b != null) b.gameObject.SetActive(b == bar.GeoscapeButton);
            foreach (var l in bar.GeoscapeButton.GetComponentsInChildren<I2.Loc.Localize>(true)) UnityEngine.Object.Destroy(l);
            foreach (var t in bar.GeoscapeButton.GetComponentsInChildren<UnityEngine.UI.Text>(true)) t.text = "FIGHT";
            bar.GeoscapeButton.SetState(true);
        }
    }

    // That button normally returns to the globe; for us it starts the battle.
    [HarmonyLib.HarmonyPatch(typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleGeoSectionBar), "ActivateGeoscapeContent")]
    static class FightButton
    {
        static bool Prefix() { if (!Squad.Editing) return true; Squad.Fight(); return false; }
    }

    public static class Squad
    {
        public static bool Editing;
        static bool _setup, _editorSeen;
        static DefRepository Repo => GameUtl.GameComponent<DefRepository>();
        static readonly string[] Templates = { "PX_AssaultStarting_CharacterTemplateDef", "PX_HeavyStarting_CharacterTemplateDef", "PX_SniperStarting_CharacterTemplateDef" };

        public static void Open()
        {
            var game = GameUtl.GameComponent<PhoenixGame>();
            var mode = game.Def.GameModeDefs.OfType<GeoscapeGameModeDef>().First();
            var p = ((GeoscapeGameParams)mode.LevelSceneBinding.LevelParams).Clone();
            p.Difficulty = SharedData.GetSharedDataFromGame().DifficultyLevels[1];
            p.TutorialEnabled = false; p.PlayIntroCinematic = false; p.IsIronmanMode = false;
            p.ExtraGameSettings = game.GetDefaultGameSettings();
            Editing = true; _setup = false; _editorSeen = false;
            Log.Line("squad: starting geoscape as squad editor");
            game.FinishLevel(new PlayNewGameResult { LevelSceneBinding = mode.LevelSceneBinding.CloneAndAddLevelParams(p) });
        }

        public static GeoLevelController Geo => GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();

        // Called every frame from ArenaMenu while editing: waits for the geoscape, then sets it up once.
        public static void Tick()
        {
            if (!Editing) return;
            var geo = Geo;
            if (_setup && geo != null && geo.View != null)
            {
                // The state keeps re-enabling the resource strip; BACK from the editor lands on the globe: go to the menu instead
                var res = geo.View.GeoscapeModules.ResourcesModule.gameObject; if (res.activeSelf) res.SetActive(false);
                if (geo.View.CurrentViewState is PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier) _editorSeen = true;
                if (_editorSeen && geo.View.CurrentViewState is PhoenixPoint.Geoscape.View.ViewStates.UIStateVehicleSelected) { Editing = false; GameUtl.GameComponent<PhoenixGame>().FinishLevel(new QuitGameResult { QuitGame = false }); }
                return;
            }
            if (_setup) return;
            if (geo == null || geo.View == null || geo.PhoenixFaction == null || geo.View.CurrentViewState == null) return;
            if (geo.View.CurrentViewState is PhoenixPoint.Geoscape.View.ViewStates.UIStateInitial || geo.View.CurrentViewState is PhoenixPoint.Geoscape.View.ViewStates.UIStateSimulation) return;
            _setup = true;
            try
            {
                var f = geo.PhoenixFaction;
                // Everything a soldier can carry, 10 of each
                foreach (var i in Repo.GetAllDefs<TacticalItemDef>().Where(i => i.Tags != null && i.Tags.Any(t => t != null && t.name == "ManufacturableItem_TagDef") && i.Tags.Any(t => t != null && t.name == "StandaloneItem_TagDef")))
                    f.ItemStorage.AddItem(new GeoItem(i, 3));
                // Top the roster up to 6 with one of each class; a new game starts with the squad inside the aircraft
                var vehicle = f.Vehicles.First();
                for (int i = 0; f.HumanSoldiers.Count() < 6 && vehicle.FreeCharacterSpace > 0; i++)
                    vehicle.AddCharacter(geo.CreateCharacterFromTemplate(Repo.GetAllDefs<TacCharacterDef>().First(t => t.name == Templates[i % Templates.Length]), f));
                Log.Line("squad: " + f.HumanSoldiers.Count() + " soldiers, " + f.ItemStorage.ToList().Count + " item stacks");
                geo.View.ToEditUnitState(f.HumanSoldiers.First(), f.HumanSoldiers.ToList());
                // No campaign chrome: resource strip off; the nav bar keeps one button, relabelled FIGHT
                geo.View.GeoscapeModules.ResourcesModule.gameObject.SetActive(false);
                FightBar.Apply(geo.View.GeoscapeModules.GeoSectionBarModule);
            }
            catch (Exception e) { Log.Line("squad setup failed: " + e); }
        }

        public static List<ActorDeployData> Current() => Geo.PhoenixFaction.HumanSoldiers.Select(c => c.GenerateActorDeployData()).ToList();

        public static void Fight()
        {
            var squad = Current(); var mirror = Current();
            if (Net.Connected) { Net.SendLoadout(squad); return; } // online: the battle starts when both loadouts are in
            Editing = false;
            // Leave the editor state while the geoscape is still the current level, its exit code needs it
            Geo.View.ResetViewState();
            // ponytail: hotseat opponent gets a mirror of the same squad; online play sends each side's own
            Battle.Start("SCV_PLT_56x56_A_PlotDef", new System.Random().Next(), squad, mirror);
        }
    }
}
