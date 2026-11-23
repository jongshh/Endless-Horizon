using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class RingDarkness
    {
        private readonly HorizonSurface surface;
        private readonly IReadOnlyList<CellRect> rects;
        private readonly List<Mesh> chunks = new List<Mesh>();
        private readonly Queue<int> queue = new Queue<int>();
        private readonly HashSet<int> queued = new HashSet<int>();
        private readonly List<(int frame, Mesh mesh)> retired = new List<(int, Mesh)>();
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private Mesh frame;
        private bool on;

        public RingDarkness(HorizonSurface surface, IReadOnlyList<CellRect> rects)
        {
            this.surface = surface;
            this.rects = rects;
        }

        public static bool Visible(Map map) => ModsConfig.AnomalyActive && map.gameConditionManager.DarknessVisible;

        internal bool On => on;
        internal int Pending => queue.Count;

        internal int ChunkCount
        {
            get
            {
                int n = 0;
                foreach (Mesh m in chunks) if (m != null) n++;
                return n;
            }
        }

        public void Add(int index)
        {
            Pad();
            if (!Visible(surface.Map)) return;
            on = true;
            Replace(index, Build(surface, rects[index]));
        }

        public void Step(double budgetMs)
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount - retired[i].frame < 2) continue;
                Object.Destroy(retired[i].mesh);
                retired.RemoveAt(i);
            }
            if (!Visible(surface.Map))
            {
                if (on) Free();
                return;
            }
            Pad();
            if (!on)
            {
                on = true;
                for (int i = 0; i < rects.Count; i++) Enqueue(i);
            }
            frame ??= FrameMesh.Make(surface.Domain, surface.Ring, FrameMesh.Far, AltitudeLayer.Darkness.AltitudeFor(), new Color32(255, 255, 255, 255));
            var sw = Stopwatch.StartNew();
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                queued.Remove(i);
                Replace(i, Build(surface, rects[i]));
                if (sw.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
        }

        public void MarkDirty(CellRect area)
        {
            if (!on) return;
            CellRect around = area.ExpandedBy(1);
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].Overlaps(around)) Enqueue(i);
        }

        public int Draw(CellRect view)
        {
            if (!on || !Visible(surface.Map)) return 0;
            Map map = surface.Map;
            Material mat = MatBases.Darkness;
            if (mat == null) return 0;
            float a = Mathf.Clamp01(1f - Mathf.Max(map.gameConditionManager.MapBrightness, map.skyManager.CurSkyGlow));
            block.SetColor(ShaderPropertyIDs.ColorTwo, new Color(1f, 1f, 1f, a));
            block.SetFloat(ShaderPropertyIDs.AgeSecsPausable, RealTime.UnpausedRealTime);
            int calls = 0;
            if (frame != null)
            {
                Graphics.DrawMesh(frame, Matrix4x4.identity, mat, 0, null, 0, block);
                calls++;
            }
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] == null || !rects[i].Overlaps(view)) continue;
                Graphics.DrawMesh(chunks[i], Matrix4x4.identity, mat, 0, null, 0, block);
                calls++;
            }
            return calls;
        }

        internal int AlphaAt(int x, int z)
        {
            for (int i = 0; i < chunks.Count && i < rects.Count; i++)
            {
                CellRect r = rects[i];
                if (chunks[i] == null || x < r.minX || z < r.minZ || x > r.maxX + 1 || z > r.maxZ + 1) continue;
                var colors = new List<Color32>();
                chunks[i].GetColors(colors);
                int k = (z - r.minZ) * (r.Width + 1) + (x - r.minX);
                return k < colors.Count ? colors[k].a : -1;
            }
            return -1;
        }

        private void Pad()
        {
            while (chunks.Count < rects.Count) chunks.Add(null);
        }

        private void Enqueue(int i)
        {
            if (queued.Add(i)) queue.Enqueue(i);
        }

        private void Replace(int i, Mesh fresh)
        {
            Mesh old = chunks[i];
            chunks[i] = fresh;
            if (old != null) retired.Add((Time.frameCount, old));
        }

        private void Free()
        {
            on = false;
            queue.Clear();
            queued.Clear();
            for (int i = 0; i < chunks.Count; i++) Replace(i, null);
            if (frame != null) retired.Add((Time.frameCount, frame));
            frame = null;
        }

        public void Dispose()
        {
            foreach (Mesh m in chunks) if (m != null) Object.Destroy(m);
            chunks.Clear();
            foreach ((int _, Mesh m) in retired) Object.Destroy(m);
            retired.Clear();
            if (frame != null) Object.Destroy(frame);
            frame = null;
            on = false;
        }

        internal static Mesh Build(HorizonSurface surface, CellRect rect)
        {
            HorizonDomain domain = surface.Domain;
            int lw = rect.Width + 2;
            var light = new float[lw * (rect.Height + 2)];
            for (int z = rect.minZ - 1; z <= rect.maxZ + 1; z++)
                for (int x = rect.minX - 1; x <= rect.maxX + 1; x++)
                    light[(z - rect.minZ + 1) * lw + (x - rect.minX + 1)] = surface.DarknessLight(x, z);

            var verts = new List<Vector3>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            float y = AltitudeLayer.Darkness.AltitudeFor();
            int cw = rect.Width + 1;
            for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
            {
                for (int x = rect.minX; x <= rect.maxX + 1; x++)
                {
                    float all = 0f, mapSum = 0f;
                    int mapN = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int cx = x - 1 + (k & 1), cz = z - 1 + (k >> 1);
                        float l = light[(cz - rect.minZ + 1) * lw + (cx - rect.minX + 1)];
                        all += l;
                        if (domain.Inside(cx, cz))
                        {
                            mapSum += l;
                            mapN++;
                        }
                    }
                    float dark = 1f - (mapN > 0 ? mapSum / mapN : all / 4f);
                    verts.Add(new Vector3(x, y, z));
                    colors.Add(new Color32(255, 255, 255, (byte)(Mathf.Clamp01(dark) * 255f)));
                }
            }
            for (int z = 0; z < rect.Height; z++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    if (!domain.Drawn(new IntVec3(rect.minX + x, 0, rect.minZ + z))) continue;
                    int i0 = z * cw + x, i1 = i0 + cw;
                    tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                    tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                }
            }
            if (tris.Count == 0) return null;
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = rect.ExpandedBy(1).ToBounds();
            return mesh;
        }
    }
}
