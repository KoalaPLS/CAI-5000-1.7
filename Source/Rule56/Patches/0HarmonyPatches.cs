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
#if RW_1_7
            // The ThinkNode job logger is debug-only. Harmony 3 treats an attributed
            // patch class with zero discovered targets as an error, while performance
            // helpers may intentionally suppress this dormant global postfix. Keep it
            // out of PatchAll on 1.7 and install it only when the user actually enabled
            // job logging.
            if (Finder.Settings.Debug && Finder.Settings.Debug_LogJobs)
            {
                try
                {
                    ThinkNode_Patch.EnsureJobLoggingPatchInstalled();
                }
                catch (Exception exception)
                {
                    Log.Error($"ISMA: Failed to enable RimWorld 1.7 ThinkNode job logging: {exception}");
                }
            }
#endif
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
