using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class HorizonDebug
    {
        public static bool HideRing;
        public static bool HideFog;
        public static bool HideClouds;
        public static bool HideLighting;
        public static bool ForceMirror;
    }

    public static class FrameMesh
    {
        public const float Far = 2000f;

        public static Mesh Make(IntVec3 size, float d0, float d1, float y, Color32 color)
        {
            float w = size.x, h = size.z;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            void Quad(float x0, float z0, float x1, float z1)
            {
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

    public sealed class HorizonLighting
    {
        private readonly Mesh mesh;

        public HorizonLighting(Map map, int ringWidth)
        {
            mesh = FrameMesh.Make(map.Size, ringWidth, FrameMesh.Far, AltitudeLayer.LightingOverlay.AltitudeFor(), new Color32(0, 0, 0, 0));
        }

        public void Draw()
        {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, MatBases.LightOverlay, 0);
        }

        public void Dispose() => Object.Destroy(mesh);
    }

    public sealed class HorizonFog
    {
        public readonly int Reach;
        private const float BorderFadePx = 6f;
        private const int CellsPerPx = 2;
        private const float UpdateInterval = 1f / 12f;
        private const int TileN = 128;

        private static float[] tile;

        private readonly Map map;
        private readonly int texW, texH;
        private readonly float[] baseAlpha;
        private readonly float[] solid;
        private readonly float[] warpedDist;
        private readonly int fogStart, ringWidth;
        private int weatherBoost = -1;
        private readonly Color32[] pixels;
        private readonly Texture2D tex;
        private readonly Material mat;
        private readonly Mesh mesh;
        private float lastUpdate = -1f;
        private bool lastClouds;
        public Color CurrentColor;

        public const float FrontSoftness = 8f;
        public const float OpaqueInset = 6f;
        public const float WispAmplitude = 9f;
        public const float SolidFrom = 14f, SolidTo = 3f;

        public static float RampLo(int fogStart, int boost = 0) => fogStart - FrontSoftness - boost;

        public static float RampHi(int ringWidth) => ringWidth - OpaqueInset;

        public static float CoveredFrom(int fogStart, int ringWidth, float alpha)
        {
            float lo = RampLo(fogStart), hi = RampHi(ringWidth);
            float t = InverseSmoothStep(alpha);
            float ramp = hi > lo ? lo + t * (hi - lo) + WispAmplitude : hi + WispAmplitude;
            float solid = ringWidth - SolidFrom + t * (SolidFrom - SolidTo);
            return Mathf.Clamp(Mathf.Min(ramp, solid), 0f, ringWidth);
        }

        private static float InverseSmoothStep(float a)
        {
            a = Mathf.Clamp01(a);
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (mid * mid * (3f - 2f * mid) < a) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        public HorizonFog(Map map, int ringWidth, int fogStart)
        {
            this.map = map;
            EnsureTile();
            int reach = Reach = ringWidth + 16;
            int cellsX = map.Size.x + 2 * reach, cellsZ = map.Size.z + 2 * reach;
            texW = Mathf.CeilToInt(cellsX / (float)CellsPerPx);
            texH = Mathf.CeilToInt(cellsZ / (float)CellsPerPx);
            baseAlpha = new float[texW * texH];
            solid = new float[texW * texH];
            warpedDist = new float[texW * texH];
            this.fogStart = fogStart;
            this.ringWidth = ringWidth;
            pixels = new Color32[texW * texH];

            int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x464F47;
            var warp = new Verse.Noise.Perlin(0.035, 2.0, 0.5, 3, seed, Verse.Noise.QualityMode.Medium);
            float w = map.Size.x, h = map.Size.z;
            for (int pz = 0; pz < texH; pz++)
            {
                for (int px = 0; px < texW; px++)
                {
                    float x = -reach + (px + 0.5f) * CellsPerPx;
                    float z = -reach + (pz + 0.5f) * CellsPerPx;
                    float dx = Mathf.Max(0f, Mathf.Max(-x, x - w));
                    float dz = Mathf.Max(0f, Mathf.Max(-z, z - h));
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    int i = pz * texW + px;
                    if (d <= 0f)
                    {
                        warpedDist[i] = -1000f;
                        continue;
                    }
                    float n = (float)warp.GetValue(x, 0, z);
                    warpedDist[i] = d + n * WispAmplitude;
                    solid[i] = SmoothStep(ringWidth - SolidFrom, ringWidth - SolidTo, d);
                }
            }

            tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            mat = new Material(ShaderDatabase.Transparent) { mainTexture = tex };
            ApplyRenderOrder(mat);
            float tx0 = -reach, tz0 = -reach, tw = texW * CellsPerPx, th = texH * CellsPerPx;
            float fx0 = -FrameMesh.Far, fz0 = -FrameMesh.Far, fx1 = map.Size.x + FrameMesh.Far, fz1 = map.Size.z + FrameMesh.Far;
            mesh = FrameMesh.Quad(fx0, fz0, fx1, fz1, AltitudeLayer.Weather.AltitudeFor(),
                (fx0 - tx0) / tw, (fz0 - tz0) / th, (fx1 - tx0) / tw, (fz1 - tz0) / th);
            ComputeBase(0);
            Rebuild(0f, true);
        }

        private void ComputeBase(int boost)
        {
            weatherBoost = boost;
            float lo = RampLo(fogStart, boost), hi = RampHi(ringWidth);
            for (int i = 0; i < baseAlpha.Length; i++)
            {
                baseAlpha[i] = warpedDist[i] <= -100f ? 0f : SmoothStep(lo, hi, warpedDist[i]);
            }
        }

        private static int BoostFor(Map map)
        {
            string weather = map.weatherManager.CurWeatherLerped?.defName ?? "";
            if (weather.Contains("Fog")) return 14;
            if (weather.Contains("Rain") || weather.Contains("Snow")) return 6;
            return 0;
        }

        private static float SmoothStep(float a, float b, float v)
        {
            float t = Mathf.Clamp01((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static void EnsureTile()
        {
            if (tile != null)
            {
                return;
            }
            tile = new float[TileN * TileN];
            var rng = new System.Random(0x45484E);
            float amp = 1f, total = 0f;
            for (int octave = 0; octave < 4; octave++)
            {
                int lattice = 4 << octave;
                var g = new float[lattice * lattice];
                for (int k = 0; k < g.Length; k++)
                {
                    g[k] = (float)rng.NextDouble();
                }
                for (int y = 0; y < TileN; y++)
                {
                    float fy = y * lattice / (float)TileN;
                    int y0 = (int)fy, y1 = (y0 + 1) % lattice;
                    float ty = fy - y0; ty = ty * ty * (3f - 2f * ty);
                    for (int x = 0; x < TileN; x++)
                    {
                        float fx = x * lattice / (float)TileN;
                        int x0 = (int)fx, x1 = (x0 + 1) % lattice;
                        float tx = fx - x0; tx = tx * tx * (3f - 2f * tx);
                        float a = Mathf.Lerp(g[y0 * lattice + x0], g[y0 * lattice + x1], tx);
                        float b = Mathf.Lerp(g[y1 * lattice + x0], g[y1 * lattice + x1], tx);
                        tile[y * TileN + x] += Mathf.Lerp(a, b, ty) * amp;
                    }
                }
                total += amp;
                amp *= 0.5f;
            }
            for (int k = 0; k < tile.Length; k++)
            {
                tile[k] /= total;
            }
        }

        private static float Sample(float x, float y)
        {
            x %= TileN; if (x < 0) x += TileN;
            y %= TileN; if (y < 0) y += TileN;
            int x0 = (int)x, y0 = (int)y;
            int x1 = (x0 + 1) % TileN, y1 = (y0 + 1) % TileN;
            float tx = x - x0, ty = y - y0;
            float a = Mathf.Lerp(tile[y0 * TileN + x0], tile[y0 * TileN + x1], tx);
            float b = Mathf.Lerp(tile[y1 * TileN + x0], tile[y1 * TileN + x1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        private void Rebuild(float t, bool clouds)
        {
            float o1x = t * 0.25f, o1y = t * 0.12f;
            float o2x = -t * 0.10f, o2y = t * 0.16f;
            for (int pz = 0; pz < texH; pz++)
            {
                for (int px = 0; px < texW; px++)
                {
                    int i = pz * texW + px;
                    float b = baseAlpha[i], s = solid[i];
                    if (b <= 0f && s <= 0f)
                    {
                        pixels[i] = new Color32(255, 255, 255, 0);
                        continue;
                    }
                    float cloud = 0.5f;
                    if (clouds)
                    {
                        cloud = 0.6f * Sample(px * 0.9f + o1x, pz * 0.9f + o1y) + 0.4f * Sample(px * 1.7f + o2x + 37f, pz * 1.7f + o2y + 11f);
                        float border = Mathf.Min(Mathf.Min(px, texW - 1 - px), Mathf.Min(pz, texH - 1 - pz));
                        cloud = Mathf.Lerp(0.5f, cloud, Mathf.Clamp01((border - 1f) / BorderFadePx));
                    }
                    float a = Mathf.Clamp01(b * (0.55f + 0.9f * cloud) + s);
                    byte bright = (byte)(255f * (0.82f + 0.18f * cloud));
                    pixels[i] = new Color32(bright, bright, bright, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(0, 0, texW, texH, pixels);
            tex.Apply(false);
        }

        public static bool SelfLit;
        private static bool orderLogged;

        private static void ApplyRenderOrder(Material fog)
        {
            int shadows = Mathf.Max(Mathf.Max(MatBases.EdgeShadow.renderQueue, MatBases.SunShadowFade.renderQueue), MatBases.SunShadow.renderQueue);
            int light = MatBases.LightOverlay.renderQueue;
            int before = fog.renderQueue;
            if (fog.renderQueue <= shadows)
            {
                fog.renderQueue = shadows + 1;
            }
            SelfLit = fog.renderQueue >= light;
            if (!orderLogged)
            {
                orderLogged = true;
                Log.Message($"{EndlessHorizonMod.LogPrefix}render queues: fog {before}→{fog.renderQueue}, shadows max {shadows} (edge {MatBases.EdgeShadow.renderQueue}, sun {MatBases.SunShadowFade.renderQueue}/{MatBases.SunShadow.renderQueue}), lightOverlay {light}, fogOfWar {MatBases.FogOfWar.renderQueue}. Self-lit fog: {SelfLit}.");
            }
        }

        public static Color FogColorFor(Map map)
        {
            Color c = new Color(0.70f, 0.72f, 0.75f);
            string weather = map.weatherManager.CurWeatherLerped?.defName ?? "";
            if (weather.Contains("Snow"))
            {
                c = Color.Lerp(c, new Color(0.88f, 0.9f, 0.93f), 0.5f);
            }
            else if (weather.Contains("Rain") || weather.Contains("Fog"))
            {
                c = Color.Lerp(c, new Color(0.55f, 0.57f, 0.6f), 0.5f);
            }
            if (SelfLit)
            {
                float glow = Mathf.Clamp01(map.skyManager.CurSkyGlow);
                c = Color.Lerp(new Color(0.05f, 0.06f, 0.08f), c, glow);
            }
            c.a = 1f;
            return c;
        }

        public void Update(bool clouds)
        {
            int boost = BoostFor(map);
            if (boost != weatherBoost)
            {
                ComputeBase(boost);
                lastClouds = !clouds;
            }
            CurrentColor = FogColorFor(map);
            float now = Time.realtimeSinceStartup;
            if (clouds && now - lastUpdate >= UpdateInterval || clouds != lastClouds)
            {
                lastUpdate = now;
                lastClouds = clouds;
                Rebuild(now, clouds);
            }
        }

        public void Draw()
        {
            mat.color = CurrentColor;
            Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0);
        }

        public void Dispose()
        {
            Object.Destroy(mesh);
            Object.Destroy(mat);
            Object.Destroy(tex);
        }
    }
}
