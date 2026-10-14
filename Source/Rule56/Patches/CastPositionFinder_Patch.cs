using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatAI.Comps;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using static CombatAI.SightTracker;

namespace CombatAI.Patches
{
    public static class Harmony_CastPositionFinder
    {
        private static bool                             ai_fightEnemies;
        private static Verb                             verb;
        private static Pawn                             pawn;
        private static Thing                            target;
        private static Map                              map;
        private static bool                             skipped;
        private static IntVec3                          targetPosition;
        private static IntVec3                          dutyDest;
        private static float                            warmupTime;
        private static float                            range;
        private static CastPositionRequest              request;
        private static ISGrid<float>                    grid;
        private static InterceptorTracker               interceptors;
        private static AvoidanceTracker                 avoidanceTracker;
        private static AvoidanceTracker.AvoidanceReader avoidanceReader;
        private static SightReader                      sightReader;

        [HarmonyPatch(typeof(JobGiver_AIFightEnemies), "TryFindShootingPosition")]
        public static class JobGiver_AIFightEnemies_TryFindShootingPosition_Patch
        {
            public static void Prefix()
            {
                ai_fightEnemies = true;
            }
        }

        [HarmonyPatch(typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition))]
        public static class CastPositionFinder_TryFindCastPosition_Patch
        {
            private static readonly FieldInfo  fBestSpot = AccessTools.Field(typeof(CastPositionFinder), "bestSpot");
            private static readonly MethodInfo mWholeMap = AccessTools.Method(typeof(CellRect), nameof(CellRect.WholeMap), new[] { typeof(Map) });
            private static readonly MethodInfo mFloodCellRect = AccessTools.Method(typeof(CastPositionFinder_TryFindCastPosition_Patch), nameof(FloodCellRect));

            // Production deliberately fails open if a future game/modded IL shape drifts.
            // The standalone 1.7 audit flips this on so an unverified anchor is a hard
            // failure rather than a log line followed by a misleading green result.
            public static bool RequireExactAuditAnchors { get; set; }

            public static void Prefix(ref CastPositionRequest newReq)
            {
                if (ai_fightEnemies)
                {
                    ai_fightEnemies           = false;
                    newReq.maxRangeFromTarget = 0;
                }
                skipped = true;
                if (newReq.caster != null && Finder.Settings.Caster_Enabled && newReq.target != null && (newReq.maxRangeFromTarget == 0 || newReq.maxRangeFromTarget * newReq.maxRangeFromTarget > newReq.caster.Position.DistanceToSquared(newReq.target.Position)) && newReq.maxRangeFromLocus == 0)
                {
                    if ((pawn = newReq.caster) != null && !(pawn.RaceProps?.Animal ?? true) && (pawn.mindState?.duty == null || pawn.mindState.duty.def != DutyDefOf.Sapper && pawn.mindState.duty.def != DutyDefOf.Breaching))
                    {
                        if (pawn.Faction.IsPlayerSafe() && pawn.AI()?.forcedTarget.IsValid == false)
                        {
                            goto skip;
                        }
                        map              = newReq.caster?.Map;
                        verb             = newReq.verb;
                        range            = verb.EffectiveRange;
                        map              = pawn.Map;
                        avoidanceTracker = pawn.Map.GetComp_Fast<AvoidanceTracker>();
                        avoidanceTracker.TryGetReader(pawn, out avoidanceReader);
                        dutyDest = pawn.TryGetNextDutyDest(newReq.maxRangeFromCaster);
                        if (avoidanceReader != null)
                        {
                            grid = map.GetFloatGrid();
                            grid.Reset();
                            newReq.caster.TryGetSightReader(out sightReader);
                            if (sightReader != null)
                            {
                                sightReader.armor          = pawn.GetArmorReport();
                                interceptors               = map.GetComp_Fast<MapComponent_CombatAI>().interceptors;
                                warmupTime                 = verb?.verbProps.warmupTime ?? 1;
                                warmupTime                 = Mathf.Clamp(warmupTime, 0.5f, 0.8f);
                                target                     = newReq.target;
                                targetPosition             = target.Position;
                                newReq.wantCoverFromTarget = true;
                                request                    = newReq;
                                skipped                    = false;
                                return;
                            }
                        }
                    }
                }
            skip:
                pawn             = null;
                grid             = null;
                avoidanceTracker = null;
                avoidanceReader  = null;
                sightReader      = null;
                verb             = null;
                target           = null;
                map              = null;
                skipped          = true;
            }

            public static void Postfix(IntVec3 dest, bool __result)
            {
                if (__result && avoidanceTracker != null)
                {
                    avoidanceTracker.Notify_CoverPositionSelected(pawn, dest);
                }
                if (grid != null)
                {
                    grid.Reset();
                }
                skipped          = true;
                grid             = null;
                interceptors     = null;
                avoidanceTracker = null;
                avoidanceReader  = null;
                sightReader      = null;
                pawn             = null;
                verb             = null;
                target           = null;
                map              = null;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                List<CodeInstruction> codes = instructions.ToList();
                if (mWholeMap == null || fBestSpot == null || mFloodCellRect == null)
                {
                    return TranspilerAnchorFailure(
                        "ISMA: CastPositionFinder 1.7 transpiler prerequisites were not found; tactical cell weighting was left unpatched.",
                        codes);
                }

                int cellRectLocal = -1;
                int wholeMapCall = -1;
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].Calls(mWholeMap))
                    {
                        wholeMapCall = i;
                        break;
                    }
                }

                if (wholeMapCall >= 0)
                {
                    for (int i = wholeMapCall + 1; i < codes.Count; i++)
                    {
                        if (TryGetStoredLocalIndex(codes[i], out cellRectLocal))
                        {
                            break;
                        }
                    }
                }

                if (cellRectLocal < 0)
                {
                    return TranspilerAnchorFailure(
                        "ISMA: CastPositionFinder 1.7 transpiler could not identify the CellRect local after CellRect.WholeMap; tactical cell weighting was left unpatched.",
                        codes);
                }

                int insertAt = -1;
                for (int i = wholeMapCall + 1; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Stsfld && codes[i].OperandIs(fBestSpot))
                    {
                        insertAt = i + 1;
                        break;
                    }
                }

                if (insertAt < 0)
                {
                    return TranspilerAnchorFailure(
                        "ISMA: CastPositionFinder 1.7 transpiler could not find the bestSpot reset after CellRect clipping; tactical cell weighting was left unpatched.",
                        codes);
                }

                // Current 1.7 constructs and clips the search CellRect before resetting
                // bestSpot. Inject immediately after that reset, when the evaluation stack
                // is empty and the final clipped rectangle is available.
                codes.Insert(insertAt, new CodeInstruction(OpCodes.Ldloc, cellRectLocal));
                codes.Insert(insertAt + 1, new CodeInstruction(OpCodes.Call, mFloodCellRect));
                return codes;
            }

            private static IEnumerable<CodeInstruction> TranspilerAnchorFailure(string message, List<CodeInstruction> codes)
            {
                if (RequireExactAuditAnchors)
                {
                    throw new InvalidOperationException(message);
                }

                Log.Error(message);
                return codes;
            }

            private static bool TryGetStoredLocalIndex(CodeInstruction instruction, out int localIndex)
            {
                localIndex = -1;
                OpCode op = instruction.opcode;
                if (op == OpCodes.Stloc_0) { localIndex = 0; return true; }
                if (op == OpCodes.Stloc_1) { localIndex = 1; return true; }
                if (op == OpCodes.Stloc_2) { localIndex = 2; return true; }
                if (op == OpCodes.Stloc_3) { localIndex = 3; return true; }
                if (op != OpCodes.Stloc && op != OpCodes.Stloc_S)
                {
                    return false;
                }

                if (instruction.operand is LocalBuilder builder)
                {
                    localIndex = builder.LocalIndex;
                    return true;
                }
                if (instruction.operand is int index)
                {
                    localIndex = index;
                    return true;
                }
                if (instruction.operand is short shortIndex)
                {
                    localIndex = shortIndex;
                    return true;
                }
                if (instruction.operand is byte byteIndex)
                {
                    localIndex = byteIndex;
                    return true;
                }
                return false;
            }

            private static void FloodCellRect(CellRect rect)
            {
                if (!skipped && sightReader != null)
                {
                    IntVec3 root             = pawn.Position;
                    float   rootVis          = sightReader.GetVisibilityToEnemies(root);
                    float   rootThreat       = sightReader.GetThreat(request.locus);
                    float   effectiveRange   = verb?.EffectiveRange > 0 ? verb.EffectiveRange : -1;
                    float   rootDutyDestDist = dutyDest.IsValid ? dutyDest.DistanceTo(pawn.Position) : -1;
                    map.GetCellFlooder().Flood(root,
                                               node =>
                                               {
                                                   float val = (node.dist - node.distAbs) / (node.distAbs + 1f) * 2 + (sightReader.GetVisibilityToEnemies(node.cell) - rootVis) * 0.5f + Maths.Min(avoidanceReader.GetProximity(node.cell), 2f) + Maths.Min(avoidanceReader.GetDanger(node.cell), 1f) - interceptors.grid.Get(node.cell) * 4 + (sightReader.GetThreat(node.cell) - rootThreat) * 0.5f;
                                                   if (rootDutyDestDist > 0)
                                                   {
                                                       val += Mathf.Clamp((Maths.Sqrt_Fast(dutyDest.DistanceToSquared(node.cell), 3) - rootDutyDestDist) * 0.25f, -0.5f, 0.5f);
                                                   }
                                                   if (effectiveRange > 0)
                                                   {
                                                       val += 2f * Mathf.Abs(effectiveRange - Maths.Sqrt_Fast(node.cell.DistanceToSquared(targetPosition), 5)) / effectiveRange;
                                                   }
                                                   grid[node.cell] = val;
                                               },
                                               cell =>
                                               {
	                                               return (sightReader.GetVisibilityToEnemies(cell) - rootVis) * 2.5f - interceptors.grid.Get(cell) + (sightReader.GetThreat(cell) - rootThreat) * 0.25f;
                                               },
                                               cell =>
                                               {
                                                   return rect.Contains(cell) && cell.WalkableBy(map, pawn) && map.reservationManager.CanReserve(pawn, cell);
                                               }
                                               , Maths.Max(rect.Height, rect.Width) * 2);
                }
            }
        }

        [HarmonyPatch(typeof(CastPositionFinder), "CastPositionPreference")]
        public static class CastPositionFinder_CastPositionPreference_Patch
        {
            public static void Postfix(IntVec3 c, ref float __result)
            {
                if (!skipped)
                {
                    if (__result == -1)
                    {
                        return;
                    }
                    if (sightReader != null)
                    {
                        if (!grid.IsSet(c))
                        {
                            __result = -1;
                        }
                        else
                        {
                            __result = __result - grid[c] * Finder.P50 + 1.5f;
                        }
                    }
                }
            }
        }
    }
}
