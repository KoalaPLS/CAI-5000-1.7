using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CombatAI.Patches
{
    public static class LabelSuppressor_Patch
    {
        public static void Patch()
        {
            try
            {
                Harmony harmony = Finder.Harmony;
                HarmonyMethod prefix = new HarmonyMethod(
                    typeof(LabelSuppressor_Patch).GetMethod(nameof(LabelPrefix), BindingFlags.NonPublic | BindingFlags.Static));

                MethodInfo vectorTarget = AccessTools.Method(
                    typeof(GenMapUI),
                    nameof(GenMapUI.DrawPawnLabel),
                    new[]
                    {
                        typeof(Pawn), typeof(Vector2), typeof(float), typeof(float),
                        typeof(Dictionary<string, string>), typeof(GameFont), typeof(bool), typeof(bool)
                    });

                MethodInfo rectTarget = AccessTools.Method(
                    typeof(GenMapUI),
                    nameof(GenMapUI.DrawPawnLabel),
                    new[]
                    {
                        typeof(Pawn), typeof(Rect), typeof(float), typeof(float),
                        typeof(Dictionary<string, string>), typeof(GameFont), typeof(bool), typeof(bool)
                    });

                if (vectorTarget == null || rectTarget == null)
                {
                    Log.Warning("ISMA: Exact GenMapUI.DrawPawnLabel targets were not found; pawn label fog suppression was not installed.");
                    return;
                }

                harmony.Patch(vectorTarget, prefix: prefix);
                harmony.Patch(rectTarget, prefix: prefix);
            }
            catch (Exception exception)
            {
                Log.Error("ISMA: LabelSuppressor_Patch.Patch failed: " + exception);
            }
        }

        private static bool LabelPrefix(Pawn pawn)
        {
            try
            {
                if (!Finder.Settings.FogOfWar_Enabled || pawn == null || pawn.Destroyed || !pawn.Spawned)
                {
                    return true;
                }

                MapComponent_FogGrid grid = pawn.Map?.GetComp_Fast<MapComponent_FogGrid>();
                return grid == null || !grid.IsFogged(pawn.Position);
            }
            catch (Exception exception)
            {
                Log.Warning("ISMA: LabelSuppressor_Patch.LabelPrefix exception: " + exception);
                return true;
            }
        }
    }
}
