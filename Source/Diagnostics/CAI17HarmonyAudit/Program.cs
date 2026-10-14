using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatAI;
using CombatAI.Patches;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

internal static class Program
{
    private const string AuditHarmonyId = "KoalaPLS.CAI5000.RW17.StaticHarmonyAudit";

    private static int Main()
    {
        Console.WriteLine("CAI 5000 RimWorld 1.7 Harmony static audit");
        Console.WriteLine($"CombatAI assembly: {typeof(CombatAIMod).Assembly.Location}");
        Console.WriteLine($"Game assembly: {typeof(Pawn).Assembly.Location}");
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

            // Several production manual patchers deliberately fail closed by logging and
            // returning instead of throwing. That is correct for a live mod, but a static
            // audit must not convert those swallowed failures into a false green result.
            RequireOwnedPatch(
                method => method.DeclaringType == typeof(PathFinder) && method.Name == nameof(PathFinder.FindPathNow),
                1,
                "PathFinder.FindPathNow debug hook");

            RequireOwnedPatch(
                method => method.DeclaringType == typeof(Selector) &&
                          (method.Name == "Select" || method.Name == "SelectInternal"),
                1,
                "Selector fog-selection hook");

            RequireOwnedPatch(
                method => method.DeclaringType == typeof(GenMapUI) && method.Name == nameof(GenMapUI.DrawPawnLabel),
                2,
                "GenMapUI.DrawPawnLabel fog-label hooks");

            Console.WriteLine("AUDIT PASS: attribute targets, transpilers, and required manual Harmony patches applied successfully.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("AUDIT FAIL: Harmony target/transpiler/manual-patch validation failed.");
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

    private static void RequireOwnedPatch(Func<MethodBase, bool> predicate, int minimumCount, string label)
    {
        List<MethodBase> matches = Harmony.GetAllPatchedMethods()
            .Where(predicate)
            .Where(IsOwnedByAudit)
            .ToList();

        if (matches.Count < minimumCount)
        {
            throw new InvalidOperationException(
                $"Required manual patch '{label}' was not installed by {AuditHarmonyId}. " +
                $"Expected at least {minimumCount}, found {matches.Count}.");
        }

        Console.WriteLine($"MANUAL PATCH PASS: {label} ({matches.Count})");
    }

    private static bool IsOwnedByAudit(MethodBase method)
    {
        Patches patchInfo = Harmony.GetPatchInfo(method);
        return patchInfo != null && patchInfo.Owners.Contains(AuditHarmonyId);
    }
}
