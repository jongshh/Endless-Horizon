using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class FrameMesh
    {
        public const float Far = 2000f;

        public static Mesh Make(HorizonDomain domain, float d0, float d1, float y, Color32 color)
        {
            float w = domain.W, h = domain.H;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            void Quad(float x0, float z0, float x1, float z1)
            {
                if (domain.Limited)
                {
                    Fan(domain.Clip(Rect(x0, z0, x1, z1)), y, verts, tris, p => uvs.Add(new Vector2(p.x / w, p.y / h)), () => colors.Add(color));
                    return;
                }
                int n = verts.Count;
                verts.Add(new Vector3(x0, y, z0));
                verts.Add(new Vector3(x0, y, z1));
                verts.Add(new Vector3(x1, y, z1));
                verts.Add(new Vector3(x1, y, z0));
                uvs.Add(new Vector2(x0 / w, z0 / h));
                uvs.Add(new Vector2(x0 / w, z1 / h));
                uvs.Add(new Vector2(x1 / w, z1 / h));
                uvs.Add(new Vector2(x1 / w, z0 / h));
                for (int i = 0; i < 4; i++)
                {
                    colors.Add(color);
                }
                tris.Add(n); tris.Add(n + 1); tris.Add(n + 2);
                tris.Add(n); tris.Add(n + 2); tris.Add(n + 3);
            }
            Quad(-d1, h + d0, w + d1, h + d1);
            Quad(-d1, -d1, w + d1, -d0);
            Quad(-d1, -d0, -d0, h + d0);
            Quad(w + d0, -d0, w + d1, h + d0);
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(new Vector3(w / 2f, y, h / 2f), new Vector3(w + 2f * d1 + 4f, 1f, h + 2f * d1 + 4f));
            return mesh;
        }

        private static List<(float x, float z)> Rect(float x0, float z0, float x1, float z1) =>
            new List<(float x, float z)> { (x0, z0), (x1, z0), (x1, z1), (x0, z1) };

        private static void Fan(List<(float x, float z)> shape, float y, List<Vector3> verts, List<int> tris, System.Action<Vector2> uv, System.Action color)
        {
            if (shape.Count < 3) return;
            int first = verts.Count;
            foreach ((float x, float z) in shape)
            {
                verts.Add(new Vector3(x, y, z));
                uv(new Vector2(x, z));
                color();
            }
            for (int i = 1; i + 1 < shape.Count; i++)
            {
                tris.Add(first); tris.Add(first + i + 1); tris.Add(first + i);
            }
        }

        public static Mesh LimitedQuad(HorizonDomain domain, float x0, float z0, float x1, float z1, float y, float u0, float v0, float u1, float v1)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            Fan(domain.Clip(Rect(x0, z0, x1, z1)), y, verts, tris,
                p => uvs.Add(new Vector2(u0 + (u1 - u0) * (p.x - x0) / (x1 - x0), v0 + (v1 - v0) * (p.y - z0) / (z1 - z0))),
                () => colors.Add(new Color32(255, 255, 255, 255)));
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(new Vector3((x0 + x1) / 2f, y, (z0 + z1) / 2f), new Vector3(x1 - x0, 1f, z1 - z0));
            return mesh;
        }

        public static Mesh Quad(float x0, float z0, float x1, float z1, float y, float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f)
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0) });
            mesh.SetColors(new List<Color32> { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) });
            mesh.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.bounds = new Bounds(new Vector3((x0 + x1) / 2f, y, (z0 + z1) / 2f), new Vector3(x1 - x0, 1f, z1 - z0));
            return mesh;
        }
    }
}
