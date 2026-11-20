using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers))]
    public static class Patch_DrawClippers
    {
        private static bool failed;

        public static bool Prefix(Map map)
        {
            if (failed || !HorizonRegistry.IsActive(map))
            {
                return true;
            }
            try
            {
                HorizonState state = HorizonRegistry.Get(map);
                HorizonProbe.OnFrame(map, state);
                if (HorizonDebug.HideRing)
                {
                    PerfMeter.Frame(0);
                    return true;
                }
                PerfMeter.Watch.Restart();
                state.Draw();
                PerfMeter.Watch.Stop();
                PerfMeter.Frame(PerfMeter.Watch.Elapsed.TotalMilliseconds);
                if (!(EndlessHorizonMod.Settings.fog && !HorizonDebug.HideFog))
                {
                    ShiftedClippers.Draw(map, state.RingWidth);
                }
                return false;
            }
            catch (Exception ex)
            {
                failed = true;
                Log.Error(EndlessHorizonMod.LogPrefix + "Drawing failed; ring disabled for this session.\n" + ex);
                return true;
            }
        }
    }

    public static class ShiftedClippers
    {
        private const float ClipSize = 500f;
        private static readonly MaterialPropertyBlock vertBlock = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock horBlock = new MaterialPropertyBlock();

        public static void Draw(Map map, int offset, Material overrideMat = null)
        {
            if (!map.DrawMapClippers)
            {
                return;
            }
            Material mat = overrideMat ?? map.MapEdgeMaterial;
            IntVec3 size = map.Size;
            float alt = AltitudeLayer.WorldClipper.AltitudeFor();
            float half = ClipSize / 2f;

            var scale = new Vector3(ClipSize, 1f, size.z + 2f * offset);
            DrawPlane(mat, horBlock, scale, new Vector3(-offset - half, 0f, size.z / 2f), alt);
            DrawPlane(mat, horBlock, scale, new Vector3(size.x + offset + half, 0f, size.z / 2f), alt);

            scale = new Vector3(size.x + 2f * offset + 2f * ClipSize, 1f, ClipSize);
            DrawPlane(mat, vertBlock, scale, new Vector3(size.x / 2f, 0f, size.z + offset + half), alt);
            DrawPlane(mat, vertBlock, scale, new Vector3(size.x / 2f, 0f, -offset - half), alt);
        }

        private static void DrawPlane(Material mat, MaterialPropertyBlock block, Vector3 scale, Vector3 center, float alt)
        {
            block.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
            block.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
            Matrix4x4 matrix = default;
            matrix.SetTRS(center.WithYOffset(alt), Quaternion.identity, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0, null, 0, block);
        }
    }

    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class Patch_CaptureCoastNoise
    {
        public static void Postfix(Map __result)
        {
            if (__result == null)
            {
                return;
            }
            try
            {
                CoastNoise.CaptureAfterGeneration(__result);
            }
            catch (Exception ex)
            {
                Log.Warning(EndlessHorizonMod.LogPrefix + "Coast noise capture failed: " + ex);
            }
        }
    }
}
