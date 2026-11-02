using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using CombatAI.Compatibility;

namespace CombatAI
{
    /// <summary>
    /// Strategic ingress layer for hostile colony assaults.
    ///
    /// CAI's tactical layer remains in charge of shooting, cover and local movement. This planner
    /// only intervenes after an assault pawn repeatedly fails to make net progress through a
    /// closed-door route. It evaluates a small set of spatially distinct player targets with
    /// PassAllDestroyableThings and commits the pawn to the first player-owned blocker on the best
    /// lane. Peer claims are penalized so raids can open multiple ingress points instead of forming
    /// one permanent door queue.
    /// </summary>
    public static class RaidPenetrationPlanner
    {
        private const int SampleIntervalTicks = 120;
        private const int StallSamplesBeforePlan = 2;
        private const int PlanCooldownTicks = 180;
        private const int CommitTicks = 1200;
        private const int NoDamageReplanTicks = 720;
        private const int RejectBlockerTicks = 1200;
        private const int MaxCandidateTargets = 8;
        private const int MaxPotentialTargetsScanned = 96;

        private sealed class PawnState
        {
            public IntVec3 progressAnchor = IntVec3.Invalid;
            public int lastSampleTick = -1;
            public int stalledSamples;
            public int nextPlanTick;

            public Thing blocker;
            public IntVec3 approachCell = IntVec3.Invalid;
            public int blockerHitPoints = -1;
            public int lastDamageTick = -1;
            public int commitUntilTick = -1;

            public readonly Dictionary<int, int> rejectedBlockers = new Dictionary<int, int>();
        }

        private struct Lane
        {
            public Thing target;
            public Thing blocker;
            public IntVec3 approachCell;
            public float score;
            public float exposure;
            public int peerClaims;
            public int pathNodes;
        }

        private static readonly Dictionary<int, PawnState> states = new Dictionary<int, PawnState>();
        private static readonly List<Thing> candidateBuffer = new List<Thing>(MaxCandidateTargets);
        private static readonly List<IAttackTarget> potentialBuffer = new List<IAttackTarget>(128);

        public static void ClearCache()
        {
            states.Clear();
            candidateBuffer.Clear();
            potentialBuffer.Clear();
        }

        public static bool TryOverrideDoorBoundGoto(Pawn pawn, Thing preferredTarget, out Job job)
        {
            job = null;
            if (!Eligible(pawn) || preferredTarget == null || preferredTarget.Destroyed || !preferredTarget.Spawned)
            {
                Forget(pawn);
                return false;
            }

            PawnState state = GetState(pawn);
            int now = GenTicks.TicksGame;
            CleanupRejected(state, now);

            if (TryMaintainCommittedBreach(pawn, state, now, out job))
            {
                return true;
            }

            // If the target is reachable without traversing a closed door, the ordinary tactical
            // route is making sense and this strategic fallback has nothing to do.
            bool closedDoorTopology;
            try
            {
                closedDoorTopology = !pawn.CanReach(
                    preferredTarget,
                    PathEndMode.InteractionCell,
                    Danger.Deadly,
                    false,
                    false,
                    TraverseMode.NoPassClosedDoors);
            }
            catch
            {
                closedDoorTopology = false;
            }

            if (!closedDoorTopology)
            {
                ResetStallObservation(state, pawn.Position, now);
                return false;
            }

            ObserveProgress(state, pawn.Position, now);
            if (state.stalledSamples < StallSamplesBeforePlan || now < state.nextPlanTick)
            {
                return false;
            }

            state.nextPlanTick = now + PlanCooldownTicks;
            if (!TryPlanLane(pawn, preferredTarget, state, now, out Lane lane))
            {
                return false;
            }

            Commit(state, lane, now);
            job = MakeBreachJob(pawn, lane.blocker, lane.approachCell);
            if (job == null)
            {
                RejectCurrentBlocker(state, now, "job-null");
                return false;
            }

            Log.Message(
                "[CAI][RaidPenetrationV0.1] COMMIT pawn=" + pawn.LabelShortCap +
                " blocker=" + DescribeThing(lane.blocker) +
                " target=" + DescribeThing(lane.target) +
                " score=" + lane.score.ToString("0.0") +
                " exposure=" + lane.exposure.ToString("0.00") +
                " peerClaims=" + lane.peerClaims +
                " pathNodes=" + lane.pathNodes +
                " stalledSamples=" + state.stalledSamples + ".");
            return true;
        }

        /// <summary>
        /// Lets the distant-building job giver continue an already committed penetration lane.
        /// It never starts a fresh lane by itself; nearest-hostile observation owns the anti-loop trigger.
        /// </summary>
        public static bool TryMaintainBreach(Pawn pawn, out Job job)
        {
            job = null;
            if (!Eligible(pawn))
            {
                return false;
            }
            if (!states.TryGetValue(pawn.thingIDNumber, out PawnState state))
            {
                return false;
            }
            return TryMaintainCommittedBreach(pawn, state, GenTicks.TicksGame, out job);
        }

        private static bool Eligible(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null || !pawn.Map.IsPlayerHome)
            {
                return false;
            }
            if (pawn.Faction == null || pawn.Faction == Faction.OfPlayer || !pawn.HostileTo(Faction.OfPlayer))
            {
                return false;
            }
            if (pawn.RaceProps == null || pawn.RaceProps.Animal)
            {
                return false;
            }

            DutyDef duty = pawn.mindState?.duty?.def;
            return duty == DutyDefOf.AssaultColony ||
                   duty == DutyDefOf.AssaultThing ||
                   duty == CombatAI_DutyDefOf.CombatAI_AssaultPoint;
        }

        private static PawnState GetState(Pawn pawn)
        {
            if (!states.TryGetValue(pawn.thingIDNumber, out PawnState state))
            {
                state = new PawnState();
                states.Add(pawn.thingIDNumber, state);
            }
            return state;
        }

        private static void Forget(Pawn pawn)
        {
            if (pawn != null)
            {
                states.Remove(pawn.thingIDNumber);
            }
        }

        private static void ResetStallObservation(PawnState state, IntVec3 pos, int now)
        {
            state.progressAnchor = pos;
            state.lastSampleTick = now;
            state.stalledSamples = 0;
        }

        private static void ObserveProgress(PawnState state, IntVec3 pos, int now)
        {
            if (!state.progressAnchor.IsValid || state.lastSampleTick < 0)
            {
                ResetStallObservation(state, pos, now);
                return;
            }
            if (now - state.lastSampleTick < SampleIntervalTicks)
            {
                return;
            }

            // Roughly five cells of net displacement counts as genuine progress. Forward/back
            // oscillation around one choke therefore accumulates stall samples instead.
            if (state.progressAnchor.DistanceToSquared(pos) >= 25)
            {
                state.progressAnchor = pos;
                state.stalledSamples = 0;
            }
            else
            {
                state.stalledSamples++;
            }
            state.lastSampleTick = now;
        }

        private static bool TryMaintainCommittedBreach(Pawn pawn, PawnState state, int now, out Job job)
        {
            job = null;
            Thing blocker = state.blocker;
            if (blocker == null)
            {
                return false;
            }

            if (blocker.Destroyed || !blocker.Spawned || blocker.Map != pawn.Map || blocker.Faction != Faction.OfPlayer)
            {
                ClearCommit(state);
                ResetStallObservation(state, pawn.Position, now);
                return false;
            }

            int hp = blocker.HitPoints;
            if (state.blockerHitPoints < 0 || hp < state.blockerHitPoints)
            {
                state.blockerHitPoints = hp;
                state.lastDamageTick = now;
            }
            if (state.lastDamageTick < 0)
            {
                state.lastDamageTick = now;
            }

            if (now - state.lastDamageTick > NoDamageReplanTicks)
            {
                RejectCurrentBlocker(state, now, "no-damage-progress");
                return false;
            }

            if (now > state.commitUntilTick && now - state.lastDamageTick > SampleIntervalTicks)
            {
                ClearCommit(state);
                return false;
            }

            job = MakeBreachJob(pawn, blocker, state.approachCell);
            if (job == null)
            {
                RejectCurrentBlocker(state, now, "maintain-job-null");
                return false;
            }
            return true;
        }

        private static bool TryPlanLane(Pawn pawn, Thing preferredTarget, PawnState state, int now, out Lane best)
        {
            best = default(Lane);
            bool found = false;
            candidateBuffer.Clear();
            CollectCandidateTargets(pawn, preferredTarget, candidateBuffer);

            for (int i = 0; i < candidateBuffer.Count; i++)
            {
                Thing target = candidateBuffer[i];
                if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map)
                {
                    continue;
                }
                if (!TryEvaluateLane(pawn, target, state, now, out Lane lane))
                {
                    continue;
                }
                if (!found || lane.score < best.score)
                {
                    best = lane;
                    found = true;
                }
            }
            return found;
        }

        private static void CollectCandidateTargets(Pawn pawn, Thing preferredTarget, List<Thing> output)
        {
            AddUnique(output, preferredTarget);
            AddUnique(output, pawn.mindState?.enemyTarget);

            potentialBuffer.Clear();
            try
            {
                List<IAttackTarget> potential = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                if (potential != null)
                {
                    int limit = Mathf.Min(potential.Count, MaxPotentialTargetsScanned);
                    for (int i = 0; i < limit; i++)
                    {
                        IAttackTarget target = potential[i];
                        Thing thing = target?.Thing;
                        if (thing != null && thing.Spawned && !thing.Destroyed && thing.Faction == Faction.OfPlayer && !target.ThreatDisabled(pawn))
                        {
                            potentialBuffer.Add(target);
                        }
                    }
                }
            }
            catch
            {
            }

            if (potentialBuffer.Count == 0)
            {
                return;
            }

            Thing nearest = null;
            Thing farthest = null;
            Thing minX = null;
            Thing maxX = null;
            Thing minZ = null;
            Thing maxZ = null;
            int nearestDist = int.MaxValue;
            int farthestDist = int.MinValue;

            for (int i = 0; i < potentialBuffer.Count; i++)
            {
                Thing thing = potentialBuffer[i].Thing;
                int dist = pawn.Position.DistanceToSquared(thing.Position);
                if (dist < nearestDist) { nearestDist = dist; nearest = thing; }
                if (dist > farthestDist) { farthestDist = dist; farthest = thing; }
                if (minX == null || thing.Position.x < minX.Position.x) minX = thing;
                if (maxX == null || thing.Position.x > maxX.Position.x) maxX = thing;
                if (minZ == null || thing.Position.z < minZ.Position.z) minZ = thing;
                if (maxZ == null || thing.Position.z > maxZ.Position.z) maxZ = thing;
            }

            AddUnique(output, nearest);
            AddUnique(output, farthest);
            AddUnique(output, minX);
            AddUnique(output, maxX);
            AddUnique(output, minZ);
            AddUnique(output, maxZ);

            if (output.Count < MaxCandidateTargets && pawn.mindState?.duty?.focus.HasThing == true)
            {
                AddUnique(output, pawn.mindState.duty.focus.Thing);
            }
        }

        private static void AddUnique(List<Thing> output, Thing thing)
        {
            if (thing == null || output.Count >= MaxCandidateTargets)
            {
                return;
            }
            for (int i = 0; i < output.Count; i++)
            {
                if (output[i] == thing)
                {
                    return;
                }
            }
            output.Add(thing);
        }

        private static bool TryEvaluateLane(Pawn pawn, Thing target, PawnState state, int now, out Lane lane)
        {
            lane = default(Lane);
            PawnPath path = null;
            try
            {
                TraverseParms parms = TraverseParms.For(pawn);
                parms.mode = TraverseMode.PassAllDestroyableThings;
                parms.maxDanger = Danger.Deadly;
                parms.canBashDoors = true;
                parms.canBashFences = true;

                path = pawn.Map.pathFinder.FindPathNow(
                    pawn.Position,
                    new LocalTargetInfo(target.Position),
                    parms);

                if (path == null || !path.Found)
                {
                    return false;
                }

                IList<IntVec3> nodes = path.GetNodes();
                int curIndex = path.GetCurNodeIndex();
                if (nodes == null || nodes.Count == 0 || curIndex <= 0 || curIndex >= nodes.Count)
                {
                    return false;
                }

                Thing blocker = null;
                IntVec3 approachCell = nodes[curIndex];
                int pathNodes = 0;
                IntVec3 previous = approachCell;

                for (int i = curIndex - 1; i >= 0; i--)
                {
                    IntVec3 cell = nodes[i];
                    pathNodes++;
                    Building edifice = cell.GetEdifice(pawn.Map);
                    if (edifice != null && IsPlayerPenetrationBlocker(edifice))
                    {
                        blocker = edifice;
                        approachCell = previous;
                        break;
                    }
                    previous = cell;
                }

                if (blocker == null || !approachCell.IsValid || IsRejected(state, blocker, now))
                {
                    return false;
                }

                float exposure = 0f;
                try
                {
                    if (pawn.TryGetSightReader(out SightTracker.SightReader reader))
                    {
                        exposure = reader.GetAbsVisibilityToEnemies(approachCell);
                    }
                }
                catch
                {
                    exposure = 0f;
                }

                int claims = CountPeerClaims(pawn, blocker, now);
                bool isDoor = blocker is Building_Door;

                float score = pathNodes;
                score += Mathf.Max(blocker.HitPoints, 1) * 0.08f;
                score += isDoor ? 0f : 18f;
                score += Mathf.Min(exposure, 8f * Finder.P50) * 95f;
                score += claims * 260f;
                if (target == pawn.mindState?.enemyTarget)
                {
                    score -= 8f;
                }

                lane = new Lane
                {
                    target = target,
                    blocker = blocker,
                    approachCell = approachCell,
                    score = score,
                    exposure = exposure,
                    peerClaims = claims,
                    pathNodes = pathNodes
                };
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (path != null)
                {
                    path.ReleaseToPool();
                }
            }
        }

        private static bool IsPlayerPenetrationBlocker(Building building)
        {
            if (building == null || building.Destroyed || building.Faction != Faction.OfPlayer)
            {
                return false;
            }
            if (building is Building_Door door)
            {
                return !door.Open;
            }
            return building.def.Fillage == FillCategory.Full || building.def.passability == Traversability.Impassable;
        }

        private static int CountPeerClaims(Pawn pawn, Thing blocker, int now)
        {
            int count = 0;
            foreach (KeyValuePair<int, PawnState> pair in states)
            {
                if (pair.Key == pawn.thingIDNumber)
                {
                    continue;
                }
                PawnState other = pair.Value;
                if (other.blocker == blocker && other.commitUntilTick >= now)
                {
                    count++;
                }
            }
            return count;
        }

        private static void Commit(PawnState state, Lane lane, int now)
        {
            state.blocker = lane.blocker;
            state.approachCell = lane.approachCell;
            state.blockerHitPoints = lane.blocker?.HitPoints ?? -1;
            state.lastDamageTick = now;
            state.commitUntilTick = now + CommitTicks;
            state.stalledSamples = 0;
        }

        private static Job MakeBreachJob(Pawn pawn, Thing blocker, IntVec3 approachCell)
        {
            if (pawn == null || blocker == null || blocker.Destroyed || !blocker.Spawned)
            {
                return null;
            }

            Job job = null;
            try
            {
                job = DigUtility.PassBlockerJob(pawn, blocker, approachCell, true, true);
            }
            catch
            {
                job = null;
            }
            if (job == null)
            {
                job = JobMaker.MakeJob(JobDefOf.AttackMelee, blocker);
            }
            if (job != null)
            {
                job.expiryInterval = 900;
                job.checkOverrideOnExpire = true;
                job.expireRequiresEnemiesNearby = false;
                job.collideWithPawns = true;
                job.ignoreDesignations = true;
                job.maxNumMeleeAttacks = 300;
            }
            return job;
        }

        private static void RejectCurrentBlocker(PawnState state, int now, string reason)
        {
            Thing blocker = state.blocker;
            if (blocker != null)
            {
                state.rejectedBlockers[blocker.thingIDNumber] = now + RejectBlockerTicks;
                Log.Message(
                    "[CAI][RaidPenetrationV0.1] REPLAN blocker=" + DescribeThing(blocker) +
                    " reason=" + reason + ".");
            }
            ClearCommit(state);
            state.nextPlanTick = now;
            state.stalledSamples = StallSamplesBeforePlan;
        }

        private static void ClearCommit(PawnState state)
        {
            state.blocker = null;
            state.approachCell = IntVec3.Invalid;
            state.blockerHitPoints = -1;
            state.lastDamageTick = -1;
            state.commitUntilTick = -1;
        }

        private static bool IsRejected(PawnState state, Thing blocker, int now)
        {
            return blocker != null &&
                   state.rejectedBlockers.TryGetValue(blocker.thingIDNumber, out int until) &&
                   until > now;
        }

        private static void CleanupRejected(PawnState state, int now)
        {
            if (state.rejectedBlockers.Count == 0)
            {
                return;
            }

            List<int> expired = null;
            foreach (KeyValuePair<int, int> pair in state.rejectedBlockers)
            {
                if (pair.Value <= now)
                {
                    if (expired == null) expired = new List<int>();
                    expired.Add(pair.Key);
                }
            }
            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    state.rejectedBlockers.Remove(expired[i]);
                }
            }
        }

        private static string DescribeThing(Thing thing)
        {
            if (thing == null)
            {
                return "<null>";
            }
            return (thing.def?.defName ?? thing.GetType().Name) + "#" + thing.thingIDNumber + "@" + thing.Position;
        }
    }
}
