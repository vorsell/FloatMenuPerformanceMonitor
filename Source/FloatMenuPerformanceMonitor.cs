using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FloatMenuPerformanceMonitor
{
    [StaticConstructorOnStartup]
    public static class FloatMenuPerformanceMonitorStartup
    {
        static FloatMenuPerformanceMonitorStartup()
        {
            try
            {
                Harmony harmony = new Harmony("vorsel.floatmenuprocessmonitor");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                FloatMenuProviderInstrumenter.PatchProviders(harmony);
                DpaIntegration.Initialize();
                Log.Message("[FMPM] Float Menu Performance Monitor initialized. Search Player.log for [FMPM-SUMMARY] and [FMPM-SAMPLE].");
            }
            catch (Exception exception)
            {
                Log.Error("[FMPM-ERROR] Initialization failed: " + exception);
            }
        }
    }

    internal sealed class TimedEntry<TKey>
    {
        internal long Calls;
        internal long Ticks;
        internal TKey Key;
        internal readonly Dictionary<string, long> Targets = new Dictionary<string, long>();
    }

    internal static class FloatMenuMonitorState
    {
        private const int SampleIntervalSeconds = 3;
        private const int SummaryIntervalSeconds = 5;

        [ThreadStatic]
        private static bool sampling;

        private static readonly Dictionary<MethodBase, TimedEntry<MethodBase>> ProviderEntries =
            new Dictionary<MethodBase, TimedEntry<MethodBase>>();
        private static readonly Dictionary<WorkGiverDef, TimedEntry<WorkGiverDef>> WorkGiverEntries =
            new Dictionary<WorkGiverDef, TimedEntry<WorkGiverDef>>();

        private static long nextSampleAt;
        private static long summaryStartedAt = Stopwatch.GetTimestamp();
        private static long nextSummaryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * SummaryIntervalSeconds;
        private static long guiCalls;
        private static long guiTicks;
        private static long sampleCount;
        private static string sampledPawn = "none";

        internal static long BeginWindow()
        {
            return Stopwatch.GetTimestamp();
        }

        internal static void EndWindow(long startedAt)
        {
            if (startedAt == 0)
            {
                return;
            }

            guiCalls++;
            guiTicks += Stopwatch.GetTimestamp() - startedAt;
            long now = Stopwatch.GetTimestamp();
            if (now < nextSummaryAt)
            {
                return;
            }

            double seconds = TicksToMilliseconds(now - summaryStartedAt) / 1000.0;
            double totalMs = TicksToMilliseconds(guiTicks);
            double averageMs = guiCalls == 0 ? 0.0 : totalMs / guiCalls;
            Log.Message("[FMPM-SUMMARY] seconds=" + seconds.ToString("F2")
                + " guiCalls=" + guiCalls
                + " guiMs=" + totalMs.ToString("F2")
                + " avgGuiMs=" + averageMs.ToString("F3")
                + " samples=" + sampleCount);
            summaryStartedAt = now;
            nextSummaryAt = now + Stopwatch.Frequency * SummaryIntervalSeconds;
            guiCalls = 0;
            guiTicks = 0;
            sampleCount = 0;
        }

        internal static long BeginOptionsSample()
        {
            if (sampling)
            {
                return 0;
            }

            long now = Stopwatch.GetTimestamp();
            if (now < nextSampleAt)
            {
                return 0;
            }

            nextSampleAt = now + Stopwatch.Frequency * SampleIntervalSeconds;
            ProviderEntries.Clear();
            WorkGiverEntries.Clear();
            sampledPawn = SelectedPawnLabel();
            sampling = true;
            return now;
        }

        internal static void EndOptionsSample(long startedAt)
        {
            if (startedAt == 0 || !sampling)
            {
                return;
            }

            sampling = false;
            sampleCount++;
            double totalMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - startedAt);
            string providers = FormatProviders();
            string workGivers = FormatWorkGivers();
            Log.Message("[FMPM-SAMPLE] pawn=" + sampledPawn
                + " totalMs=" + totalMs.ToString("F3")
                + " providerTop=" + providers
                + " workGiverTop=" + workGivers);
        }

        internal static void AbortOptionsSample(long startedAt)
        {
            // A nested window that did not start this sample must not abort the
            // outer window's active sample.
            if (startedAt == 0 || !sampling)
            {
                return;
            }

            sampling = false;
            ProviderEntries.Clear();
            WorkGiverEntries.Clear();
            sampledPawn = "none";
        }

        internal static long BeginNestedTiming()
        {
            return sampling ? Stopwatch.GetTimestamp() : 0;
        }

        internal static void RecordProvider(MethodBase method, long startedAt)
        {
            if (startedAt == 0 || !sampling || method == null)
            {
                return;
            }

            TimedEntry<MethodBase> entry;
            if (!ProviderEntries.TryGetValue(method, out entry))
            {
                entry = new TimedEntry<MethodBase> { Key = method };
                ProviderEntries.Add(method, entry);
            }

            entry.Calls++;
            entry.Ticks += Stopwatch.GetTimestamp() - startedAt;
        }

        internal static void RecordWorkGiver(WorkGiverDef workGiver, LocalTargetInfo target, long startedAt)
        {
            if (startedAt == 0 || !sampling || workGiver == null)
            {
                return;
            }

            TimedEntry<WorkGiverDef> entry;
            if (!WorkGiverEntries.TryGetValue(workGiver, out entry))
            {
                entry = new TimedEntry<WorkGiverDef> { Key = workGiver };
                WorkGiverEntries.Add(workGiver, entry);
            }

            entry.Calls++;
            entry.Ticks += Stopwatch.GetTimestamp() - startedAt;

            string targetLabel = TargetLabel(target);
            long targetCalls;
            entry.Targets.TryGetValue(targetLabel, out targetCalls);
            entry.Targets[targetLabel] = targetCalls + 1;
        }

        private static string FormatProviders()
        {
            if (ProviderEntries.Count == 0)
            {
                return "none";
            }

            return string.Join(" | ", ProviderEntries.Values
                .OrderByDescending(entry => entry.Ticks)
                .Take(10)
                .Select(entry => MethodLabel(entry.Key)
                    + ":calls=" + entry.Calls
                    + ",ms=" + TicksToMilliseconds(entry.Ticks).ToString("F3")
                    + ",mod=" + ModAttribution.ForAssembly(entry.Key.DeclaringType.Assembly)));
        }

        private static string FormatWorkGivers()
        {
            if (WorkGiverEntries.Count == 0)
            {
                return "none";
            }

            return string.Join(" | ", WorkGiverEntries.Values
                .OrderByDescending(entry => entry.Ticks)
                .Take(15)
                .Select(entry => (entry.Key.defName ?? entry.Key.ToString())
                    + ":calls=" + entry.Calls
                    + ",ms=" + TicksToMilliseconds(entry.Ticks).ToString("F3")
                    + ",mod=" + ModAttribution.ForWorkGiver(entry.Key)
                    + ",targets=" + FormatTargets(entry.Targets)));
        }

        private static string FormatTargets(Dictionary<string, long> targets)
        {
            if (targets.Count == 0)
            {
                return "none";
            }

            return "[" + string.Join("; ", targets
                .OrderByDescending(pair => pair.Value)
                .Take(8)
                .Select(pair => pair.Key + " x" + pair.Value)) + "]";
        }

        private static string TargetLabel(LocalTargetInfo target)
        {
            Thing thing = target.Thing;
            if (thing == null)
            {
                return "cell-target";
            }

            return "def=" + (thing.def == null ? "null" : thing.def.defName)
                + ",type=" + thing.GetType().FullName;
        }

        private static string MethodLabel(MethodBase method)
        {
            return method.DeclaringType.FullName + "." + method.Name;
        }

        private static string SelectedPawnLabel()
        {
            List<Pawn> selected = Find.Selector == null ? null : Find.Selector.SelectedPawns;
            if (selected == null || selected.Count == 0)
            {
                return "none";
            }

            Pawn pawn = selected[0];
            return (pawn.def == null ? "null" : pawn.def.defName)
                + "/"
                + (pawn.mutant == null || pawn.mutant.Def == null ? "none" : pawn.mutant.Def.defName);
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return (double)ticks * 1000.0 / Stopwatch.Frequency;
        }
    }

    internal static class ModAttribution
    {
        private static readonly Dictionary<Assembly, string> AssemblyOwners =
            new Dictionary<Assembly, string>();

        internal static string ForWorkGiver(WorkGiverDef workGiver)
        {
            if (workGiver == null)
            {
                return "unknown";
            }

            if (workGiver.modContentPack != null)
            {
                return ModLabel(workGiver.modContentPack);
            }

            return "unknown";
        }

        internal static string ForAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                return "unknown";
            }

            string owner;
            if (AssemblyOwners.TryGetValue(assembly, out owner))
            {
                return owner;
            }

            owner = assembly.GetName().Name;
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int modIndex = 0; modIndex < mods.Count; modIndex++)
            {
                ModContentPack mod = mods[modIndex];
                if (mod.assemblies == null || mod.assemblies.loadedAssemblies == null)
                {
                    continue;
                }

                if (mod.assemblies.loadedAssemblies.Contains(assembly))
                {
                    owner = ModLabel(mod);
                    break;
                }
            }

            AssemblyOwners[assembly] = owner;
            return owner;
        }

        private static string ModLabel(ModContentPack mod)
        {
            return mod.Name + " [" + mod.PackageIdPlayerFacing + "]";
        }
    }

    internal struct FloatMenuWindowTimingState
    {
        internal long WindowStartedAt;
        internal long SampleStartedAt;
    }

    [HarmonyPatch(typeof(FloatMenuMap), "DoWindowContents")]
    internal static class FloatMenuWindowMonitorPatch
    {
        private static void Prefix(out FloatMenuWindowTimingState __state)
        {
            __state = new FloatMenuWindowTimingState
            {
                WindowStartedAt = FloatMenuMonitorState.BeginWindow(),
                SampleStartedAt = FloatMenuMonitorState.BeginOptionsSample()
            };
        }

        private static void Postfix(FloatMenuWindowTimingState __state)
        {
            FloatMenuMonitorState.EndOptionsSample(__state.SampleStartedAt);
            FloatMenuMonitorState.EndWindow(__state.WindowStartedAt);
        }

        private static Exception Finalizer(
            Exception __exception,
            FloatMenuWindowTimingState __state)
        {
            if (__exception != null)
            {
                FloatMenuMonitorState.AbortOptionsSample(
                    __state.SampleStartedAt);
            }

            // Preserve the original exception; this monitor only cleans up its
            // own sampling state and must not hide failures from other code.
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class WorkGiverOptionMonitorPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(FloatMenuOptionProvider_WorkGivers), "GetWorkGiverOption");
        }

        private static void Prefix(out long __state)
        {
            __state = FloatMenuMonitorState.BeginNestedTiming();
        }

        private static void Postfix(WorkGiverDef workGiver, LocalTargetInfo target, long __state)
        {
            FloatMenuMonitorState.RecordWorkGiver(workGiver, target, __state);
        }
    }

    internal static class FloatMenuProviderInstrumenter
    {
        private static readonly Dictionary<MethodBase, MethodBase> ProviderOrigins =
            new Dictionary<MethodBase, MethodBase>();

        internal static void PatchProviders(Harmony harmony)
        {
            MethodInfo prefix = AccessTools.Method(typeof(FloatMenuProviderInstrumenter), "Prefix");
            MethodInfo postfix = AccessTools.Method(typeof(FloatMenuProviderInstrumenter), "Postfix");
            int patched = 0;
            int iteratorStateMachines = 0;

            foreach (Type type in GenTypes.AllTypes)
            {
                if (type == null
                    || type.IsAbstract
                    || !typeof(FloatMenuOptionProvider).IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    MethodInfo method = methods[methodIndex];
                    if (method.Name != "GetOptionsFor"
                        || !DpaFloatMenuMethodDiscovery.HasMethodBody(method))
                    {
                        continue;
                    }

                    MethodInfo iteratorMoveNext = DpaFloatMenuMethodDiscovery.IteratorMoveNext(method);
                    MethodBase targetMethod = iteratorMoveNext ?? method;
                    if (ProviderOrigins.ContainsKey(targetMethod))
                    {
                        continue;
                    }

                    ProviderOrigins.Add(targetMethod, method);
                    harmony.Patch(targetMethod, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                    patched++;
                    if (iteratorMoveNext != null)
                    {
                        iteratorStateMachines++;
                    }
                }
            }

            Log.Message(
                "[FMPM] Patched " + patched
                + " FloatMenuOptionProvider.GetOptionsFor execution paths ("
                + iteratorStateMachines + " iterator state machines).");
        }

        public static void Prefix(out long __state)
        {
            __state = FloatMenuMonitorState.BeginNestedTiming();
        }

        public static void Postfix(MethodBase __originalMethod, long __state)
        {
            MethodBase providerMethod;
            if (!ProviderOrigins.TryGetValue(__originalMethod, out providerMethod))
            {
                providerMethod = __originalMethod;
            }

            FloatMenuMonitorState.RecordProvider(providerMethod, __state);
        }
    }
}
