using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal class HorizonSettings : ModSettings
    {
        internal static HorizonSettings Current;

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
        public System.Collections.Generic.List<string> restored = new System.Collections.Generic.List<string>();

        internal bool Restored(string id) => restored != null && restored.Contains(id);

        internal void SetRestored(string id, bool on)
        {
            restored ??= new System.Collections.Generic.List<string>();
            if (!on) restored.Remove(id);
            else if (!restored.Contains(id)) restored.Add(id);
        }
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
            Scribe_Collections.Look(ref restored, "restored", LookMode.Value);
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
            restored = d.restored;
        }

        public void Sanitize()
        {
            ringWidth = Mathf.Clamp(ringWidth, 8, 96);
            fogStart = Mathf.Clamp(fogStart, 0, ringWidth - 4);
        }
    }
}
