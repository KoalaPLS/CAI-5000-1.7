using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CombatAI.Patches
{
	public static class PathFinder_Patch
	{
		// Debug flags (used by debug UI)
		public static bool FlashSearch { get; set; }
		public static bool FlashSapperPath { get; set; }

		// Registered manually because RimWorld 1.6 and 1.7 expose different pathfinder surfaces.
		public static void Patch(Harmony harmony)
		{
			try
			{
				MethodInfo target;
#if RW_1_7
				target = AccessTools.Method(
					typeof(PathFinder),
					nameof(PathFinder.FindPathNow),
					new[]
					{
						typeof(IntVec3),
						typeof(LocalTargetInfo),
						typeof(Pawn),
						typeof(Nullable<PathFinderCostTuning>),
						typeof(PathEndMode)
					});
#else
				target = null;
				foreach (MethodInfo method in typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (method.Name != "FindPath")
					{
						continue;
					}
					ParameterInfo[] parameters = method.GetParameters();
					if (parameters.Length >= 2 && parameters[0].ParameterType == typeof(IntVec3) && parameters[1].ParameterType == typeof(LocalTargetInfo))
					{
						target = method;
						break;
					}
				}
#endif
				if (target == null)
				{
					Log.Warning("ISMA: PathFinder debug patch target was not found.");
					return;
				}

				HarmonyMethod postfix = new HarmonyMethod(
					typeof(PathFinder_Patch).GetMethod(nameof(PostfixMethod), BindingFlags.Static | BindingFlags.NonPublic));
				harmony.Patch(target, postfix: postfix);
			}
			catch (Exception exception)
			{
				Log.Warning("ISMA: PathFinder debug patch failed: " + exception);
			}
		}

		// The result already contains the path we want to visualize. Never call the
		// pathfinder again from its own postfix; doing so would recurse through the hook.
		private static void PostfixMethod(PathFinder __instance, PawnPath __result)
		{
			try
			{
				if (!FlashSearch && !FlashSapperPath)
				{
					return;
				}
				if (__result == null)
				{
					return;
				}

				FieldInfo mapField = AccessTools.Field(typeof(PathFinder), "map");
				Map map = mapField?.GetValue(__instance) as Map;
				if (map == null)
				{
					return;
				}

				var nodes = __result.GetNodes();
				if (nodes == null)
				{
					return;
				}
				for (int i = 0; i < nodes.Count; i++)
				{
					map.debugDrawer.FlashCell(nodes[i], Mathf.Clamp01((float)i / Math.Max(1, nodes.Count)), $"{nodes.Count - i}", 30);
				}
			}
			catch (Exception exception)
			{
				Log.Warning("ISMA: PathFinder debug visualization failed: " + exception);
			}
			finally
			{
				FlashSearch = false;
				FlashSapperPath = false;
			}
		}
	}
}
