using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal class EndlessHorizonMod : Mod
    {
        public const string Version = "1.0.0.4";
        public const string LogPrefix = HorizonLog.Prefix;

        public static HorizonSettings Settings
        {
            get => HorizonSettings.Current;
            set => HorizonSettings.Current = value;
        }

#if EH_DEV
        public static readonly bool IsDevBuild = true;
        public static readonly bool ProbeMode = Environment.GetEnvironmentVariable("ENDLESS_HORIZON_PROBE") == "1";
#else
        public static readonly bool IsDevBuild = false;
        public static readonly bool ProbeMode = false;
#endif

        public EndlessHorizonMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<HorizonSettings>();
            Settings.Sanitize();
            var harmony = new Harmony("jongsh.endlesshorizon");
            harmony.PatchAll(typeof(EndlessHorizonMod).Assembly);
            HorizonDiagnostics.Init(harmony);
            HorizonRegistry.ConnectApi();
            IntegrationList.Init(harmony);
            HorizonAssets.FogShaderLoader = () => LoadShader(content, "ehfog.shader");
            string patched = string.Join(", ", harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name));
            Log.Message($"{LogPrefix}{Version}{(IsDevBuild ? " (development build)" : "")} loaded. Patched: {patched}.{(IsDevBuild ? " Probe mode: " + ProbeMode + "." : "")}");
        }

        private static Shader LoadShader(ModContentPack content, string file)
        {
            var bundles = content?.assetBundles?.loadedAssetBundles;
            if (bundles == null) return null;
            foreach (AssetBundle bundle in bundles)
            {
                if (bundle == null) continue;
                foreach (string name in bundle.GetAllAssetNames())
                {
                    if (!name.EndsWith("/" + file, System.StringComparison.OrdinalIgnoreCase)) continue;
                    Shader shader = bundle.LoadAsset<Shader>(name);
                    if (shader != null) return shader;
                }
            }
            return null;
        }

        public override string SettingsCategory() => "Endless Horizon";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            HorizonSettings st = Settings;
            var viewRect = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(settingsHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref settingsScroll, viewRect);
            var list = new Listing_Standard();
            list.Begin(viewRect);

            void Header(string key)
            {
                list.Gap(10f);
                Text.Font = GameFont.Medium;
                list.Label(key.Translate(), -1f, (TipSignal?)null);
                Text.Font = GameFont.Small;
                list.GapLine(6f);
            }
            void Note(string key)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.72f, 0.72f, 0.72f);
                list.Label(key.Translate(), -1f, (TipSignal?)null);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                list.Gap(4f);
            }
            void Check(string key, ref bool value)
            {
                list.CheckboxLabeled(key.Translate(), ref value, (key + "_Desc").Translate(), 0f, 1f);
                Note(key + "_Desc");
            }

            Header("EH_Section_Outside");
            Check("EH_Enabled", ref st.enabled);
            st.ringWidth = (int)list.SliderLabeled("EH_RingWidth".Translate(st.ringWidth), st.ringWidth, 8f, 96f, 0.6f, null);
            Note("EH_RingWidth_Desc");

            Header("EH_Section_Fog");
            Check("EH_Fog", ref st.fog);
            st.fogStart = (int)list.SliderLabeled("EH_FogStart".Translate(st.fogStart), st.fogStart, 0f, st.ringWidth - 4, 0.6f, null);
            Note("EH_FogStart_Desc");
            Check("EH_Clouds", ref st.clouds);

            Header("EH_Section_Drawn");
            Check("EH_Generator", ref st.useGenerator);
            Check("EH_Things", ref st.things);
            Check("EH_Snow", ref st.snow);
            Check("EH_Lighting", ref st.lighting);
            Check("EH_Coast", ref st.useCoastNoise);
            Check("EH_WaterDepth", ref st.drawWaterDepth);

            Header("EH_Section_Compat");
            foreach (IntegrationEntry e in IntegrationList.Entries)
            {
                string state = ("EH_Compat_" + e.State).Translate();
                GUI.color = e.State == CompatState.Detected ? new Color(0.6f, 0.9f, 0.6f)
                    : e.State == CompatState.NotInstalled ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.95f, 0.75f, 0.45f);
                list.Label(e.Name + " — " + state, -1f, e.Detail.Length > 0 ? (TipSignal?)e.Detail : null);
                GUI.color = Color.white;
            }
            Note("EH_Compat_Note");
            bool anyReplaced = false;
            foreach (System.Collections.Generic.KeyValuePair<string, string> replaced in ExtensionRegistry.Replaced)
            {
                if (!anyReplaced) Header("EH_Section_Replaced");
                anyReplaced = true;
                bool taken = !st.Restored(replaced.Key), was = taken;
                list.CheckboxLabeled("EH_Replaced".Translate(replaced.Key, replaced.Value), ref taken, "EH_Replaced_Desc".Translate(), 0f, 1f);
                if (taken != was) st.SetRestored(replaced.Key, !taken);
            }
            if (anyReplaced) Note("EH_Replaced_Desc");

            list.Gap(12f);
            Rect row = list.GetRect(30f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, row.width * 0.3f, row.height), "EH_Reset".Translate()))
            {
                st.ResetToDefaults();
            }
            if (Widgets.ButtonText(new Rect(row.x + row.width * 0.32f, row.y, row.width * 0.3f, row.height), "EH_Diagnostics_Copy".Translate()))
            {
                string report;
                try
                {
                    report = HorizonDiagnostics.Collect();
                }
                catch (Exception ex)
                {
                    report = "Endless Horizon diagnostics failed: " + ex;
                }
                GUIUtility.systemCopyBuffer = report;
                Messages.Message("EH_Diagnostics_Copied".Translate(), RimWorld.MessageTypeDefOf.NeutralEvent, false);
            }
            list.Gap(4f);
            Note("EH_Diagnostics_Note");
            Note("EH_ApplyNote");
            if (IsDevBuild)
            {
                Note("EH_DevNote");
            }
            settingsHeight = list.CurHeight + 12f;
            list.End();
            Widgets.EndScrollView();
        }

        private static Vector2 settingsScroll;
        private static float settingsHeight = 1200f;

        public override void WriteSettings()
        {
            Settings.Sanitize();
            base.WriteSettings();
            HorizonRegistry.InvalidateAll();
            if (Current.ProgramState != ProgramState.Playing) return;
            foreach (Map map in Find.Maps)
                ExtensionMaps.Publish(map, Vocabulary.SettingsChanged, HorizonDomain.Of(map, Settings.ringWidth).Bounds);
        }
    }
}
