using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FloatMenuPerformanceMonitor
{
    /// <summary>
    /// Optional bridge to Dubs Performance Analyzer. This assembly has no
    /// compile-time dependency on DPA; all interaction is through the public
    /// entry contract exposed by PerformanceAnalyzer.dll.
    /// </summary>
    internal static class DpaIntegration
    {
        private const string HarmonyId =
            "vorsel.floatmenuperformancemonitor.dpa";
        private const string GuiControllerTypeName =
            "Analyzer.Profiling.GUIController";
        private const string EntryTypeName =
            "Analyzer.Profiling.Entry";
        private const string CategoryTypeName =
            "Analyzer.Profiling.Category";

        private static bool hookInstalled;
        private static bool registered;
        private static bool warned;

        internal static void Initialize()
        {
            Type guiControllerType = AccessTools.TypeByName(
                GuiControllerTypeName);
            if (guiControllerType == null)
            {
                return;
            }

            TryInstallInitializationHook(guiControllerType);
            LongEventHandler.ExecuteWhenFinished(TryRegisterEntries);
        }

        private static void TryInstallInitializationHook(
            Type guiControllerType)
        {
            if (hookInstalled)
            {
                return;
            }

            MethodInfo initializeTabs = AccessTools.Method(
                guiControllerType,
                "InitialiseTabs");
            MethodInfo postfix = AccessTools.Method(
                typeof(DpaIntegration),
                nameof(AfterDpaTabsInitialized));
            if (initializeTabs == null || postfix == null)
            {
                return;
            }

            new Harmony(HarmonyId).Patch(
                initializeTabs,
                postfix: new HarmonyMethod(postfix));
            hookInstalled = true;
        }

        private static void AfterDpaTabsInitialized()
        {
            registered = false;
            TryRegisterEntries();
        }

        internal static void TryRegisterEntries()
        {
            if (registered)
            {
                return;
            }

            try
            {
                Type guiControllerType = AccessTools.TypeByName(
                    GuiControllerTypeName);
                Type entryType = AccessTools.TypeByName(EntryTypeName);
                Type categoryType = AccessTools.TypeByName(CategoryTypeName);
                if (guiControllerType == null
                    || entryType == null
                    || categoryType == null)
                {
                    return;
                }

                object modderCategory = Enum.Parse(categoryType, "Modder");
                MethodInfo tabMethod = FindTabMethod(
                    guiControllerType,
                    categoryType);
                MethodInfo createEntryMethod = FindCreateEntryMethod(
                    entryType,
                    categoryType);
                if (tabMethod == null || createEntryMethod == null)
                {
                    WarnUnavailable();
                    return;
                }

                object modderTab = tabMethod.Invoke(
                    null,
                    new[] { modderCategory });
                if (modderTab == null)
                {
                    return;
                }

                FieldInfo entriesField = AccessTools.Field(
                    modderTab.GetType(),
                    "entries");
                IDictionary entries = entriesField?.GetValue(modderTab)
                    as IDictionary;
                if (entries == null)
                {
                    WarnUnavailable();
                    return;
                }

                RegisterEntry(
                    entries,
                    createEntryMethod,
                    modderCategory,
                    "Float Menu Overview",
                    typeof(DpaFloatMenuOverviewEntry));
                RegisterEntry(
                    entries,
                    createEntryMethod,
                    modderCategory,
                    "Float Menu Providers",
                    typeof(DpaFloatMenuProvidersEntry));
                RegisterEntry(
                    entries,
                    createEntryMethod,
                    modderCategory,
                    "Float Menu WorkGivers",
                    typeof(DpaFloatMenuWorkGiversEntry));

                registered = true;
                Log.Message(
                    "[FMPM-DPA] Registered Float Menu Overview, Providers, "
                    + "and WorkGivers entries in Dubs Performance Analyzer.");
            }
            catch (Exception exception)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning(
                        "[FMPM-DPA] Dubs Performance Analyzer was detected, "
                        + "but its integration API was not available. "
                        + "Player.log monitoring remains active. "
                        + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static MethodInfo FindTabMethod(
            Type guiControllerType,
            Type categoryType)
        {
            return guiControllerType.GetMethod(
                "Tab",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { categoryType },
                modifiers: null);
        }

        private static MethodInfo FindCreateEntryMethod(
            Type entryType,
            Type categoryType)
        {
            return entryType.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[]
                {
                    typeof(string),
                    categoryType,
                    typeof(Type),
                    typeof(bool),
                    typeof(bool)
                },
                modifiers: null);
        }

        private static void RegisterEntry(
            IDictionary entries,
            MethodInfo createEntryMethod,
            object category,
            string label,
            Type descriptorType)
        {
            foreach (DictionaryEntry pair in entries)
            {
                if (ReferenceEquals(pair.Value, descriptorType))
                {
                    return;
                }
            }

            object entry = createEntryMethod.Invoke(
                null,
                new object[]
                {
                    label,
                    category,
                    descriptorType,
                    false,
                    true
                });
            if (entry != null && !entries.Contains(entry))
            {
                entries.Add(entry, descriptorType);
            }
        }

        private static void WarnUnavailable()
        {
            if (warned)
            {
                return;
            }

            warned = true;
            Log.Warning(
                "[FMPM-DPA] Dubs Performance Analyzer was detected, but "
                + "the expected public entry API was not found. Player.log "
                + "monitoring remains active.");
        }
    }

    public static class DpaFloatMenuOverviewEntry
    {
        public static bool Active;
        private static bool warnedTargets;

        public static IEnumerable<MethodInfo> GetPatchMethods()
        {
            return DpaFloatMenuMethodDiscovery.ValidMethods(
                FindTarget(
                    typeof(FloatMenuMap), "DoWindowContents", false, typeof(void),
                    typeof(UnityEngine.Rect)),
                FindTarget(
                    typeof(FloatMenuMakerMap), "GetOptions", true,
                    typeof(List<FloatMenuOption>), typeof(List<Pawn>),
                    typeof(UnityEngine.Vector3), typeof(FloatMenuContext).MakeByRefType()),
                FindTarget(
                    typeof(FloatMenuMakerMap), "GetProviderOptions", true, typeof(void),
                    typeof(FloatMenuContext), typeof(List<FloatMenuOption>)));
        }

        // DPA's entry contract loads IL argument zero for __instance, including
        // on static methods. These three validated targets receive respectively
        // the menu instance, selected-pawn list, and menu context. A parameterized
        // callback keeps DPA off its pinned-profiler fast path, which is invalid
        // for reference-containing Profiler objects on the 1.7 runtime.
        public static string GetName(object __instance)
        {
            if (__instance is FloatMenuMap)
            {
                return "FMPM.Overview.FloatMenuMap.DoWindowContents";
            }

            if (__instance is List<Pawn>)
            {
                return "FMPM.Overview.FloatMenuMakerMap.GetOptions";
            }

            if (__instance is FloatMenuContext)
            {
                return "FMPM.Overview.FloatMenuMakerMap.GetProviderOptions";
            }

            return null;
        }

        public static string GetLabel(object __instance)
        {
            if (__instance is FloatMenuMap)
            {
                return "FloatMenuMap.DoWindowContents";
            }

            if (__instance is List<Pawn>)
            {
                return "FloatMenuMakerMap.GetOptions";
            }

            if (__instance is FloatMenuContext)
            {
                return "FloatMenuMakerMap.GetProviderOptions";
            }

            return null;
        }

        private static MethodInfo FindTarget(
            Type type, string name, bool isStatic, Type returnType,
            params Type[] parameterTypes)
        {
            MethodInfo method = AccessTools.Method(type, name, parameterTypes);
            if (DpaFloatMenuMethodDiscovery.HasMethodBody(method)
                && method.DeclaringType == type
                && method.IsStatic == isStatic
                && method.ReturnType == returnType
                && DpaFloatMenuMethodDiscovery.IteratorMoveNext(method) == null)
            {
                return method;
            }

            if (!warnedTargets)
            {
                warnedTargets = true;
                Log.Warning("[FMPM-DPA] An Overview target signature was not recognized ("
                    + type.FullName + "." + name + "). The unsupported target was skipped; "
                    + "Player.log monitoring remains active.");
            }

            return null;
        }
    }

    public static class DpaFloatMenuProvidersEntry
    {
        public static bool Active;

        public static IEnumerable<MethodInfo> GetPatchMethods()
        {
            return DpaFloatMenuMethodDiscovery.ProviderMethods();
        }

        public static string GetName(object __instance)
        {
            Type providerType = DpaFloatMenuMethodDiscovery.ProviderTypeForInstance(__instance);
            return providerType == null ? null : "FMPM.Provider." + providerType.AssemblyQualifiedName;
        }

        public static string GetLabel(object __instance)
        {
            Type providerType = DpaFloatMenuMethodDiscovery.ProviderTypeForInstance(__instance);
            return providerType == null ? "Unknown FloatMenuOptionProvider" : providerType.FullName;
        }
    }

    public static class DpaFloatMenuWorkGiversEntry
    {
        public static bool Active;

        public static IEnumerable<MethodInfo> GetPatchMethods()
        {
            return DpaFloatMenuMethodDiscovery.ValidMethods(
                AccessTools.Method(
                    typeof(FloatMenuOptionProvider_WorkGivers),
                    "GetWorkGiverOption"));
        }

        public static string GetName(WorkGiverDef workGiver)
        {
            if (workGiver == null)
            {
                return null;
            }

            string packageId = workGiver.modContentPack == null
                ? "unknown"
                : workGiver.modContentPack.PackageIdPlayerFacing;
            return "FMPM.WorkGiver."
                + (workGiver.defName ?? "unnamed")
                + "."
                + packageId;
        }

        public static string GetLabel(WorkGiverDef workGiver)
        {
            if (workGiver == null)
            {
                return "Unknown WorkGiver";
            }

            string label = workGiver.defName ?? workGiver.ToString();
            if (workGiver.modContentPack != null)
            {
                label += " — " + workGiver.modContentPack.Name;
            }

            return label;
        }
    }

    internal static class DpaFloatMenuMethodDiscovery
    {
        internal static IEnumerable<MethodInfo> ProviderMethods()
        {
            HashSet<MethodInfo> methods = new HashSet<MethodInfo>();
            foreach (Type type in GenTypes.AllTypes)
            {
                if (type == null
                    || type.IsAbstract
                    || !typeof(FloatMenuOptionProvider).IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo[] declaredMethods;
                try
                {
                    declaredMethods = type.GetMethods(
                        BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                for (int index = 0; index < declaredMethods.Length; index++)
                {
                    MethodInfo method = declaredMethods[index];
                    if ((method.Name != "GetOptions"
                            && method.Name != "GetOptionsFor")
                        || !HasMethodBody(method))
                    {
                        continue;
                    }

                    MethodInfo iteratorMoveNext = IteratorMoveNext(method);
                    if (iteratorMoveNext != null)
                    {
                        methods.Add(iteratorMoveNext);
                    }
                    else
                    {
                        methods.Add(method);
                    }
                }
            }

            return methods;
        }

        internal static IEnumerable<MethodInfo> ValidMethods(
            params MethodInfo[] methods)
        {
            return methods.Where(HasMethodBody).Distinct();
        }

        internal static void AddNamedMethods(
            HashSet<MethodInfo> destination,
            Type type,
            string methodName)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return;
            }

            for (int index = 0; index < methods.Length; index++)
            {
                MethodInfo method = methods[index];
                if (method.Name != methodName || !HasMethodBody(method))
                {
                    continue;
                }

                destination.Add(IteratorMoveNext(method) ?? method);
            }
        }

        internal static Type ProviderTypeForInstance(object instance)
        {
            if (instance == null)
            {
                return null;
            }

            Type type = instance.GetType();
            if (typeof(FloatMenuOptionProvider).IsAssignableFrom(type))
            {
                return type;
            }

            Type declaringType = type.DeclaringType;
            return declaringType != null
                && typeof(FloatMenuOptionProvider).IsAssignableFrom(declaringType)
                ? declaringType
                : type;
        }

        internal static MethodInfo IteratorMoveNext(MethodInfo method)
        {
            IteratorStateMachineAttribute attribute = method.GetCustomAttributes(
                    typeof(IteratorStateMachineAttribute),
                    inherit: false)
                .OfType<IteratorStateMachineAttribute>()
                .FirstOrDefault();
            MethodInfo moveNext = attribute == null
                ? null
                : AccessTools.Method(attribute.StateMachineType, "MoveNext");
            if (HasMethodBody(moveNext))
            {
                return moveNext;
            }

            Type declaringType = method.DeclaringType;
            if (declaringType == null)
            {
                return null;
            }

            Type[] nestedTypes;
            try
            {
                nestedTypes = declaringType.GetNestedTypes(
                    BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return null;
            }

            string generatedNamePrefix = "<" + method.Name + ">";
            for (int index = 0; index < nestedTypes.Length; index++)
            {
                Type nestedType = nestedTypes[index];
                if (nestedType.Name.IndexOf(
                        generatedNamePrefix,
                        StringComparison.Ordinal) < 0
                    || nestedType.GetCustomAttributes(
                            typeof(CompilerGeneratedAttribute),
                            inherit: false).Length == 0)
                {
                    continue;
                }

                moveNext = AccessTools.Method(nestedType, "MoveNext");
                if (HasMethodBody(moveNext))
                {
                    return moveNext;
                }
            }

            return null;
        }

        internal static bool HasMethodBody(MethodInfo method)
        {
            if (method == null
                || method.IsAbstract
                || method.IsGenericMethod
                || method.ContainsGenericParameters)
            {
                return false;
            }

            try
            {
                return method.GetMethodBody() != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
