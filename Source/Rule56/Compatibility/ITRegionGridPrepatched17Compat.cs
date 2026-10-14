using System;
using Verse;

namespace CombatAI
{
    /// <summary>
    /// Compile-time compatibility shell for the historical Prepatcher region-grid type.
    ///
    /// RimWorld 1.7 never selects this backend because Extern.active is a compile-time
    /// false constant. The actual 1.7 region storage owner is ITRegionGridLegacy.
    /// Keeping this shell temporarily lets the shared SightGrid source compile without
    /// pulling Prepatcher or injected Region fields into the native 1.7 build.
    /// </summary>
    [Obsolete("RimWorld 1.7 uses ITRegionGridLegacy; Prepatcher injection is not supported.")]
    internal sealed class ITRegionGridPrepatched : ITRegionGridLegacy
    {
        public ITRegionGridPrepatched(Map map, int gridId) : base(map)
        {
        }
    }
}
