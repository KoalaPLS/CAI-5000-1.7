using System.Runtime.CompilerServices;
using CombatAI.Comps;
using Verse;
#if !RW_1_7
using Prepatcher;
#endif

namespace CombatAI
{
#if RW_1_7
    /// <summary>
    /// Native RimWorld 1.7 component accessors.
    /// Prepatcher injection is not part of the Waylith build; CAI's own component cache
    /// remains the sole accessor path.
    /// </summary>
    public static class Extern
    {
        // Fixed false but deliberately non-const because SightGrid is shared source.
        // RegionGrid17Alias maps the historical alternate symbol back to the same
        // direct-array ITRegionGridLegacy owner.
        public static readonly bool active = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThingComp_CombatAI AI(this Pawn pawn) => pawn.GetComp_Fast<ThingComp_CombatAI>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompAttachBase CompAttachBase(this Pawn pawn) => pawn.GetComp_Fast<CompAttachBase>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MapComponent_CombatAI AI(this Map map) => map.GetComp_Fast<MapComponent_CombatAI>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SightTracker Sight(this Map map) => map.GetComp_Fast<SightTracker>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AvoidanceTracker Avoidance(this Map map) => map.GetComp_Fast<AvoidanceTracker>();
    }
#else
    [LoadIf("zetrith.prepatcher")]
    public static class Extern
    {
#pragma warning disable CS0649
        public static bool active;
#pragma warning restore CS0649

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ThingComp_CombatAI AI(this Pawn pawn)
        {
            return pawn.GetComp_Fast<ThingComp_CombatAI>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompAttachBase CompAttachBase(this Pawn pawn)
        {
            if (active)
            {
                return CombatAI_CompAttachBase(pawn);
            }
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

        [PrepatcherField]
        [InjectComponent]
        private static extern ThingComp_CombatAI CombatAI_ThingComp(Pawn pawn);
        [PrepatcherField]
        [InjectComponent]
        private static extern CompAttachBase CombatAI_CompAttachBase(Pawn pawn);
        [PrepatcherField]
        [InjectComponent]
        private static extern MapComponent_CombatAI CombatAI_MapComp(Map map);
        [PrepatcherField]
        [InjectComponent]
        private static extern SightTracker CombatAI_Sight(Map map);
        [PrepatcherField]
        [InjectComponent]
        private static extern AvoidanceTracker CombatAI_Avoidance(Map map);
    }
#endif
}
