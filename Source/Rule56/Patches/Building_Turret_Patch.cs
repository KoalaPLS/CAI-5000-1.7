using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CombatAI.Patches
{
    public static class Building_Turret_Patch
    {
        private static List<Type>       types;
        private static List<MethodInfo> spawnMethods;
        private static List<MethodInfo> despawnMethods;

        private static List<Type> TurretSubclasses =>
            types ??= typeof(Building_TurretGun).AllSubclassesNonAbstract().ToList();

        [HarmonyPatch]
        private static class Building_Turret_SpawnSetup_Patch
        {
            public static bool Prepare()
            {
                if (spawnMethods != null)
                {
                    return spawnMethods.Count != 0;
                }

                spawnMethods = new List<MethodInfo>();
                foreach (Type type in TurretSubclasses)
                {
                    MethodInfo method = AccessTools.Method(type, nameof(Building_Turret.SpawnSetup));
                    if (method != null && method.DeclaringType == type && method.HasMethodBody())
                    {
                        spawnMethods.Add(method);
                    }
                }
                return spawnMethods.Count != 0;
            }

            public static IEnumerable<MethodBase> TargetMethods()
            {
                return spawnMethods;
            }

            public static void Postfix(Building_TurretGun __instance)
            {
                TurretTracker tracker = __instance.Map?.GetComp_Fast<TurretTracker>();
                tracker?.Register(__instance);
            }
        }

        [HarmonyPatch]
        private static class Building_Turret_DeSpawn_Patch
        {
            public static bool Prepare()
            {
                if (despawnMethods != null)
                {
                    return despawnMethods.Count != 0;
                }

                despawnMethods = new List<MethodInfo>();
                foreach (Type type in TurretSubclasses)
                {
                    MethodInfo method = AccessTools.Method(type, nameof(Building_Turret.DeSpawn));
                    if (method != null && method.DeclaringType == type && method.HasMethodBody())
                    {
                        despawnMethods.Add(method);
                    }
                }
                return despawnMethods.Count != 0;
            }

            public static IEnumerable<MethodBase> TargetMethods()
            {
                return despawnMethods;
            }

            public static void Prefix(Building_TurretGun __instance)
            {
                TurretTracker tracker = __instance.Map?.GetComp_Fast<TurretTracker>();
                tracker?.DeRegister(__instance);
            }
        }
    }
}
