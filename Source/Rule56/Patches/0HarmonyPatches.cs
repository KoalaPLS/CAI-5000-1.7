using System;
using Verse;
namespace CombatAI.Patches
{
    public static class HarmonyPatches
    {
        public static void Initialize()
        {
            // queue patches
            LongEventHandler.QueueLongEvent(PatchAll, "CombatAI.Preparing", false, null);
            // manual patches
            MainMenuDrawer_Patch.Patch();
#if !RW_1_7
            // RimWorld 1.7 no longer exposes PawnRenderer.DrawPawn. RenderPawnAt is
            // covered by the attribute patch; keep the legacy DrawPawn scan for 1.6.
            PawnRenderer_Patch.Patch();
#endif
            Selector_Patch.Patch();
            LabelSuppressor_Patch.Patch();
        }

        private static void PatchAll()
        {
            Log.Message("ISMA: Applying patches");
            // Run attribute-based patches
            Finder.Harmony.PatchAll();
            // Register any runtime-only patches
            try
            {
                PathFinder_Patch.Patch(Finder.Harmony);
            }
            catch { }
            try
            {
                SiegeBlueprintPlacer_CanPlaceBlueprint_Patch.Register(Finder.Harmony);
            }
            catch { }
        }
    }
}
