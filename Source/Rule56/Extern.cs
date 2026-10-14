using System.Runtime.CompilerServices;
using CombatAI.Comps;
using Verse;

namespace CombatAI
{
    /// <summary>
    /// Native RimWorld 1.7 component accessors.
    ///
    /// Prepatcher injected-field access is intentionally not used on the 1.7 branch.
    /// CAI already owns a fast component cache, so these accessors preserve the existing
    /// behavior without depending on Mono-era assembly rewriting.
    /// </summary>
    public static class Extern
    {
        public const bool active = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThingComp_CombatAI AI(this Pawn pawn)
        {
            return pawn.GetComp_Fast<ThingComp_CombatAI>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompAttachBase CompAttachBase(this Pawn pawn)
        {
            return pawn.GetComp_Fast<CompAttachBase>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MapComponent_CombatAI AI(this Map map)
        {
            return map.GetComp_Fast<MapComponent_CombatAI>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SightTracker Sight(this Map map)
        {
            return map.GetComp_Fast<SightTracker>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AvoidanceTracker Avoidance(this Map map)
        {
            return map.GetComp_Fast<AvoidanceTracker>();
        }
    }
}
