using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
namespace CombatAI
{
    public class CompatibilityManager
    {
        private static List<Type> types;

        public static void Initialize()
        {
            // find types
            types = typeof(CompatibilityManager).Assembly.GetTypes()
                .Where(t => t.HasAttribute<LoadIf>())
                .ToList();
            // start processing
            LoadIf    attr;
            LoadNamed named;
            RunIf     runned;
            foreach (Type type in types)
            {
                if ((attr = type.TryGetAttribute<LoadIf>()) != null && attr.packageId != null)
                {
                    bool packageLoaded = LoadedModManager.RunningMods.Any(m => CombatAI.Compatibility.ModContentPackCompat.GetPackageIdPlayerFacing(m).ToLower() == attr.packageId || m.PackageId == attr.packageId);
                    bool bindingsValid = packageLoaded;
                    if (packageLoaded)
                    {
                        Log.Message($"CAI: attempting LoadIf for {attr.packageId}");
                        // start loading defs / API bindings
                        foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
                        {
                            // Only fields owned by the compatibility loader are required
                            // bindings. Ordinary static helper/cache state must not disable
                            // an otherwise valid optional-mod bridge.
                            if (!field.IsStatic || field.HasAttribute<UnsavedAttribute>())
                            {
                                continue;
                            }

                            Type fieldType = field.FieldType;
                            bool loaderOwned = typeof(Def).IsAssignableFrom(fieldType)
                                               || field.HasAttribute<LoadNamed>()
                                               || (fieldType == typeof(bool) && field.Name == "active");
                            if (!loaderOwned)
                            {
                                continue;
                            }

                            bool success = false;
                            try
                            {
                                if (typeof(Def).IsAssignableFrom(fieldType))
                                {
                                    object value = AccessTools.Method(typeof(DefDatabase<>).MakeGenericType(fieldType), "GetNamed").Invoke(null, new object[]
                                    {
                                        field.Name, false
                                    });
                                    field.SetValue(null, value);
                                    success = value != null;
                                }
                                else if (field.HasAttribute<LoadNamed>() && (named = field.TryGetAttribute<LoadNamed>())?.name != null)
                                {
                                    object value = null;
                                    switch (named.type)
                                    {
                                        case LoadableType.Unspecified:
                                            if (typeof(Type).IsAssignableFrom(fieldType))
                                            {
                                                value = AccessTools.TypeByName(named.name);
                                            }
                                            else if (typeof(FieldInfo).IsAssignableFrom(fieldType))
                                            {
                                                value = AccessTools.Field(named.name);
                                            }
                                            else if (typeof(MethodInfo).IsAssignableFrom(fieldType))
                                            {
                                                value = AccessTools.Method(named.name, named.prams);
                                            }
                                            break;
                                        case LoadableType.Field:
                                            value = AccessTools.Field(named.name);
                                            break;
                                        case LoadableType.Type:
                                            value = AccessTools.TypeByName(named.name);
                                            break;
                                        case LoadableType.Constructor:
                                        {
                                            Type owner = AccessTools.TypeByName(named.name);
                                            value = owner == null ? null : AccessTools.Constructor(owner, named.prams);
                                            break;
                                        }
                                        case LoadableType.Setter:
                                            value = AccessTools.PropertySetter(named.name);
                                            break;
                                        case LoadableType.Getter:
                                            value = AccessTools.PropertyGetter(named.name);
                                            break;
                                        case LoadableType.Method:
                                            value = AccessTools.Method(named.name, named.prams);
                                            break;
                                    }
                                    field.SetValue(null, value);
                                    success = value != null;
                                }
                                else if (fieldType == typeof(bool) && field.Name == "active")
                                {
                                    field.SetValue(null, true);
                                    success = true;
                                }
                            }
                            catch (Exception exception)
                            {
                                Log.Error($"CAI: Failed while binding '{field.Name}'({field.FieldType}) from '{attr.packageId}': {exception}");
                                success = false;
                            }

                            if (!success || field.GetValue(null) == null)
                            {
                                bindingsValid = false;
                                Log.Error($"CAI: Failed to find '{field.Name}'({field.FieldType}) from '{attr.packageId}'. CAI compatibility for this mod will stay disabled.");
                            }
                            else
                            {
                                Log.Message($"CAI: <color=green>Load success</color> '{field.Name}'t<{field.FieldType}> from '{attr.packageId}'");
                            }
                        }
                    }

                    bool loaded = packageLoaded && bindingsValid;
                    if (packageLoaded && !loaded)
                    {
                        FieldInfo activeField = AccessTools.GetDeclaredFields(type)
                            .FirstOrDefault(field => field.IsStatic && field.FieldType == typeof(bool) && field.Name == "active");
                        activeField?.SetValue(null, false);
                        Log.Warning($"CAI: Disabled compatibility bridge for '{attr.packageId}' because one or more required API bindings could not be resolved.");
                    }

                    foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                    {
                        if (method.HasAttribute<RunIf>() && (runned = method.TryGetAttribute<RunIf>()) != null && loaded == runned.loaded)
                        {
                            try
                            {
                                method.Invoke(null, Array.Empty<object>());
                            }
                            catch (Exception exception)
                            {
                                Log.Error($"CAI: Compatibility callback '{type.FullName}.{method.Name}' failed for '{attr.packageId}': {exception}");
                            }
                        }
                    }
                }
            }
        }

        private static T GetDef<T>(string name) where T : Def
        {
            return DefDatabase<T>.GetNamed(name);
        }
    }
}
