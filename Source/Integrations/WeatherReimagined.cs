using System;
using System.Collections.Generic;
using System.Reflection;
using EndlessHorizon.Api;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class OpenSkyMesh
    {
        private const float Far = 2000f;

        internal static IEnumerable<bool> BuildSteps(Map map, Action<Mesh> made)
        {
            FieldShape shape = FieldShape.Of(map);
            int w = shape.W, h = shape.H, r = shape.Ring;
            float far = Far;
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
            var row = new float[shape.Bounds.Width];
            for (int z = -r; z < h + r; z++)
            {
                if (!HorizonWorld.Copy(map, "weather.exposed", new CellRect(-r, z, row.Length, 1), row))
                    throw new InvalidOperationException("weather.exposed could not be read");
                int run = int.MinValue;
                for (int x = -r; x <= w + r; x++)
                {
                    bool open = x < w + r && row[x + r] == 1f && HorizonSpace.IsOutside(map, new IntVec3(x, 0, z));
                    if (open && run == int.MinValue) run = x;
                    if (!open && run != int.MinValue)
                    {
                        Quad(run, z, x, z + 1);
                        run = int.MinValue;
                    }
                }
                yield return false;
            }
            bool Reaches(int x, int z) => HorizonSpace.IsOutside(map, new IntVec3(x, 0, z));
            bool unlimited = Reaches(-r, -r) && Reaches(w + r - 1, -r) && Reaches(-r, h + r - 1) && Reaches(w + r - 1, h + r - 1);
            if (unlimited)
            {
                Quad(-far, -far, w + far, -r);
                Quad(-far, h + r, w + far, h + far);
                Quad(-far, -r, -r, h + r);
                Quad(w + r, -r, w + far, h + r);
            }
            var mesh = new Mesh { name = "EndlessHorizon_OpenSky" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(new Vector3(w / 2f, 0f, h / 2f), new Vector3(w + 2 * far, 0.02f, h + 2 * far));
            made(mesh);
        }
    }

    [HarmonyPatch]
    internal static class Compat_WeatherReimagined
    {
        private const string Owner = "weatherreimagined", Step = Owner + ".sky";
        private static readonly Type Freiflaeche = AccessTools.TypeByName("LivingWeather.Freiflaeche");
        private static readonly int Layer = LayerMask.NameToLayer("GravshipExclude");
        public static bool Active;
        private static bool on;
        private static readonly StepResult<Mesh> sky = new StepResult<Mesh>(mesh => UnityEngine.Object.Destroy(mesh));

        public static bool Installed => Freiflaeche != null;

        public static string Enable()
        {
            if (TargetMethod() == null) return "LivingWeather.Freiflaeche.Zeichnen missing";
            if (!HorizonExtensions.RegisterBuildStep(Owner, Step, map => OpenSkyMesh.BuildSteps(map, mesh => sky.Made(map, mesh)), new string[0]))
                return "registration refused";
            HorizonInfo.Observe(Owner, (map, id, area) =>
            {
                if (id == Step) sky.Shown(map);
                else if (id == "disposed") sky.Disposed(map);
            });
            on = true;
            return null;
        }

        internal static Mesh SkyOf(Map map) => sky.Current(map);

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Freiflaeche == null ? null : AccessTools.Method(Freiflaeche, "Zeichnen");

        public static void Postfix(Map __0, Matrix4x4 __1, Material __2)
        {
            Active = true;
            try
            {
                if (!on || __2 == null) return;
                Mesh mesh = sky.Current(__0);
                if (mesh == null || !HorizonInfo.IsActive(__0) || HorizonExtensions.StateOf(Owner) != "active") return;
                Graphics.DrawMesh(mesh, __1, __2, Layer < 0 ? 0 : Layer);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(IntegrationLog.Prefix + "Weather Reimagined overlay outside the map failed: " + ex, 0x45485752);
            }
        }
    }
}
