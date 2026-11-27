using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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

        private sealed class Quads
        {
            internal readonly List<Vector3> Verts = new List<Vector3>();
            internal readonly List<Vector2> Uvs = new List<Vector2>();
            internal readonly List<int> Tris = new List<int>();
            internal int Run = int.MinValue;

            internal void Add(float x0, float z0, float x1, float z1)
            {
                int i = Verts.Count;
                Verts.Add(new Vector3(x0, 0f, z0)); Uvs.Add(new Vector2(x0, z0));
                Verts.Add(new Vector3(x0, 0f, z1)); Uvs.Add(new Vector2(x0, z1));
                Verts.Add(new Vector3(x1, 0f, z1)); Uvs.Add(new Vector2(x1, z1));
                Verts.Add(new Vector3(x1, 0f, z0)); Uvs.Add(new Vector2(x1, z0));
                Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
                Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 3);
            }

            internal void Step(bool on, int x, int z)
            {
                if (on && Run == int.MinValue) Run = x;
                if (!on && Run != int.MinValue)
                {
                    Add(Run, z, x, z + 1);
                    Run = int.MinValue;
                }
            }

            internal Mesh Make(string name, Bounds bounds)
            {
                var mesh = new Mesh { name = name };
                if (Verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(Verts);
                mesh.SetUVs(0, Uvs);
                mesh.SetTriangles(Tris, 0);
                mesh.bounds = bounds;
                return mesh;
            }
        }

        internal static IEnumerable<bool> BuildSteps(Map map, Action<Mesh, Mesh> made)
        {
            FieldShape shape = FieldShape.Of(map);
            int w = shape.W, h = shape.H, r = shape.Ring;
            float far = Far;
            var open = new Quads();
            var all = new Quads();
            var row = new float[shape.Bounds.Width];
            for (int z = -r; z < h + r; z++)
            {
                if (!HorizonWorld.Copy(map, "weather.exposed", new CellRect(-r, z, row.Length, 1), row))
                    throw new InvalidOperationException("weather.exposed could not be read");
                for (int x = -r; x <= w + r; x++)
                {
                    bool outside = x < w + r && HorizonSpace.IsOutside(map, new IntVec3(x, 0, z));
                    open.Step(outside && row[x + r] == 1f, x, z);
                    all.Step(outside, x, z);
                }
                yield return false;
            }
            bool Reaches(int x, int z) => HorizonSpace.IsOutside(map, new IntVec3(x, 0, z));
            bool unlimited = Reaches(-r, -r) && Reaches(w + r - 1, -r) && Reaches(-r, h + r - 1) && Reaches(w + r - 1, h + r - 1);
            if (unlimited)
            {
                foreach (Quads q in new[] { open, all })
                {
                    q.Add(-far, -far, w + far, -r);
                    q.Add(-far, h + r, w + far, h + far);
                    q.Add(-far, -r, -r, h + r);
                    q.Add(w + r, -r, w + far, h + r);
                }
            }
            var bounds = new Bounds(new Vector3(w / 2f, 0f, h / 2f), new Vector3(w + 2 * far, 0.02f, h + 2 * far));
            made(open.Make("EndlessHorizon_OpenSky", bounds), all.Make("EndlessHorizon_OutsideSky", bounds));
        }
    }

    internal sealed class SkyMeshes
    {
        internal readonly Mesh Open, All;

        internal SkyMeshes(Mesh open, Mesh all)
        {
            Open = open;
            All = all;
        }

        internal void Destroy()
        {
            if (Open != null) UnityEngine.Object.Destroy(Open);
            if (All != null) UnityEngine.Object.Destroy(All);
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
        private static readonly StepResult<SkyMeshes> sky = new StepResult<SkyMeshes>(meshes => meshes.Destroy());

        private static Func<bool> hideUnderMountains;

        public static bool Installed => Freiflaeche != null;

        public static string Enable()
        {
            MethodBase target = TargetMethod();
            if (target == null) return "LivingWeather.Freiflaeche.Zeichnen missing";
            ParameterInfo[] p = target.GetParameters();
            bool builtOnly = p.Length >= 4 && p[3].ParameterType == typeof(bool);
            FieldInfo berge = AccessTools.Field(Freiflaeche, "Berge");
            hideUnderMountains = builtOnly && berge != null && berge.IsStatic && berge.FieldType == typeof(bool)
                ? Expression.Lambda<Func<bool>>(Expression.Field(null, berge)).Compile()
                : null;
            HorizonInfo.Report(Owner, "mountains", hideUnderMountains == null
                ? "cover rule not recognised: the outside keeps the open sky only"
                : "follows hideWeatherUnderMountains");
            if (!HorizonExtensions.RegisterBuildStep(Owner, Step, map => OpenSkyMesh.BuildSteps(map, (open, all) => sky.Made(map, new SkyMeshes(open, all))), new string[0]))
                return "registration refused";
            HorizonInfo.Observe(Owner, (map, id, area) =>
            {
                if (id == Step) sky.Shown(map);
                else if (id == "disposed") sky.Disposed(map);
            });
            on = true;
            return null;
        }

        internal static Mesh SkyOf(Map map) => sky.Current(map)?.Open;

        internal static Mesh OutsideSkyOf(Map map) => sky.Current(map)?.All;

        public static bool Prepare() => TargetMethod() is MethodBase m && m.GetParameters().Length >= 4 && m.GetParameters()[3].ParameterType == typeof(bool);

        public static MethodBase TargetMethod() => Freiflaeche == null ? null : AccessTools.Method(Freiflaeche, "Zeichnen");

        public static void Postfix(Map __0, Matrix4x4 __1, Material __2, bool __3)
        {
            Active = true;
            try
            {
                if (!on || __2 == null) return;
                SkyMeshes meshes = sky.Current(__0);
                bool overMountains = __3 && hideUnderMountains != null && !hideUnderMountains();
                Mesh mesh = overMountains ? meshes?.All : meshes?.Open;
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
