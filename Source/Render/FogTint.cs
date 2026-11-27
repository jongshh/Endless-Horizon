using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class FogTint
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private const int Band = 3;

        private readonly int w, h;
        private readonly HorizonDomain domain;
        private readonly Color32?[] cellColor;
        private Color32 mean = new Color32(255, 255, 255, 255);
        public bool Tinted;
        private string reason = "vanilla";

        private static bool FogLayerPatchedByOthers(out string owners)
        {
            owners = "";
            MethodInfo regen = AccessTools.Method(typeof(SectionLayer_FogOfWar), nameof(SectionLayer_FogOfWar.Regenerate));
            Patches info = regen == null ? null : Harmony.GetPatchInfo(regen);
            if (info == null) return false;
            var list = info.Postfixes.Concat(info.Transpilers).Concat(info.Prefixes)
                .Select(p => p.owner).Where(o => o != "jongsh.endlesshorizon").Distinct().ToList();
            owners = string.Join(",", list);
            return list.Count > 0;
        }

        private FogTint(Map map, HorizonDomain domain)
        {
            this.domain = domain;
            w = domain.W;
            h = domain.H;
            cellColor = new Color32?[w * h];
            if (!FogLayerPatchedByOthers(out string owners))
            {
                return;
            }
            reason = "patched by " + owners;
            Section[,] sections = sectionsRef(map.mapDrawer);
            if (sections == null)
            {
                return;
            }
            var sum = new Vector3[w * h];
            var count = new int[w * h];
            Vector3 all = Vector3.zero;
            long allCount = 0;
            foreach (Section section in sections)
            {
                if (section == null) continue;
                SectionLayer layer = section.GetLayer(typeof(SectionLayer_FogOfWar));
                if (layer != null)
                {
                    CellRect r = section.CellRect;
                    bool band = domain.AnyInBand(r.minX, r.minZ, r.maxX, r.maxZ, Band + 1);
                    int stride = band ? 1 : 8;
                    foreach (LayerSubMesh sm in layer.subMeshes)
                    {
                        if (sm.material != MatBases.FogOfWar || sm.disabled) continue;
                        List<Vector3> verts = sm.verts;
                        List<Color32> colors = sm.colors;
                        int n = Mathf.Min(verts.Count, colors.Count);
                        for (int i = 0; i < n; i += stride)
                        {
                            Color32 c = colors[i];
                            if (c.r == 0 && c.g == 0 && c.b == 0) continue;
                            if (c.a > 0)
                            {
                                all += new Vector3(c.r, c.g, c.b);
                                allCount++;
                            }
                            if (!band) continue;
                            int x = Mathf.Clamp(Mathf.FloorToInt(verts[i].x), 0, w - 1);
                            int z = Mathf.Clamp(Mathf.FloorToInt(verts[i].z), 0, h - 1);
                            if (!domain.InBand(x, z, Band)) continue;
                            int k = z * w + x;
                            sum[k] += new Vector3(c.r, c.g, c.b);
                            count[k]++;
                        }
                    }
                }
            }
            if (allCount > 0)
            {
                Vector3 m = all / allCount;
                mean = new Color32((byte)Mathf.RoundToInt(m.x), (byte)Mathf.RoundToInt(m.y), (byte)Mathf.RoundToInt(m.z), 255);
            }
            var queue = new Queue<int>();
            for (int k = 0; k < sum.Length; k++)
            {
                if (count[k] == 0) continue;
                Vector3 v = sum[k] / count[k];
                var c = new Color32((byte)Mathf.RoundToInt(v.x), (byte)Mathf.RoundToInt(v.y), (byte)Mathf.RoundToInt(v.z), 255);
                cellColor[k] = c;
                queue.Enqueue(k);
                if (c.r < 250 || c.g < 250 || c.b < 250) Tinted = true;
            }
            if (mean.r < 250 || mean.g < 250 || mean.b < 250) Tinted = true;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                int x = k % w, z = k / w;
                for (int d = 0; d < 4; d++)
                {
                    int xx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), zz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (!domain.InBand(xx, zz, Band)) continue;
                    int j = zz * w + xx;
                    if (cellColor[j].HasValue) continue;
                    cellColor[j] = cellColor[k];
                    queue.Enqueue(j);
                }
            }
        }

        public long Signature { get; private set; }

        public static long ReadSignature(Map map, HorizonDomain domain)
        {
            Section[,] sections = sectionsRef(map.mapDrawer);
            if (sections == null) return 0;
            unchecked
            {
                long hash = 17;
                foreach (Section section in sections)
                {
                    if (section == null) continue;
                    SectionLayer layer = section.GetLayer(typeof(SectionLayer_FogOfWar));
                    if (layer == null) continue;
                    CellRect r = section.CellRect;
                    int stride = domain.AnyInBand(r.minX, r.minZ, r.maxX, r.maxZ, Band + 1) ? 1 : 8;
                    foreach (LayerSubMesh sm in layer.subMeshes)
                    {
                        if (sm.material != MatBases.FogOfWar || sm.disabled) continue;
                        List<Color32> colors = sm.colors;
                        for (int i = 0; i < colors.Count; i += stride)
                        {
                            Color32 c = colors[i];
                            hash = hash * 31 + (c.r | c.g << 8 | c.b << 16);
                        }
                    }
                }
                return hash;
            }
        }

        public static FogTint Build(Map map, HorizonDomain domain)
        {
            try
            {
                var tint = new FogTint(map, domain);
                if (tint.Reads) tint.Signature = ReadSignature(map, domain);
                return tint;
            }
            catch (System.Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Reading the map fog colour failed; outside fog stays vanilla white. " + ex, 0x45484654);
                return null;
            }
        }

        public string Report => Tinted ? $"tinted (mean {mean.r},{mean.g},{mean.b}; {reason})" : reason;

        public bool Reads => reason != "vanilla";

        public bool SameAs(FogTint other)
        {
            if (other == null || other.w != w || other.h != h || other.Tinted != Tinted) return false;
            if (!Tinted) return true;
            if (!SameColour(mean, other.mean)) return false;
            for (int k = 0; k < cellColor.Length; k++)
            {
                Color32? a = cellColor[k], b = other.cellColor[k];
                if (a.HasValue != b.HasValue || a.HasValue && !SameColour(a.Value, b.Value)) return false;
            }
            return true;
        }

        private static bool SameColour(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        public int Recolour(List<LayerSubMesh> subMeshes)
        {
            int changed = 0;
            foreach (LayerSubMesh sm in subMeshes)
            {
                if (sm.material != MatBases.FogOfWar || sm.mesh == null) continue;
                List<Vector3> verts = sm.verts;
                List<Color32> colors = sm.colors;
                if (verts.Count == 0 || colors.Count != verts.Count) continue;
                for (int i = 0; i < verts.Count; i++)
                {
                    Color32 c = At(Mathf.RoundToInt(verts[i].x), Mathf.RoundToInt(verts[i].z));
                    c.a = colors[i].a;
                    colors[i] = c;
                }
                sm.mesh.SetColors(colors);
                changed++;
            }
            return changed;
        }

        public Color32 At(int x, int z)
        {
            if (!Tinted)
            {
                return new Color32(255, 255, 255, 255);
            }
            domain.Nearest(x, z, out int cx, out int cz);
            return cellColor[cz * w + cx] ?? mean;
        }
    }
}
