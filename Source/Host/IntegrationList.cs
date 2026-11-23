using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    internal enum CompatState
    {
        NotInstalled,
        Detected,
        Unrecognized,
        Failed,
        Replaced,
    }

    internal sealed class IntegrationEntry
    {
        internal string Name;
        internal string Owner;
        internal string PackageId;
        internal bool Installed;
        internal string Reason = "";

        internal CompatState State
        {
            get
            {
                string state = ExtensionRegistry.StateOf(Owner);
                if (state == "absent") return CompatState.NotInstalled;
                if (state == "unrecognized") return CompatState.Unrecognized;
                if (state == "failed") return CompatState.Failed;
                return state.StartsWith("replaced:", StringComparison.Ordinal) ? CompatState.Replaced : CompatState.Detected;
            }
        }

        internal string Detail => ExtensionRegistry.FailureOf(Owner)
            ?? (State == CompatState.Replaced ? ExtensionRegistry.StateOf(Owner) : Reason);
    }

    internal static class IntegrationList
    {
        internal static readonly List<IntegrationEntry> Entries = new List<IntegrationEntry>();

        internal static void Init(Harmony harmony)
        {
            Add("RimWorld: Waves", "waves", "patrickschwarzer.waves", null, () => Compat_Waves.Enable(harmony));
            Add("RimWorld: Living Waters", "livingwaters", "patrickschwarzer.rivers", null, () => Compat_LivingWaters.Enable(harmony));
            Add("Quiet Wonders", "quietwonders", "choco.quietwonders", null, () => Compat_QuietWonders.Enable(harmony), () => Compat_QuietWonders.Detail);
            Add("Weather Reimagined", "weatherreimagined", null, "LivingWeather.Freiflaeche", Compat_WeatherReimagined.Enable);
            Add("Realistic Rain", "realisticrain", null, "RealisticRain.ReflectionCapture", Compat_RealisticRain.Enable);
        }

        private static void Add(string name, string owner, string packageId, string typeName, Func<string> enable, Func<string> detail = null)
        {
            var entry = new IntegrationEntry { Name = name, Owner = owner, PackageId = packageId };
            Entries.Add(entry);
            entry.Installed = packageId != null ? ModsConfig.IsActive(packageId) : GenTypes.GetTypeInAnyAssembly(typeName) != null;
            if (!entry.Installed)
            {
                ExtensionRegistry.SetBuiltIn(owner, "absent");
                return;
            }
            string state;
            string before = ExtensionRegistry.SettingUp;
            try
            {
                ExtensionRegistry.SettingUp = owner;
                string reason = enable();
                state = reason == null ? "active" : "unrecognized";
                entry.Reason = reason ?? (detail?.Invoke() ?? "");
            }
            catch (Exception ex)
            {
                state = "unrecognized";
                entry.Reason = ex.GetType().Name;
                Log.Error(HorizonLog.Prefix + "Compatibility with " + name + " could not be set up and stays off. " + ex);
            }
            finally { ExtensionRegistry.SettingUp = before; }
            ExtensionRegistry.SetBuiltIn(owner, state);
            Log.Message(HorizonLog.Prefix + "compat " + name + ": " + entry.State + (entry.Reason.Length > 0 ? " (" + entry.Reason + ")" : ""));
        }

        internal static IntegrationEntry Find(string owner) => Entries.Find(e => e.Owner == owner);
    }
}
