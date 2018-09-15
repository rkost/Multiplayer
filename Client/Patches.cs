﻿using Harmony;
using LiteNetLib;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Profile;

namespace Multiplayer.Client
{
    static class HarmonyPatches
    {
        [MpPrefix(typeof(PatchProcessor), nameof(PatchProcessor.Patch))]
        static void PatchProcessorPrefix(List<MethodBase> ___originals)
        {
            foreach (MethodBase m in ___originals)
            {
                MarkNoInlining(m);
            }
        }

        public unsafe static void MarkNoInlining(MethodBase method)
        {
            ushort* iflags = (ushort*)(method.MethodHandle.Value) + 1;
            *iflags |= (ushort)MethodImplOptions.NoInlining;
        }

        static readonly FieldInfo paramName = AccessTools.Field(typeof(ParameterInfo), "NameImpl");

        [MpPrefix(typeof(MethodPatcher), "EmitCallParameter")]
        static void EmitCallParamsPrefix(MethodBase original, MethodInfo patch)
        {
            if (Attribute.GetCustomAttribute(patch, typeof(IndexedPatchParameters)) == null)
                return;

            ParameterInfo[] patchParams = patch.GetParameters();

            for (int i = 0; i < patchParams.Length; i++)
            {
                string name;

                if (original.IsStatic)
                    name = original.GetParameters()[i].Name;
                else if (i == 0)
                    name = MethodPatcher.INSTANCE_PARAM;
                else
                    name = original.GetParameters()[i - 1].Name;

                paramName.SetValue(patchParams[i], name);
            }
        }
    }

    // For instance methods the first parameter is the instance
    // The rest are original method's parameters in order
    [AttributeUsage(AttributeTargets.Method)]
    public class IndexedPatchParameters : Attribute
    {
    }

    [MpPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
    public static class MainMenuMarker
    {
        public static bool drawing;

        static void Prefix() => drawing = true;

        static void Postfix() => drawing = false;
    }

    [HarmonyPatch(typeof(WildAnimalSpawner))]
    [HarmonyPatch(nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    public static class WildAnimalSpawnerTickMarker
    {
        public static bool ticking;

        static void Prefix() => ticking = true;

        static void Postfix() => ticking = false;
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects))]
    [HarmonyPatch(nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
    public static class SteadyEnvironmentEffectsTickMarker
    {
        public static bool ticking;

        static void Prefix() => ticking = true;

        static void Postfix() => ticking = false;
    }

    [MpPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
    public static class MainMenuPatch
    {
        public static Stopwatch time = new Stopwatch();

        static void Prefix(Rect rect, List<ListableOption> optList)
        {
            if (!MainMenuMarker.drawing) return;

            if (Current.ProgramState == ProgramState.Entry)
            {
                int newColony = optList.FindIndex(opt => opt.label == "NewColony".Translate());
                if (newColony != -1)
                    optList.Insert(newColony + 1, new ListableOption("Connect to server", () =>
                    {
                        Find.WindowStack.Add(new ServerBrowser());
                    }));
            }

            if (optList.Any(opt => opt.label == "ReviewScenario".Translate()))
            {
                if (Multiplayer.LocalServer == null && Multiplayer.Client == null)
                {
                    optList.Insert(0, new ListableOption("Host a server", () =>
                    {
                        Find.WindowStack.Add(new HostWindow());
                    }));
                }

                if (Multiplayer.Client != null)
                {
                    optList.Insert(0, new ListableOption("Autosave", () =>
                    {
                        /*Stopwatch ticksStart = Stopwatch.StartNew();
                        for (int i = 0; i < 1000; i++)
                        {
                            Find.TickManager.DoSingleTick();
                        }
                        Log.Message("1000 ticks took " + ticksStart.ElapsedMilliseconds + "ms (" + (ticksStart.ElapsedMilliseconds / 1000.0) + ")");
                        */

                        //Multiplayer.SendGameData(Multiplayer.SaveGame());

                        Multiplayer.LocalServer.DoAutosave();
                    }));

                    optList.RemoveAll(opt => opt.label == "Save".Translate() || opt.label == "LoadGame".Translate());

                    optList.FirstOrDefault(opt => opt.label == "QuitToMainMenu".Translate()).action = () =>
                    {
                        OnMainThread.StopMultiplayer();
                        GenScene.GoToMainMenu();
                    };

                    optList.FirstOrDefault(opt => opt.label == "QuitToOS".Translate()).action = () => Root.Shutdown();
                }
            }
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow))]
    public static class MapDrawerRegenPatch
    {
        public static Dictionary<int, MapDrawer> copyFrom = new Dictionary<int, MapDrawer>();

        static bool Prefix(MapDrawer __instance)
        {
            Map map = __instance.map;
            if (!copyFrom.TryGetValue(map.uniqueID, out MapDrawer keepDrawer)) return true;

            map.mapDrawer = keepDrawer;
            keepDrawer.map = map;

            foreach (Section s in keepDrawer.sections)
            {
                s.map = map;

                for (int i = 0; i < s.layers.Count; i++)
                {
                    SectionLayer layer = s.layers[i];
                    if (!ShouldKeep(layer))
                        s.layers[i] = (SectionLayer)Activator.CreateInstance(layer.GetType(), s);
                    else if (layer is SectionLayer_LightingOverlay lighting)
                        lighting.glowGrid = map.glowGrid.glowGrid;
                }
            }

            foreach (Section s in keepDrawer.sections)
                foreach (SectionLayer layer in s.layers)
                    if (!ShouldKeep(layer))
                        layer.Regenerate();

            copyFrom.Remove(map.uniqueID);

            return false;
        }

        static bool ShouldKeep(SectionLayer layer)
        {
            return layer.GetType().Assembly == typeof(Game).Assembly &&
                layer.GetType() != typeof(SectionLayer_TerrainScatter);
        }
    }

    //[HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.RebuildAllRegionsAndRooms))]
    public static class RebuildRegionsAndRoomsPatch
    {
        public static Dictionary<int, RegionGrid> copyFrom = new Dictionary<int, RegionGrid>();

        static bool Prefix(RegionAndRoomUpdater __instance)
        {
            Map map = __instance.map;
            if (!copyFrom.TryGetValue(map.uniqueID, out RegionGrid oldRegions)) return true;

            __instance.initialized = true;
            map.temperatureCache.ResetTemperatureCache();

            oldRegions.map = map; // for access to cellIndices in the iterator

            foreach (Region r in oldRegions.AllRegions_NoRebuild_InvalidAllowed)
            {
                r.cachedAreaOverlaps = null;
                r.cachedDangers.Clear();
                r.mark = 0;
                r.reachedIndex = 0;
                r.closedIndex = new uint[RegionTraverser.NumWorkers];
                r.cachedCellCount = -1;
                r.mapIndex = (sbyte)map.Index;

                if (r.door != null)
                    r.door = map.ThingReplacement(r.door);

                foreach (List<Thing> things in r.listerThings.listsByGroup.Concat(r.ListerThings.listsByDef.Values))
                    if (things != null)
                        for (int j = 0; j < things.Count; j++)
                            if (things[j] != null)
                                things[j] = map.ThingReplacement(things[j]);

                Room rm = r.Room;
                if (rm == null) continue;

                rm.mapIndex = (sbyte)map.Index;
                rm.cachedCellCount = -1;
                rm.cachedOpenRoofCount = -1;
                rm.statsAndRoleDirty = true;
                rm.stats = new DefMap<RoomStatDef, float>();
                rm.role = null;
                rm.uniqueNeighbors.Clear();
                rm.uniqueContainedThings.Clear();

                RoomGroup rg = rm.groupInt;
                rg.tempTracker.cycleIndex = 0;
            }

            for (int i = 0; i < oldRegions.regionGrid.Length; i++)
                map.regionGrid.regionGrid[i] = oldRegions.regionGrid[i];

            copyFrom.Remove(map.uniqueID);

            return false;
        }
    }

    [HarmonyPatch(typeof(WorldGrid))]
    public static class WorldGridCtorPatch
    {
        public static WorldGrid copyFrom;

        static bool Prefix(WorldGrid __instance, int ___cachedTraversalDistance, int ___cachedTraversalDistanceForStart, int ___cachedTraversalDistanceForEnd)
        {
            if (copyFrom == null) return true;

            WorldGrid grid = __instance;

            grid.viewAngle = copyFrom.viewAngle;
            grid.viewCenter = copyFrom.viewCenter;
            grid.verts = copyFrom.verts;
            grid.tileIDToNeighbors_offsets = copyFrom.tileIDToNeighbors_offsets;
            grid.tileIDToNeighbors_values = copyFrom.tileIDToNeighbors_values;
            grid.tileIDToVerts_offsets = copyFrom.tileIDToVerts_offsets;
            grid.averageTileSize = copyFrom.averageTileSize;

            grid.tiles = new List<Tile>();
            ___cachedTraversalDistance = -1;
            ___cachedTraversalDistanceForStart = -1;
            ___cachedTraversalDistanceForEnd = -1;

            copyFrom = null;

            return false;
        }
    }

    [HarmonyPatch(typeof(WorldRenderer))]
    public static class WorldRendererCtorPatch
    {
        public static WorldRenderer copyFrom;

        static bool Prefix(WorldRenderer __instance)
        {
            if (copyFrom == null) return true;

            __instance.layers = copyFrom.layers;
            copyFrom = null;

            return false;
        }
    }

    // Fixes a lag spike when opening debug tools
    [HarmonyPatch(typeof(UIRoot))]
    [HarmonyPatch(nameof(UIRoot.UIRootOnGUI))]
    static class UIRootPatch
    {
        static bool ran;

        static void Prefix()
        {
            if (ran) return;
            GUI.skin.font = Text.fontStyles[1].font;
            ran = true;
        }
    }

    [HarmonyPatch(typeof(SavedGameLoaderNow))]
    [HarmonyPatch(nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow))]
    [HarmonyPatch(new[] { typeof(string) })]
    public static class LoadPatch
    {
        public static XmlDocument gameToLoad;

        static bool Prefix()
        {
            if (gameToLoad == null) return false;

            bool prevCompress = SaveCompression.doSaveCompression;
            SaveCompression.doSaveCompression = true;

            ScribeUtil.StartLoading(gameToLoad);
            ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, false);
            Scribe.EnterNode("game");
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Prefs.PauseOnLoad = false;
            Current.Game.LoadGame(); // calls Scribe.loader.FinalizeLoading()
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            SaveCompression.doSaveCompression = prevCompress;
            gameToLoad = null;

            Log.Message("Game loaded");

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (!Current.Game.Maps.Any())
                {
                    MemoryUtility.UnloadUnusedUnityAssets();
                    Find.World.renderer.RegenerateAllLayersNow();
                }

                /*Find.WindowStack.Add(new CustomSelectLandingSite()
                {
                    nextAct = () => Settle()
                });*/
            });

            return false;
        }

        private static void Settle()
        {
            // notify the server of map gen pause?

            Find.GameInitData.mapSize = 150;
            Find.GameInitData.startingAndOptionalPawns.Add(StartingPawnUtility.NewGeneratedStartingPawn());
            Find.GameInitData.startingAndOptionalPawns.Add(StartingPawnUtility.NewGeneratedStartingPawn());

            Settlement settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.SetFaction(Multiplayer.RealPlayerFaction);
            settlement.Tile = Find.GameInitData.startingTile;
            settlement.Name = SettlementNameGenerator.GenerateSettlementName(settlement);
            Find.WorldObjects.Add(settlement);

            IntVec3 intVec = new IntVec3(Find.GameInitData.mapSize, 1, Find.GameInitData.mapSize);
            Map visibleMap = MapGenerator.GenerateMap(intVec, settlement, settlement.MapGeneratorDef, settlement.ExtraGenStepDefs, null);
            Find.World.info.initialMapSize = intVec;
            PawnUtility.GiveAllStartingPlayerPawnsThought(ThoughtDefOf.NewColonyOptimism);
            Current.Game.CurrentMap = visibleMap;
            Find.CameraDriver.JumpToCurrentMapLoc(MapGenerator.PlayerStartSpot);
            Find.CameraDriver.ResetSize();
            Current.Game.InitData = null;

            Log.Message("New map: " + visibleMap.GetUniqueLoadID());

            ClientPlayingState.SyncClientWorldObj(settlement);
        }
    }

    [HarmonyPatch(typeof(MainButtonsRoot))]
    [HarmonyPatch(nameof(MainButtonsRoot.MainButtonsOnGUI))]
    public static class MainButtonsPatch
    {
        static bool Prefix()
        {
            Text.Font = GameFont.Small;

            int timerLag = (TickPatch.tickUntil - (int)TickPatch.timerInt);
            string text = $"{Find.TickManager.TicksGame} {TickPatch.timerInt} {TickPatch.tickUntil} {timerLag} {Time.deltaTime * 60f}";

            Rect rect = new Rect(80f, 60f, 330f, Text.CalcHeight(text, 330f));
            Widgets.Label(rect, text);

            if (Find.CurrentMap != null && Multiplayer.Client != null)
            {
                MapAsyncTimeComp async = Find.CurrentMap.GetComponent<MapAsyncTimeComp>();
                string text1 = "" + async.mapTicks;

                text1 += " r:" + Find.CurrentMap.reservationManager.AllReservedThings().Count();

                int faction = Find.CurrentMap.info.parent.Faction.loadID;
                MultiplayerMapComp comp = Find.CurrentMap.GetComponent<MultiplayerMapComp>();
                FactionMapData data = comp.factionMapData.GetValueSafe(faction);

                if (data != null)
                {
                    text1 += " h:" + data.listerHaulables.ThingsPotentiallyNeedingHauling().Count;
                    text1 += " sg:" + data.haulDestinationManager.AllGroupsListForReading.Count;
                }

                if (comp.mapIdBlock != null)
                    text1 += " " + comp.mapIdBlock.current;

                text1 += " " + Sync.bufferedChanges.Sum(kv => kv.Value.Count);

                Rect rect1 = new Rect(80f, 110f, 330f, Text.CalcHeight(text1, 330f));
                Widgets.Label(rect1, text1);
            }

            if (Widgets.ButtonText(new Rect(Screen.width - 60f, 10f, 50f, 25f), "Chat"))
                Find.WindowStack.Add(Multiplayer.Chat);

            if (Widgets.ButtonText(new Rect(Screen.width - 60f, 35f, 50f, 25f), "Packets"))
                Find.WindowStack.Add(Multiplayer.PacketLog);

            return Find.Maps.Count > 0;
        }
    }

    [HarmonyPatch(typeof(CaravanArrivalAction_AttackSettlement))]
    [HarmonyPatch(nameof(CaravanArrivalAction_AttackSettlement.Arrived))]
    public static class AttackSettlementPatch
    {
        static bool Prefix(CaravanArrivalAction_AttackSettlement __instance, Caravan caravan)
        {
            if (Multiplayer.Client == null) return true;

            SettlementBase settlement = __instance.settlement;
            if (settlement.Faction.def != Multiplayer.factionDef) return true;

            Multiplayer.Client.Send(Packets.CLIENT_ENCOUNTER_REQUEST, new object[] { settlement.Tile });

            return false;
        }
    }

    [HarmonyPatch(typeof(Settlement))]
    [HarmonyPatch(nameof(Settlement.ShouldRemoveMapNow))]
    public static class ShouldRemoveMapPatch
    {
        static bool Prefix() => Multiplayer.Client == null;
    }

    [HarmonyPatch(typeof(SettlementDefeatUtility))]
    [HarmonyPatch(nameof(SettlementDefeatUtility.CheckDefeated))]
    public static class CheckDefeatedPatch
    {
        static bool Prefix() => Multiplayer.Client == null;
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch(nameof(Pawn_JobTracker.StartJob))]
    public static class JobTrackerStart
    {
        static void Prefix(Pawn_JobTracker __instance, Job newJob, ref Container<Map> __state)
        {
            if (Multiplayer.Client == null) return;
            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Postfix(Container<Map> __state)
        {
            if (__state != null)
                __state.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch(nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerEndCurrent
    {
        static void Prefix(Pawn_JobTracker __instance, JobCondition condition, ref Container<Map> __state)
        {
            if (Multiplayer.Client == null) return;
            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Postfix(Container<Map> __state)
        {
            if (__state != null)
                __state.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch(nameof(Pawn_JobTracker.CheckForJobOverride))]
    public static class JobTrackerOverride
    {
        static void Prefix(Pawn_JobTracker __instance, ref Container<Map> __state)
        {
            if (Multiplayer.Client == null) return;
            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            ThingContext.Push(pawn);
            __state = pawn.Map;
        }

        static void Postfix(Container<Map> __state)
        {
            if (__state != null)
            {
                __state.PopFaction();
                ThingContext.Pop();
            }
        }
    }

    public static class ThingContext
    {
        private static Stack<Pair<Thing, Map>> stack = new Stack<Pair<Thing, Map>>();

        static ThingContext()
        {
            stack.Push(new Pair<Thing, Map>(null, null));
        }

        public static Thing Current => stack.Peek().First;
        public static Pawn CurrentPawn => Current as Pawn;

        public static Map CurrentMap
        {
            get
            {
                Pair<Thing, Map> peek = stack.Peek();
                if (peek.First != null && peek.First.Map != peek.Second)
                    Log.ErrorOnce("Thing " + peek.First + " has changed its map!", peek.First.thingIDNumber ^ 57481021);
                return peek.Second;
            }
        }

        public static void Push(Thing t)
        {
            stack.Push(new Pair<Thing, Map>(t, t.Map));
        }

        public static void Pop()
        {
            stack.Pop();
        }
    }

    [HarmonyPatch(typeof(GameEnder))]
    [HarmonyPatch(nameof(GameEnder.CheckOrUpdateGameOver))]
    public static class GameEnderPatch
    {
        static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Faction))]
    [HarmonyPatch(nameof(Faction.OfPlayer), PropertyMethod.Getter)]
    public static class FactionOfPlayerPatch
    {
        static void Prefix()
        {
            if (Multiplayer.Ticking && FactionContext.stack.Count == 0)
                Log.Warning("Faction context not set during ticking");
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager))]
    [HarmonyPatch(nameof(UniqueIDsManager.GetNextID))]
    public static class UniqueIdsPatch
    {
        private static IdBlock currentBlock;
        public static IdBlock CurrentBlock
        {
            get => currentBlock;

            set
            {
                if (value != null && currentBlock != null && currentBlock != value)
                    Log.Warning("Reassigning the current id block!");
                currentBlock = value;
            }
        }

        static void Postfix(ref int __result)
        {
            if (Multiplayer.Client == null) return;

            if (CurrentBlock == null)
            {
                //__result = -1;
                Log.Warning("Tried to get a unique id without an id block set!");
                return;
            }

            __result = CurrentBlock.NextId();
            MpLog.Log("got new id " + __result);

            if (currentBlock.current > currentBlock.blockSize * 0.95f && !currentBlock.overflowHandled)
            {
                Multiplayer.Client.Send(Packets.CLIENT_ID_BLOCK_REQUEST, CurrentBlock.mapId);
                currentBlock.overflowHandled = true;
            }
        }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility))]
    [HarmonyPatch(nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
    public static class AddAndRemoveCompsPatch
    {
        static void Prefix(Pawn pawn, ref Container<Map> __state)
        {
            if (Multiplayer.Client == null || pawn.Faction == null) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Postfix(Pawn pawn, Container<Map> __state)
        {
            if (__state != null)
                __state.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Building))]
    [HarmonyPatch(nameof(Building.GetGizmos))]
    public static class GetGizmos
    {
        static void Postfix(Building __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = __result.Concat(new Command_Action
            {
                defaultLabel = "Set faction",
                action = () =>
                {
                    Find.WindowStack.Add(new Dialog_String(s =>
                    {
                        //Type t = typeof(WindowStack).Assembly.GetType("Verse.DataAnalysisTableMaker", true);
                        //MethodInfo m = t.GetMethod(s, BindingFlags.Public | BindingFlags.Static);
                        //m.Invoke(null, new object[0]);
                    }));

                    //__instance.SetFaction(Faction.OfSpacerHostile);
                }
            });
        }
    }

    [HarmonyPatch(typeof(Pawn))]
    [HarmonyPatch(nameof(Pawn.GetGizmos))]
    public static class PawnGizmos
    {
        static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = __result.Concat(new Command_Action
            {
                defaultLabel = "Thinker",
                action = () =>
                {
                    Dialog_BillConfig dialog = Find.WindowStack.WindowOfType<Dialog_BillConfig>();
                    if (dialog != null)
                        dialog.bill.repeatCount++;

                    //Find.WindowStack.Add(new ThinkTreeWindow(__instance));
                    // Log.Message("" + Multiplayer.mainBlock.blockStart);
                    // Log.Message("" + __instance.Map.GetComponent<MultiplayerMapComp>().encounterIdBlock.current);
                    //Log.Message("" + __instance.Map.GetComponent<MultiplayerMapComp>().encounterIdBlock.GetHashCode());
                }
            });
        }
    }

    [HarmonyPatch]
    public static class WidgetsResolveParsePatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Widgets), "ResolveParseNow").MakeGenericMethod(typeof(int));
        }

        // Fix input field handling
        static void Prefix(bool force, ref int val, ref string buffer, ref string edited)
        {
            if (force)
                edited = Widgets.ToStringTypedIn(val);
        }
    }

    [HarmonyPatch(typeof(Dialog_BillConfig))]
    [HarmonyPatch(new[] { typeof(Bill_Production), typeof(IntVec3) })]
    public static class DialogPatch
    {
        static void Postfix(Dialog_BillConfig __instance)
        {
            __instance.absorbInputAroundWindow = false;
        }
    }

    public class Dialog_String : Dialog_Rename
    {
        private Action<string> action;

        public Dialog_String(Action<string> action)
        {
            this.action = action;
        }

        public override void SetName(string name)
        {
            action(name);
        }
    }

    [HarmonyPatch(typeof(WorldObject))]
    [HarmonyPatch(nameof(WorldObject.GetGizmos))]
    public static class WorldObjectGizmos
    {
        static void Postfix(WorldObject __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = __result.Concat(new Command_Action
            {
                defaultLabel = "Jump to",
                action = () =>
                {
                    /*if (__instance is Caravan c)
                    {
                        foreach (Pawn p in c.pawns)
                        {
                            Log.Message(p + " " + p.Spawned);

                            foreach (Thing t in p.inventory.innerContainer)
                                Log.Message(t + " " + t.Spawned);

                            foreach (Thing t in p.equipment.AllEquipmentListForReading)
                                Log.Message(t + " " + t.Spawned);

                            foreach (Thing t in p.apparel.GetDirectlyHeldThings())
                                Log.Message(t + " " + t.Spawned);
                        }
                    }*/

                    Find.WindowStack.Add(new Dialog_JumpTo(s =>
                    {
                        int i = int.Parse(s);
                        Find.WorldCameraDriver.JumpTo(i);
                        Find.WorldSelector.selectedTile = i;
                    }));
                }
            });
        }
    }

    [HarmonyPatch(typeof(ListerHaulables))]
    [HarmonyPatch(nameof(ListerHaulables.ListerHaulablesTick))]
    public static class HaulablesTickPatch
    {
        static bool Prefix() => Multiplayer.Client == null || MultiplayerMapComp.tickingFactions;
    }

    [HarmonyPatch(typeof(ResourceCounter))]
    [HarmonyPatch(nameof(ResourceCounter.ResourceCounterTick))]
    public static class ResourcesTickPatch
    {
        static bool Prefix() => Multiplayer.Client == null || MultiplayerMapComp.tickingFactions;
    }

    [HarmonyPatch(typeof(WindowStack))]
    [HarmonyPatch(nameof(WindowStack.WindowsForcePause), PropertyMethod.Getter)]
    public static class WindowsPausePatch
    {
        static void Postfix(ref bool __result)
        {
            if (Multiplayer.Client != null)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(Plant))]
    [HarmonyPatch(nameof(Plant.TickLong))]
    public static class PlantTickLong
    {
        static void Prefix()
        {
            Rand.PushState();
        }

        static void Postfix()
        {
            Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(RandomNumberGenerator_BasicHash))]
    [HarmonyPatch(nameof(RandomNumberGenerator_BasicHash.GetInt))]
    public static class RandPatch
    {
        public static int call;
        private static bool dontLog;

        public static string current;

        static void Prefix()
        {
            if (RandPatches.Ignore || dontLog || Multiplayer.Client == null) return;
            if (Current.ProgramState != ProgramState.Playing && !Multiplayer.reloading) return;

            dontLog = true;

            if (MapAsyncTimeComp.tickingMap != null && false)
            {
                call++;

                if (ThingContext.Current == null || !(ThingContext.Current is Plant || ThingContext.Current.def == ThingDefOf.SteamGeyser))
                    if (!(WildAnimalSpawnerTickMarker.ticking || SteadyEnvironmentEffectsTickMarker.ticking) || (Find.TickManager.TicksGame > 9670 && Find.TickManager.TicksGame < 9690))
                        MpLog.Log(call + " thing rand " + ThingContext.Current + " " + Rand.Int);
            }

            if (ThingContext.Current != null && !(ThingContext.Current is Plant) && !(ThingContext.Current.def == ThingDefOf.SteamGeyser))
            {
                //MpLog.Log((call++) + " thing rand " + ThingContext.Current + " " + Rand.Int);
            }
            else if (!current.NullOrEmpty())
            {
                //Log.Message(Find.TickManager.TicksGame + " " + Multiplayer.username + " " + (call++) + " rand call " + current + " " + Rand.Int);
            }
            else if (Multiplayer.reloading)
            {
                //Log.Message(Find.TickManager.TicksGame + " " + Multiplayer.username + " " + (call++) + " rand encounter " + Rand.Int);
            }

            dontLog = false;
        }
    }

    [HarmonyPatch(typeof(Rand))]
    [HarmonyPatch(nameof(Rand.Seed), PropertyMethod.Setter)]
    public static class RandSetSeedPatch
    {
        public static bool dontLog;

        static void Prefix()
        {
            if (dontLog) return;
            //if (MapAsyncTimeComp.tickingMap != null)
            //MpLog.Log("set seed");
        }
    }

    [HarmonyPatch(typeof(AutoBuildRoofAreaSetter))]
    [HarmonyPatch(nameof(AutoBuildRoofAreaSetter.TryGenerateAreaNow))]
    public static class AutoRoofPatch
    {
        static bool Prefix(AutoBuildRoofAreaSetter __instance, Room room, ref Map __state)
        {
            if (Multiplayer.Client == null) return true;
            if (room.Dereferenced || room.TouchesMapEdge || room.RegionCount > 26 || room.CellCount > 320 || room.RegionType == RegionType.Portal) return false;

            Map map = room.Map;
            Faction faction = null;

            foreach (IntVec3 cell in room.BorderCells)
            {
                Thing holder = cell.GetRoofHolderOrImpassable(map);
                if (holder == null || holder.Faction == null) continue;
                if (faction != null && holder.Faction != faction) return false;
                faction = holder.Faction;
            }

            if (faction == null) return false;

            map.PushFaction(faction);
            __state = map;

            return true;
        }

        static void Postfix(ref Map __state)
        {
            if (__state != null)
                __state.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_DrawTracker))]
    [HarmonyPatch(nameof(Pawn_DrawTracker.DrawPos), PropertyMethod.Getter)]
    static class DrawPosPatch
    {
        // Give the root position when ticking
        static void Postfix(Pawn_DrawTracker __instance, ref Vector3 __result)
        {
            if (Multiplayer.Client == null || Multiplayer.ShouldSync) return;
            __result = __result - __instance.tweener.TweenedPos + __instance.tweener.TweenedPosRoot();
        }
    }

    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch(nameof(Thing.ExposeData))]
    public static class PawnExposeDataFirst
    {
        public static Container<Map> state;

        // Postfix so Thing's faction is already loaded
        static void Postfix(Thing __instance)
        {
            if (!(__instance is Pawn)) return;
            if (Multiplayer.Client == null || __instance.Faction == null || Find.FactionManager == null || Find.FactionManager.AllFactions.Count() == 0) return;

            ThingContext.Push(__instance);
            state = __instance.Map;
            __instance.Map.PushFaction(__instance.Faction);
        }
    }

    [HarmonyPatch(typeof(Pawn))]
    [HarmonyPatch(nameof(Pawn.ExposeData))]
    public static class PawnExposeDataLast
    {
        static void Postfix()
        {
            if (PawnExposeDataFirst.state != null)
            {
                PawnExposeDataFirst.state.PopFaction();
                ThingContext.Pop();
                PawnExposeDataFirst.state = null;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_NeedsTracker))]
    [HarmonyPatch(nameof(Pawn_NeedsTracker.AddOrRemoveNeedsAsAppropriate))]
    public static class AddRemoveNeeds
    {
        static void Prefix(Pawn_NeedsTracker __instance)
        {
            //MpLog.Log("add remove needs {0} {1}", FactionContext.OfPlayer.ToString(), __instance.GetPropertyOrField("pawn"));
        }
    }

    [HarmonyPatch(typeof(PawnTweener))]
    [HarmonyPatch(nameof(PawnTweener.PreDrawPosCalculation))]
    public static class PreDrawPosCalcPatch
    {
        static void Prefix()
        {
            //if (MapAsyncTimeComp.tickingMap != null)
            //    SimpleProfiler.Pause();
        }

        static void Postfix()
        {
            //if (MapAsyncTimeComp.tickingMap != null)
            //    SimpleProfiler.Start();
        }
    }

    [HarmonyPatch(typeof(TickManager))]
    [HarmonyPatch(nameof(TickManager.TickRateMultiplier), PropertyMethod.Getter)]
    public static class TickRatePatch
    {
        static bool Prefix(TickManager __instance, ref float __result)
        {
            if (Multiplayer.Client == null) return true;

            if (__instance.CurTimeSpeed == TimeSpeed.Paused)
                __result = 0;
            else if (__instance.slower.ForcedNormalSpeed)
                __result = 1;
            else if (__instance.CurTimeSpeed == TimeSpeed.Fast)
                __result = 3;
            else if (__instance.CurTimeSpeed == TimeSpeed.Superfast)
                __result = 6;
            else
                __result = 1;

            return false;
        }
    }

    public static class ValueSavePatch
    {
        public static bool DoubleSave_Prefix(string label, ref double value)
        {
            if (Scribe.mode != LoadSaveMode.Saving) return true;
            Scribe.saver.WriteElement(label, value.ToString("G17"));
            return false;
        }

        public static bool FloatSave_Prefix(string label, ref float value)
        {
            if (Scribe.mode != LoadSaveMode.Saving) return true;
            Scribe.saver.WriteElement(label, value.ToString("G9"));
            return false;
        }
    }

    [HarmonyPatch(typeof(Log))]
    [HarmonyPatch(nameof(Log.Warning))]
    public static class CrossRefWarningPatch
    {
        private static Regex regex = new Regex(@"^Could not resolve reference to object with loadID ([\w.-]*) of type ([\w.<>+]*)\. Was it compressed away");
        public static bool ignore;

        // The only non-generic entry point during cross reference resolving
        static bool Prefix(string text)
        {
            if (Multiplayer.Client == null || ignore) return true;

            ignore = true;

            GroupCollection groups = regex.Match(text).Groups;
            if (groups.Count == 3)
            {
                string loadId = groups[1].Value;
                string typeName = groups[2].Value;
                // todo
                return false;
            }

            ignore = false;

            return true;
        }
    }

    [HarmonyPatch(typeof(UI))]
    [HarmonyPatch(nameof(UI.MouseCell))]
    public static class MouseCellPatch
    {
        public static IntVec3? result;

        static void Postfix(ref IntVec3 __result)
        {
            if (result.HasValue)
                __result = result.Value;
        }
    }

    [HarmonyPatch(typeof(KeyBindingDef))]
    [HarmonyPatch(nameof(KeyBindingDef.IsDownEvent), PropertyMethod.Getter)]
    public static class KeyIsDownPatch
    {
        public static bool? result;
        public static KeyBindingDef forKey;

        static bool Prefix(KeyBindingDef __instance) => !(__instance == forKey && result.HasValue);

        static void Postfix(KeyBindingDef __instance, ref bool __result)
        {
            if (__instance == forKey && result.HasValue)
                __result = result.Value;
        }
    }

    [HarmonyPatch(typeof(ITab))]
    [HarmonyPatch(nameof(ITab.SelThing), PropertyMethod.Getter)]
    public static class ITabSelThingPatch
    {
        public static Thing result;

        static void Postfix(ref Thing __result)
        {
            if (result != null)
                __result = result;
        }
    }

    // Fix window focus
    [HarmonyPatch(typeof(WindowStack))]
    [HarmonyPatch(nameof(WindowStack.CloseWindowsBecauseClicked))]
    public static class WindowFocusPatch
    {
        static void Prefix(Window clickedWindow)
        {
            for (int i = Find.WindowStack.Windows.Count - 1; i >= 0; i--)
            {
                Window window = Find.WindowStack.Windows[i];
                if (window == clickedWindow || window.closeOnClickedOutside) break;
                UI.UnfocusCurrentControl();
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    static class PawnSpawnSetupMarker
    {
        public static bool respawningAfterLoad;

        static void Prefix(bool respawningAfterLoad)
        {
            PawnSpawnSetupMarker.respawningAfterLoad = respawningAfterLoad;
        }

        static void Postfix()
        {
            respawningAfterLoad = false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.ResetToCurrentPosition))]
    static class PatherResetPatch
    {
        static bool Prefix() => !PawnSpawnSetupMarker.respawningAfterLoad;
    }

    // Seed the rotation random
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool) })]
    static class GenSpawnRotatePatch
    {
        static MethodInfo Rot4GetRandom = AccessTools.Property(typeof(Rot4), nameof(Rot4.Random)).GetGetMethod();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (CodeInstruction inst in insts)
            {
                if (inst.operand == Rot4GetRandom)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber)));
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Rand), nameof(Rand.PushState), new[] { typeof(int) }));
                }

                yield return inst;

                if (inst.operand == Rot4GetRandom)
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Rand), nameof(Rand.PopState)));
            }
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished))]
    static class ExecuteWhenFinishedPatch
    {
        static bool Prefix(Action action)
        {
            if (!Multiplayer.simulating) return true;
            action();
            return false;
        }
    }

    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay))]
    static class SetupQuickTestPatch
    {
        public static bool marker;

        static void Prefix() => marker = true;

        static void Postfix()
        {
            Find.GameInitData.mapSize = 250;
            marker = false;
        }
    }

    [HarmonyPatch(typeof(GameInitData), nameof(GameInitData.ChooseRandomStartingTile))]
    static class RandomStartingTilePatch
    {
        static void Postfix()
        {
            if (SetupQuickTestPatch.marker)
            {
                Find.GameInitData.startingTile = 501;
                Find.WorldGrid[Find.GameInitData.startingTile].hilliness = Hilliness.SmallHills;
            }
        }
    }

    [HarmonyPatch(typeof(GenText), nameof(GenText.RandomSeedString))]
    static class GrammarRandomStringPatch
    {
        static void Postfix(ref string __result)
        {
            if (SetupQuickTestPatch.marker)
                __result = "multiplayer";
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), "<SortWornApparelIntoDrawOrder>m__0")]
    static class FixApparelSort
    {
        static void Postfix(Apparel a, Apparel b, ref int __result)
        {
            if (__result == 0)
                __result = a.thingIDNumber.CompareTo(b.thingIDNumber);
        }
    }

    [MpPatch(typeof(OutfitDatabase), nameof(OutfitDatabase.GenerateStartingOutfits))]
    [MpPatch(typeof(DrugPolicyDatabase), nameof(DrugPolicyDatabase.GenerateStartingDrugPolicies))]
    static class CancelReinitializationDuringLoading
    {
        static bool Prefix() => Scribe.mode != LoadSaveMode.LoadingVars;
    }

    [HarmonyPatch(typeof(OutfitDatabase), nameof(OutfitDatabase.MakeNewOutfit))]
    static class OutfitUniqueIdPatch
    {
        static void Postfix(Outfit __result)
        {
            if (Multiplayer.Ticking || OnMainThread.executingCmds)
                __result.uniqueId = Multiplayer.GlobalIdBlock.NextId();
        }
    }

    [HarmonyPatch(typeof(DrugPolicyDatabase), nameof(DrugPolicyDatabase.MakeNewDrugPolicy))]
    static class DrugPolicyUniqueIdPatch
    {
        static void Postfix(DrugPolicy __result)
        {
            if (Multiplayer.Ticking || OnMainThread.executingCmds)
                __result.uniqueId = Multiplayer.GlobalIdBlock.NextId();
        }
    }

    [HarmonyPatch(typeof(ListerFilthInHomeArea), nameof(ListerFilthInHomeArea.RebuildAll))]
    static class ListerFilthRebuildPatch
    {
        static bool ignore;

        static void Prefix(ListerFilthInHomeArea __instance)
        {
            if (ignore) return;
            ignore = true;
            foreach (FactionMapData data in __instance.map.MpComp().factionMapData.Values)
            {
                __instance.map.PushFaction(data.factionId);
                data.listerFilthInHomeArea.RebuildAll();
                __instance.map.PopFaction();
            }
            ignore = false;
        }
    }

    [HarmonyPatch(typeof(ListerFilthInHomeArea), nameof(ListerFilthInHomeArea.Notify_FilthSpawned))]
    static class ListerFilthSpawnedPatch
    {
        static bool ignore;

        static void Prefix(ListerFilthInHomeArea __instance, Filth f)
        {
            if (ignore) return;
            ignore = true;
            foreach (FactionMapData data in __instance.map.MpComp().factionMapData.Values)
            {
                __instance.map.PushFaction(data.factionId);
                data.listerFilthInHomeArea.Notify_FilthSpawned(f);
                __instance.map.PopFaction();
            }
            ignore = false;
        }
    }

    [HarmonyPatch(typeof(ListerFilthInHomeArea), nameof(ListerFilthInHomeArea.Notify_FilthDespawned))]
    static class ListerFilthDespawnedPatch
    {
        static bool ignore;

        static void Prefix(ListerFilthInHomeArea __instance, Filth f)
        {
            if (ignore) return;
            ignore = true;
            foreach (FactionMapData data in __instance.map.MpComp().factionMapData.Values)
            {
                __instance.map.PushFaction(data.factionId);
                data.listerFilthInHomeArea.Notify_FilthDespawned(f);
                __instance.map.PopFaction();
            }
            ignore = false;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    static class LoadGamePatch
    {
        public static bool loading;

        static void Prefix() => loading = true;
        static void Postfix() => loading = false;
    }

    [HarmonyPatch(typeof(Game), nameof(Game.ExposeSmallComponents))]
    static class GameExposeComponentsPatch
    {
        static void Prefix()
        {
            if (Multiplayer.Client == null || Scribe.mode != LoadSaveMode.LoadingVars) return;
            Multiplayer.game = new MultiplayerGame();
        }
    }

    [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
    static class ClearAllPatch
    {
        static void Postfix()
        {
            Multiplayer.game = null;
        }
    }

    [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.RecacheFactions))]
    static class RecacheFactionsPatch
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null) return;
            Multiplayer.game.dummyFaction = Find.FactionManager.allFactions.FirstOrDefault(f => f.loadID == -1);
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.ExposeComponents))]
    static class WorldExposeComponentsPatch
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null) return;
            Multiplayer.game.worldComp = Find.World.GetComponent<MultiplayerWorldComp>();
        }
    }


}