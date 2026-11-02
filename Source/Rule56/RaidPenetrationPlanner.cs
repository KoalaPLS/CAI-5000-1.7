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
    /// Strategic fallback for hostile assault pawns that repeatedly stall on CAI's normal
    /// closed-door route. Tactical shooting, cover and retreat remain owned by CAI.
    /// </summary>
    public static class RaidPenetrationPlanner
    {
        private const int SampleTicks = 120;
        private const int StallSamples = 2;
        private const int PlanCooldown = 180;
        private const int CommitTicks = 1200;
        private const int NoDamageTicks = 720;
        private const int RejectTicks = 1200;
        private const int MaxTargets = 8;
        private const int MaxTargetsScanned = 96;

        private sealed class State
        {
            public IntVec3 anchor = IntVec3.Invalid;
            public int lastSample = -1;
            public int stalls;
            public int nextPlan;
            public Thing blocker;
            public IntVec3 approach = IntVec3.Invalid;
            public int blockerHp = -1;
            public int lastDamage = -1;
            public int commitUntil = -1;
            public readonly Dictionary<int, int> rejected = new Dictionary<int, int>();
        }

        private struct Lane
        {
            public Thing target;
            public Thing blocker;
            public IntVec3 approach;
            public float score;
            public float exposure;
            public int defenders;
            public int claims;
            public int nodes;
        }

        private static readonly Dictionary<int, State> states = new Dictionary<int, State>();

        public static void ClearCache() => states.Clear();

        public static bool TryOverrideDoorBoundGoto(Pawn pawn, Thing preferredTarget, out Job job)
        {
            job = null;
            if (!Eligible(pawn) || preferredTarget == null || preferredTarget.Destroyed || !preferredTarget.Spawned)
            {
                if (pawn != null) states.Remove(pawn.thingIDNumber);
                return false;
            }

            State state = GetState(pawn);
            int now = GenTicks.TicksGame;
            TrimRejected(state, now);
            if (Maintain(pawn, state, now, out job)) return true;

            bool doorBound;
            try
            {
                doorBound = !pawn.CanReach(preferredTarget, PathEndMode.InteractionCell, Danger.Deadly,
                    false, false, TraverseMode.NoPassClosedDoors);
            }
            catch
            {
                doorBound = false;
            }

            if (!doorBound)
            {
                ResetObservation(state, pawn.Position, now);
                return false;
            }

            Observe(state, pawn.Position, now);
            if (state.stalls < StallSamples || now < state.nextPlan) return false;
            state.nextPlan = now + PlanCooldown;

            if (!TryPlan(pawn, preferredTarget, state, now, out Lane lane)) return false;
            Commit(state, lane, now);
            job = MakeBreachJob(pawn, lane.blocker, lane.approach);
            if (job == null)
            {
                Reject(state, now, "job-null");
                return false;
            }

            Log.Message(
                "[CAI][RaidPenetrationV0.1] COMMIT pawn=" + pawn.LabelShortCap +
                " blocker=" + Describe(lane.blocker) +
                " target=" + Describe(lane.target) +
                " score=" + lane.score.ToString("0.0") +
                " exposure=" + lane.exposure.ToString("0.00") +
                " nearbyDefenders=" + lane.defenders +
                " peerClaims=" + lane.claims +
                " pathNodes=" + lane.nodes + ".");
            return true;
        }

        public static bool TryMaintainBreach(Pawn pawn, out Job job)
        {
            job = null;
            if (!Eligible(pawn) || !states.TryGetValue(pawn.thingIDNumber, out State state)) return false;
            return Maintain(pawn, state, GenTicks.TicksGame, out job);
        }

        private static bool Eligible(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null || !pawn.Map.IsPlayerHome) return false;
            if (pawn.Faction == null || pawn.Faction == Faction.OfPlayer || !pawn.HostileTo(Faction.OfPlayer)) return false;
            if (pawn.RaceProps == null || pawn.RaceProps.Animal) return false;
            DutyDef duty = pawn.mindState?.duty?.def;
            return duty == DutyDefOf.AssaultColony || duty == DutyDefOf.AssaultThing || duty == CombatAI_DutyDefOf.CombatAI_AssaultPoint;
        }

        private static State GetState(Pawn pawn)
        {
            if (!states.TryGetValue(pawn.thingIDNumber, out State state))
            {
                state = new State();
                states[pawn.thingIDNumber] = state;
            }
            return state;
        }

        private static void ResetObservation(State state, IntVec3 pos, int now)
        {
            state.anchor = pos;
            state.lastSample = now;
            state.stalls = 0;
        }

        private static void Observe(State state, IntVec3 pos, int now)
        {
            if (!state.anchor.IsValid || state.lastSample < 0)
            {
                ResetObservation(state, pos, now);
                return;
            }
            if (now - state.lastSample < SampleTicks) return;

            if (state.anchor.DistanceToSquared(pos) >= 25)
            {
                state.anchor = pos;
                state.stalls = 0;
            }
            else
            {
                state.stalls++;
            }
            state.lastSample = now;
        }

        private static bool Maintain(Pawn pawn, State state, int now, out Job job)
        {
            job = null;
            Thing blocker = state.blocker;
            if (blocker == null) return false;

            if (blocker.Destroyed || !blocker.Spawned || blocker.Map != pawn.Map || blocker.Faction != Faction.OfPlayer)
            {
                ClearCommit(state);
                ResetObservation(state, pawn.Position, now);
                return false;
            }

            int hp = blocker.HitPoints;
            if (state.blockerHp < 0 || hp < state.blockerHp)
            {
                state.blockerHp = hp;
                state.lastDamage = now;
            }
            if (state.lastDamage < 0) state.lastDamage = now;

            if (now - state.lastDamage > NoDamageTicks)
            {
                Reject(state, now, "no-damage-progress");
                return false;
            }
            if (now > state.commitUntil && now - state.lastDamage > SampleTicks)
            {
                ClearCommit(state);
                return false;
            }

            job = MakeBreachJob(pawn, blocker, state.approach);
            if (job == null)
            {
                Reject(state, now, "maintain-job-null");
                return false;
            }
            return true;
        }

        private static bool TryPlan(Pawn pawn, Thing preferredTarget, State state, int now, out Lane best)
        {
            best = default;
            bool found = false;
            List<Thing> candidates = CollectTargets(pawn, preferredTarget);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!EvaluateLane(pawn, candidates[i], state, now, out Lane lane)) continue;
                if (!found || lane.score < best.score)
                {
                    best = lane;
                    found = true;
                }
            }
            return found;
        }

        private static List<Thing> CollectTargets(Pawn pawn, Thing preferred)
        {
            List<Thing> output = new List<Thing>(MaxTargets);
            AddUnique(output, preferred);
            AddUnique(output, pawn.mindState?.enemyTarget);

            var potential = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            if (potential == null || potential.Count == 0) return output;

            Thing nearest = null, farthest = null, minX = null, maxX = null, minZ = null, maxZ = null;
            int nearestDist = int.MaxValue, farthestDist = int.MinValue;
            int limit = Mathf.Min(potential.Count, MaxTargetsScanned);
            for (int i = 0; i < limit; i++)
            {
                IAttackTarget attackTarget = potential[i];
                Thing thing = attackTarget?.Thing;
                if (thing == null || !thing.Spawned || thing.Destroyed || thing.Faction != Faction.OfPlayer || attackTarget.ThreatDisabled(pawn)) continue;
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
            if (output.Count < MaxTargets && pawn.mindState?.duty != null && pawn.mindState.duty.focus.HasThing)
            {
                AddUnique(output, pawn.mindState.duty.focus.Thing);
            }
            return output;
        }

        private static void AddUnique(List<Thing> list, Thing thing)
        {
            if (thing == null || list.Count >= MaxTargets) return;
            for (int i = 0; i < list.Count; i++) if (list[i] == thing) return;
            list.Add(thing);
        }

        private static bool EvaluateLane(Pawn pawn, Thing target, State state, int now, out Lane lane)
        {
            lane = default;
            if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map) return false;

            PawnPath path = null;
            try
            {
                TraverseParms parms = TraverseParms.For(pawn);
                parms.mode = TraverseMode.PassAllDestroyableThings;
                parms.maxDanger = Danger.Deadly;
                parms.canBashDoors = true;
                parms.canBashFences = true;
                path = pawn.Map.pathFinder.FindPathNow(pawn.Position, new LocalTargetInfo(target.Position), parms);
                if (path == null || !path.Found) return false;

                IList<IntVec3> nodes = path.GetNodes();
                int cur = path.GetCurNodeIndex();
                if (nodes == null || nodes.Count == 0 || cur <= 0 || cur >= nodes.Count) return false;

                Thing blocker = null;
                IntVec3 approach = nodes[cur];
                IntVec3 previous = approach;
                int nodeCount = 0;
                for (int i = cur - 1; i >= 0; i--)
                {
                    IntVec3 cell = nodes[i];
                    nodeCount++;
                    Building edifice = cell.GetEdifice(pawn.Map);
                    if (IsBlocker(edifice))
                    {
                        blocker = edifice;
                        approach = previous;
                        break;
                    }
                    previous = cell;
                }
                if (blocker == null || !approach.IsValid || IsRejected(state, blocker, now)) return false;

                float exposure = 0f;
                try
                {
                    if (pawn.TryGetSightReader(out SightTracker.SightReader reader)) exposure = reader.GetAbsVisibilityToEnemies(approach);
                }
                catch { }

                int defenders = NearbyDefenders(pawn.Map, blocker.Position);
                int claims = PeerClaims(pawn, blocker, now);
                bool door = blocker is Building_Door;
                float score = nodeCount;
                score += Mathf.Max(blocker.HitPoints, 1) * 0.08f;
                score += door ? 0f : 18f;
                score += Mathf.Min(exposure, 8f * Finder.P50) * 95f;
                score += defenders * 65f;
                score += claims * 260f;
                if (target == pawn.mindState?.enemyTarget) score -= 8f;

                lane.target = target;
                lane.blocker = blocker;
                lane.approach = approach;
                lane.score = score;
                lane.exposure = exposure;
                lane.defenders = defenders;
                lane.claims = claims;
                lane.nodes = nodeCount;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                path?.ReleaseToPool();
            }
        }

        private static bool IsBlocker(Building building)
        {
            if (building == null || building.Destroyed || building.Faction != Faction.OfPlayer) return false;
            if (building is Building_Door door) return !door.Open;
            return building.def.Fillage == FillCategory.Full || building.def.passability == Traversability.Impassable;
        }

        private static int NearbyDefenders(Map map, IntVec3 cell)
        {
            int count = 0;
            if (map?.mapPawns == null) return count;
            foreach (Pawn defender in map.mapPawns.AllPawnsSpawned)
            {
                if (defender == null || defender.Faction != Faction.OfPlayer || defender.Dead || defender.Downed || !defender.IsCombatant()) continue;
                if (defender.Position.DistanceToSquared(cell) <= 64) count++;
            }
            return count;
        }

        private static int PeerClaims(Pawn pawn, Thing blocker, int now)
        {
            int count = 0;
            foreach (KeyValuePair<int, State> pair in states)
            {
                if (pair.Key == pawn.thingIDNumber) continue;
                if (pair.Value.blocker == blocker && pair.Value.commitUntil >= now) count++;
            }
            return count;
        }

        private static void Commit(State state, Lane lane, int now)
        {
            state.blocker = lane.blocker;
            state.approach = lane.approach;
            state.blockerHp = lane.blocker?.HitPoints ?? -1;
            state.lastDamage = now;
            state.commitUntil = now + CommitTicks;
            state.stalls = 0;
        }

        private static Job MakeBreachJob(Pawn pawn, Thing blocker, IntVec3 approach)
        {
            if (pawn == null || blocker == null || blocker.Destroyed || !blocker.Spawned) return null;
            Job job = null;
            try { job = DigUtility.PassBlockerJob(pawn, blocker, approach, true, true); }
            catch { }
            if (job == null) job = JobMaker.MakeJob(JobDefOf.AttackMelee, blocker);
            if (job == null) return null;
            job.expiryInterval = 900;
            job.checkOverrideOnExpire = true;
            job.expireRequiresEnemiesNearby = false;
            job.collideWithPawns = true;
            job.ignoreDesignations = true;
            job.maxNumMeleeAttacks = 300;
            return job;
        }

        private static void Reject(State state, int now, string reason)
        {
            if (state.blocker != null)
            {
                state.rejected[state.blocker.thingIDNumber] = now + RejectTicks;
                Log.Message("[CAI][RaidPenetrationV0.1] REPLAN blocker=" + Describe(state.blocker) + " reason=" + reason + ".");
            }
            ClearCommit(state);
            state.nextPlan = now;
            state.stalls = StallSamples;
        }

        private static void ClearCommit(State state)
        {
            state.blocker = null;
            state.approach = IntVec3.Invalid;
            state.blockerHp = -1;
            state.lastDamage = -1;
            state.commitUntil = -1;
        }

        private static bool IsRejected(State state, Thing blocker, int now)
        {
            return blocker != null && state.rejected.TryGetValue(blocker.thingIDNumber, out int until) && until > now;
        }

        private static void TrimRejected(State state, int now)
        {
            if (state.rejected.Count == 0) return;
            List<int> expired = null;
            foreach (KeyValuePair<int, int> pair in state.rejected)
            {
                if (pair.Value > now) continue;
                expired ??= new List<int>();
                expired.Add(pair.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) state.rejected.Remove(expired[i]);
        }

        private static string Describe(Thing thing)
        {
            return thing == null ? "<null>" : (thing.def?.defName ?? thing.GetType().Name) + "#" + thing.thingIDNumber + "@" + thing.Position;
        }
    }
}
