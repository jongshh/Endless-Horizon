using System.Diagnostics;
#if EH_DEV
using System.Text;
using LudeonTK;
using Verse;
#endif

namespace EndlessHorizon
{
    internal static class DrawCost
    {
        internal const int Get = 0, Before = 1, Terrain = 2, Depth = 3, AfterTerrain = 4, Advance = 5, Layers = 6, Work = 7, Retire = 8,
            Things = 9, Coast = 10, BeforeLighting = 11, Lighting = 12, BeforeFog = 13, FogUpdate = 14, FogDraw = 15, After = 16;
#if EH_DEV
        private static readonly string[] Names =
        {
            "registry", "stage.before", "terrain", "depth", "stage.afterTerrain", "ext.advance", "ext.layers", "work", "retire",
            "things", "coastSearch", "stage.beforeLighting", "lighting", "stage.beforeFog", "fog.update", "fog.draw", "stage.after",
        };
        private static readonly double[] sum = new double[Names.Length], max = new double[Names.Length];
        private static readonly double[] atWorst = new double[Names.Length], current = new double[Names.Length];
        private static long last;
        private static int frames, over1, over2, over5;
        private static double total, worst;
        private static float since;
        private static int sinceFrame;
#endif

        [Conditional("EH_DEV")]
        internal static void Begin()
        {
#if EH_DEV
            System.Array.Clear(current, 0, current.Length);
            last = Stopwatch.GetTimestamp();
#endif
        }

        [Conditional("EH_DEV")]
        internal static void Lap(int section)
        {
#if EH_DEV
            long now = Stopwatch.GetTimestamp();
            double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
            last = now;
            current[section] += ms;
#endif
        }

        [Conditional("EH_DEV")]
        internal static void End()
        {
#if EH_DEV
            double frame = 0;
            for (int i = 0; i < current.Length; i++)
            {
                frame += current[i];
                sum[i] += current[i];
                if (current[i] > max[i]) max[i] = current[i];
            }
            frames++;
            total += frame;
            if (frame > 1) over1++;
            if (frame > 2) over2++;
            if (frame > 5) over5++;
            if (frame > worst)
            {
                worst = frame;
                System.Array.Copy(current, atWorst, current.Length);
            }
#endif
        }

#if EH_DEV
        [DebugAction("Endless Horizon", "Draw cost: mark", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Mark()
        {
            float seconds = UnityEngine.Time.realtimeSinceStartup - since;
            var b = new StringBuilder();
            b.Append($"{HorizonLog.Prefix}EH_DRAWCOST seconds={seconds:0.00} frames={UnityEngine.Time.frameCount - sinceFrame} draws={frames} ");
            b.Append($"avgMs={(frames > 0 ? total / frames : 0):0.000} worstMs={worst:0.000} over1ms={over1} over2ms={over2} over5ms={over5} |");
            for (int i = 0; i < Names.Length; i++)
                b.Append($" {Names[i]}={(frames > 0 ? sum[i] / frames : 0):0.000}/{max[i]:0.00}/{atWorst[i]:0.00}");
            Log.Message(b.ToString());
            System.Array.Clear(sum, 0, sum.Length);
            System.Array.Clear(max, 0, max.Length);
            System.Array.Clear(atWorst, 0, atWorst.Length);
            frames = over1 = over2 = over5 = 0;
            total = worst = 0;
            since = UnityEngine.Time.realtimeSinceStartup;
            sinceFrame = UnityEngine.Time.frameCount;
        }
#endif
    }
}
