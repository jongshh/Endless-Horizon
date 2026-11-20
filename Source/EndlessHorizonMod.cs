using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public class HorizonSettings : ModSettings
    {
        public bool enabled = true;
        public int ringWidth = 52;
        public int fogStart = 32;
        public bool fog = true;
        public bool clouds = true;
        public bool lighting = true;
        public bool drawWaterDepth = true;
        public bool useCoastNoise = true;
        public bool useGenerator = true;
        public bool things = true;
        public bool snow = true;
        private int settingsVersion = CurrentSettingsVersion;
        private const int CurrentSettingsVersion = 3;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 1);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref ringWidth, "ringWidth", 52);
            Scribe_Values.Look(ref fogStart, "fogStart", 32);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                settingsVersion = CurrentSettingsVersion;
            }
            Scribe_Values.Look(ref fog, "fog", true);
            Scribe_Values.Look(ref clouds, "clouds", true);
            Scribe_Values.Look(ref lighting, "lighting", true);
            Scribe_Values.Look(ref drawWaterDepth, "drawWaterDepth", true);
            Scribe_Values.Look(ref useCoastNoise, "useCoastNoise", true);
            Scribe_Values.Look(ref useGenerator, "useGenerator", true);
            Scribe_Values.Look(ref things, "things", true);
            Scribe_Values.Look(ref snow, "snow", true);
        }

        public void ResetToDefaults()
        {
            var d = new HorizonSettings();
            enabled = d.enabled;
            ringWidth = d.ringWidth;
            fogStart = d.fogStart;
            fog = d.fog;
            clouds = d.clouds;
            lighting = d.lighting;
            drawWaterDepth = d.drawWaterDepth;
            useCoastNoise = d.useCoastNoise;
            useGenerator = d.useGenerator;
            things = d.things;
            snow = d.snow;
        }

        public void Sanitize()
        {
            ringWidth = Mathf.Clamp(ringWidth, 8, 96);
            fogStart = Mathf.Clamp(fogStart, 0, ringWidth - 4);
        }
    }

    public class EndlessHorizonMod : Mod
    {
        public const string Version = "1.0.0.3";
        public const string LogPrefix = "[Endless Horizon] ";

        public static HorizonSettings Settings;

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
            CompatRegistry.Init(harmony);
            string patched = string.Join(", ", harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name));
            Log.Message($"{LogPrefix}{Version}{(IsDevBuild ? " (development build)" : "")} loaded. Patched: {patched}.{(IsDevBuild ? " Probe mode: " + ProbeMode + "." : "")}");
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
            foreach (CompatEntry e in CompatRegistry.Entries)
            {
                string state = ("EH_Compat_" + e.State).Translate();
                GUI.color = e.State == CompatState.Detected ? new Color(0.6f, 0.9f, 0.6f)
                    : e.State == CompatState.NotInstalled ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.95f, 0.75f, 0.45f);
                list.Label(e.Name + " — " + state, -1f, e.Reason.Length > 0 ? (TipSignal?)e.Reason : null);
                GUI.color = Color.white;
            }
            Note("EH_Compat_Note");

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
        }
    }
}
