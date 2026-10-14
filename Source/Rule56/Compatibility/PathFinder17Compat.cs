using Verse;
using Verse.AI;

namespace CombatAI.Compatibility
{
    /// <summary>
    /// RimWorld 1.7 pathfinding adapter.
    ///
    /// 1.7 replaced the old FindPath surface with the job-backed FindPathNow API.
    /// CAI should consume that native implementation rather than reflect over obsolete
    /// overload names or recreate pathfinding on the mod side.
    /// </summary>
    public static class PathFinder17Compat
    {
        public static PawnPath FindPath(Map map, IntVec3 start, IntVec3 dest, Pawn pawn)
        {
            if (map?.pathFinder == null)
            {
                return null;
            }

            LocalTargetInfo target = new LocalTargetInfo(dest);
            if (pawn != null)
            {
                return map.pathFinder.FindPathNow(start, target, pawn);
            }

            // Pawn-less requests are currently diagnostic/debug callers. Use the
            // ordinary door-passing traversal contract instead of inventing pawn state.
            return map.pathFinder.FindPathNow(
                start,
                target,
                TraverseParms.For(TraverseMode.PassDoors));
        }
    }
}
