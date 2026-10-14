using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CombatAI.Patches
{
    [HarmonyPatch]
    public static class Mote_Draw_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            HashSet<MethodBase> methods = new HashSet<MethodBase>();
            System.Type[] signature = { typeof(Vector3), typeof(bool) };

            MethodInfo baseDrawAt = AccessTools.DeclaredMethod(typeof(Mote), "DrawAt", signature);
            if (baseDrawAt != null)
            {
                methods.Add(baseDrawAt);
            }

            // RimWorld 1.7/Waylith draws motes through protected DrawAt rather than a
            // public Draw method. Patch only Mote subclasses that actually declare the
            // exact DrawAt(Vector3,bool) override so the prefix can suppress the whole
            // most-derived presentation path under CAI fog.
            foreach (System.Type type in typeof(Mote).AllSubclasses())
            {
                MethodInfo drawAt = AccessTools.DeclaredMethod(type, "DrawAt", signature);
                if (drawAt != null && drawAt.HasMethodBody())
                {
                    methods.Add(drawAt);
                }
            }

            return methods;
        }

        public static bool Prefix(Mote __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }
                MapComponent_FogGrid fog = Pawn_Patch.fogThings;
                if (fog == null)
                {
                    return true;
                }
                // Ensure the fog component belongs to the same map as the mote.
                if (fog.map == null || __instance.Map == null || fog.map != __instance.Map)
                {
                    return true;
                }
                return !fog.IsFogged(__instance.Position);
            }
            catch
            {
                return true;
            }
        }
    }
}
