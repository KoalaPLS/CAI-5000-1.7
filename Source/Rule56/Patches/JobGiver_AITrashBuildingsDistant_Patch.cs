using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using static CombatAI.SightTracker;

namespace CombatAI.Patches
{
    public static class JobGiver_AITrashBuildingsDistant_Patch
    {
        private static readonly Dictionary<int, int> lastGaveById = new Dictionary<int, int>();

        public static void ClearCache()
        {
            lastGaveById.Clear();
        }

        [HarmonyPatch(typeof(JobGiver_AITrashBuildingsDistant), "TryGiveJob")]
        private static class JobGiver_AITrashBuildingsDistant_TryGiveJob_Patch
        {
            public static bool Prefix(Pawn pawn, ref Job __result)
            {
                if (pawn == null) return true;

                // CAI normally suppresses distant-building jobs while the pawn is exposed to
                // enemies. That is sensible for ordinary tactical movement, but it used to erase
                // the only destructive fallback exactly at a defended choke. A committed strategic
                // breach is allowed to survive that visibility gate.
                if (RaidPenetrationPlanner.TryMaintainBreach(pawn, out Job penetrationJob))
                {
                    __result = penetrationJob;
                    return false;
                }

                if (lastGaveById.TryGetValue(pawn.thingIDNumber, out int ticks) && GenTicks.TicksGame - ticks < 30)
                {
                    return false;
                }
                try
                {
                    if (pawn.TryGetSightReader(out SightReader reader) && reader.GetVisibilityToEnemies(pawn.Position) > 0)
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(Pawn pawn, Job __result)
            {
                if (pawn == null) return;
                if (__result != null)
                {
                    lastGaveById[pawn.thingIDNumber] = GenTicks.TicksGame;
                }
                else
                {
                    lastGaveById[pawn.thingIDNumber] = GenTicks.TicksGame + 20;
                }
            }
        }
    }
}
