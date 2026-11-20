using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class OpenSkyMesh
    {
        public static Mesh Build(Map map, HorizonField field, MountainMask mountains)
        {
            int w = map.Size.x, h = map.Size.z, r = field.RingWidth;
            float far = FrameMesh.Far;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            void Quad(float x0, float z0, float x1, float z1)
            {
                int i = verts.Count;
                verts.Add(new Vector3(x0, 0f, z0)); uvs.Add(new Vector2(x0, z0));
                verts.Add(new Vector3(x0, 0f, z1)); uvs.Add(new Vector2(x0, z1));
                verts.Add(new Vector3(x1, 0f, z1)); uvs.Add(new Vector2(x1, z1));
                verts.Add(new Vector3(x1, 0f, z0)); uvs.Add(new Vector2(x1, z0));
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            bool Open(int x, int z)
            {
                if (x >= 0 && z >= 0 && x < w && z < h) return false;
                var c = new IntVec3(x, 0, z);
                if (mountains != null && mountains.IsRock(x, z)) return false;
                if (HorizonThings.OutsideFogged(map, field, c)) return false;
                int s = field.RawSampleAt(c);
                if (s >= 0)
                {
                    RoofDef roof = map.roofGrid.RoofAt(s);
                    if (roof != null && roof.isNatural) return false;
                }
                return true;
            }
            for (int z = -r; z < h + r; z++)
            {
                int run = int.MinValue;
                for (int x = -r; x <= w + r; x++)
                {
                    bool open = x < w + r && Open(x, z);
                    if (open && run == int.MinValue) run = x;
                    if (!open && run != int.MinValue)
                    {
                        Quad(run, z, x, z + 1);
                        run = int.MinValue;
                    }
                }
            }
            Quad(-far, -far, w + far, -r);
            Quad(-far, h + r, w + far, h + far);
            Quad(-far, -r, -r, h + r);
            Quad(w + r, -r, w + far, h + r);
            var mesh = new Mesh { name = "EndlessHorizon_OpenSky" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(new Vector3(w / 2f, 0f, h / 2f), new Vector3(w + 2 * far, 0.02f, h + 2 * far));
            return mesh;
        }
    }

    [HarmonyPatch]
    public static class Compat_WeatherReimagined
    {
        private static readonly Type Freiflaeche = AccessTools.TypeByName("LivingWeather.Freiflaeche");
        private static readonly int Layer = LayerMask.NameToLayer("GravshipExclude");
        public static bool Active;
        public static bool Installed => Freiflaeche != null;

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Freiflaeche == null ? null : AccessTools.Method(Freiflaeche, "Zeichnen");

        public static void Postfix(Map __0, Matrix4x4 __1, Material __2)
        {
            Active = true;
            try
            {
                if (__2 == null || !HorizonRegistry.IsActive(__0)) return;
                HorizonState s = HorizonRegistry.Peek(__0);
                Mesh sky = s?.OpenSky;
                if (sky != null)
                {
                    Graphics.DrawMesh(sky, __1, __2, Layer < 0 ? 0 : Layer);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(EndlessHorizonMod.LogPrefix + "Weather Reimagined overlay outside the map failed: " + ex, 0x45485752);
            }
        }
    }
}
