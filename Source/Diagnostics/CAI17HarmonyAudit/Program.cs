using System;
using System.Reflection;
using CombatAI;
using CombatAI.Patches;
using HarmonyLib;

internal static class Program
{
    private const string AuditHarmonyId = "KoalaPLS.CAI5000.RW17.StaticHarmonyAudit";

    private static int Main()
    {
        Console.WriteLine("CAI 5000 RimWorld 1.7 Harmony static audit");
        Console.WriteLine($"CombatAI assembly: {typeof(CombatAIMod).Assembly.Location}");
        Console.WriteLine($"Game assembly: {typeof(Verse.Pawn).Assembly.Location}");
        Console.WriteLine($"Harmony assembly: {typeof(Harmony).Assembly.Location}");

        Harmony harmony = new Harmony(AuditHarmonyId);
        Finder.Harmony = harmony;
        Finder.Settings = new Settings();

        try
        {
            Assembly combatAI = typeof(CombatAIMod).Assembly;

            // This is intentionally performed in a throwaway console process. Harmony
            // must resolve every attribute target and execute every transpiler against
            // the exact installed RimWorld 1.7 Assembly-CSharp before we attempt an
            // in-game launch.
            harmony.PatchAll(combatAI);

            // Manual patch owners are not discovered by PatchAll.
            PathFinder_Patch.Patch(harmony);
            SiegeBlueprintPlacer_CanPlaceBlueprint_Patch.Register(harmony);
            MainMenuDrawer_Patch.Patch();
            Selector_Patch.Patch();
            LabelSuppressor_Patch.Patch();

            Console.WriteLine("AUDIT PASS: attribute and manual Harmony patch application completed without exception.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("AUDIT FAIL: Harmony target/transpiler application threw an exception.");
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try
            {
                harmony.UnpatchAll(AuditHarmonyId);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine("AUDIT CLEANUP WARNING: " + cleanupException);
            }
        }
    }
}
