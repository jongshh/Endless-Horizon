using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonFog
    {
        private const float BorderFadePx = 6f;
        private const int CellsPerPx = FogMask.CellsPerPx;
        private const float UpdateInterval = 1f / 12f;
        private const int TileN = 128;

        private static float[] tile;

        private readonly Map map;
        private readonly int texW, texH;
        private readonly FogMask mask;
        private readonly float[] baseAlpha;
        private readonly float[] solid;
        private readonly Color32[] pixels;
        private readonly Texture2D tex;
        private readonly Material mat;
        private readonly Mesh mesh;
        private float lastUpdate = -1f;
        private bool lastClouds;
        private bool rampChanged;

        private readonly Material gpuMat;
        private readonly Texture2D maskTex, noiseTex;
        private bool maskStale;

        private bool Gpu =>
#if EH_DEV
            gpuMat != null && !HorizonDebug.FogCpu;
#else
            gpuMat != null;
#endif

        internal static string Path => HorizonAssets.FogState;

        private const int BlockCells = 17;
        private const int SpillCells = 3;
        private readonly int reachCells, extentX, extentZ, blocksX, blocksZ;
        private readonly bool[] foggedBlock;
        public Color CurrentColor;

        public HorizonFog(HorizonSurface surface)
        {
            map = surface.Map;
            HorizonDomain domain = surface.Domain;
            EnsureTile();
            mask = surface.FogMask;
            int reach = mask.Reach;
            texW = mask.Width;
            texH = mask.Height;
            baseAlpha = mask.BaseAlpha;
            solid = mask.Solid;
            pixels = new Color32[texW * texH];

            tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            mat = new Material(ShaderDatabase.Transparent) { mainTexture = tex };
            ApplyRenderOrder(mat);
            float tx0 = -reach, tz0 = -reach, tw = texW * CellsPerPx, th = texH * CellsPerPx;
            float fx0 = -FrameMesh.Far, fz0 = -FrameMesh.Far, fx1 = domain.W + FrameMesh.Far, fz1 = domain.H + FrameMesh.Far;
            float u0 = (fx0 - tx0) / tw, v0 = (fz0 - tz0) / th, u1 = (fx1 - tx0) / tw, v1 = (fz1 - tz0) / th;
            float y = AltitudeLayer.Weather.AltitudeFor();
            mesh = domain.Limited ? FrameMesh.LimitedQuad(domain, fx0, fz0, fx1, fz1, y, u0, v0, u1, v1)
                : FrameMesh.Quad(fx0, fz0, fx1, fz1, y, u0, v0, u1, v1);
            mask.SetBoost(0);
            Shader shader = HorizonAssets.FogShader;
            if (shader != null)
            {
                maskTex = new Texture2D(texW, texH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                noiseTex = new Texture2D(TileN, TileN, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
                var noise = new Color32[TileN * TileN];
                for (int k = 0; k < noise.Length; k++)
                {
                    byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(tile[k] * 255f), 0, 255);
                    noise[k] = new Color32(v, v, v, 255);
                }
                noiseTex.SetPixels32(noise);
                noiseTex.Apply(false);
                gpuMat = new Material(shader);
                gpuMat.SetTexture("_MaskTex", maskTex);
                gpuMat.SetTexture("_NoiseTex", noiseTex);
                gpuMat.SetVector("_MaskSize", new Vector4(texW, texH, 0f, 0f));
                ApplyRenderOrder(gpuMat);
                maskStale = true;
            }
            reachCells = reach;
            extentX = domain.W + reach;
            extentZ = domain.H + reach;
            blocksX = (extentX + reach) / BlockCells + 1;
            blocksZ = (extentZ + reach) / BlockCells + 1;
            foggedBlock = new bool[blocksX * blocksZ];
            MarkBlocks();
            Rebuild(0f, true);
        }

        private void MarkBlocks()
        {
            System.Array.Clear(foggedBlock, 0, foggedBlock.Length);
            for (int pz = 0; pz < texH; pz++)
            {
                for (int px = 0; px < texW; px++)
                {
                    int i = pz * texW + px;
                    if (baseAlpha[i] <= 0f && solid[i] <= 0f) continue;
                    int cx = px * CellsPerPx + 1, cz = pz * CellsPerPx + 1;
                    int x0 = Mathf.Max(0, cx - SpillCells) / BlockCells, x1 = Mathf.Min(blocksX - 1, (cx + SpillCells) / BlockCells);
                    int z0 = Mathf.Max(0, cz - SpillCells) / BlockCells, z1 = Mathf.Min(blocksZ - 1, (cz + SpillCells) / BlockCells);
                    for (int bz = z0; bz <= z1; bz++)
                        for (int bx = x0; bx <= x1; bx++) foggedBlock[bz * blocksX + bx] = true;
                }
            }
        }

        private bool InView(CellRect view)
        {
            if (view.minX < -reachCells || view.minZ < -reachCells || view.maxX >= extentX || view.maxZ >= extentZ) return true;
            int x0 = (view.minX + reachCells) / BlockCells, x1 = (view.maxX + reachCells) / BlockCells;
            int z0 = (view.minZ + reachCells) / BlockCells, z1 = (view.maxZ + reachCells) / BlockCells;
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                    if (foggedBlock[bz * blocksX + bx]) return true;
            return false;
        }

        private static int BoostFor(Map map)
        {
            string weather = map.weatherManager.CurWeatherLerped?.defName ?? "";
            if (weather.Contains("Fog")) return 14;
            if (weather.Contains("Rain") || weather.Contains("Snow")) return 6;
            return 0;
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
#if EH_DEV
            long computed = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            tex.SetPixels32(0, 0, texW, texH, pixels);
            tex.Apply(false);
#if EH_DEV
            HorizonDebug.FogUploadMs += (System.Diagnostics.Stopwatch.GetTimestamp() - computed) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
#endif
        }

        public static bool SelfLit;
        public static bool DarkensItself;
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
            Material darkness = MatBases.Darkness;
            DarkensItself = darkness != null && fog.renderQueue > darkness.renderQueue;
            if (!orderLogged)
            {
                orderLogged = true;
                Log.Message($"{HorizonLog.Prefix}render queues: fog {before}→{fog.renderQueue}, shadows max {shadows} (edge {MatBases.EdgeShadow.renderQueue}, sun {MatBases.SunShadowFade.renderQueue}/{MatBases.SunShadow.renderQueue}), lightOverlay {light}, fogOfWar {MatBases.FogOfWar.renderQueue}. darkness {(MatBases.Darkness != null ? MatBases.Darkness.renderQueue : -1)}. Self-lit fog: {SelfLit}, darkens itself: {DarkensItself}.");
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
            if (DarkensItself && ModsConfig.AnomalyActive && map.gameConditionManager.DarknessVisible)
            {
                float bright = Mathf.Clamp01(Mathf.Max(map.gameConditionManager.MapBrightness, map.skyManager.CurSkyGlow));
                c = new Color(c.r * bright, c.g * bright, c.b * bright);
            }
            c.a = 1f;
            return c;
        }

        public bool Update(bool clouds, CellRect view)
        {
            int boost = BoostFor(map);
            if (boost != mask.Boost)
            {
                mask.SetBoost(boost);
                MarkBlocks();
                rampChanged = true;
                maskStale = true;
            }
            if (!InView(view))
            {
#if EH_DEV
                if (!HorizonDebug.FogAlways) return false;
#else
                return false;
#endif
            }
            CurrentColor = FogColorFor(map);
            float now = Time.realtimeSinceStartup;
            if (Gpu)
            {
                if (maskStale)
                {
                    UploadMask();
                    maskStale = false;
                }
                gpuMat.SetFloat("_FogTime", now);
                gpuMat.SetFloat("_Clouds", clouds ? 1f : 0f);
#if EH_DEV
                HorizonDebug.FogDraws++;
#endif
                return true;
            }
            if (clouds && now - lastUpdate >= UpdateInterval || clouds != lastClouds || rampChanged)
            {
                lastUpdate = now;
                lastClouds = clouds;
                rampChanged = false;
#if EH_DEV
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                Rebuild(now, clouds);
#if EH_DEV
                HorizonDebug.FogUpdates++;
                HorizonDebug.FogUpdateMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
#endif
            }
#if EH_DEV
            HorizonDebug.FogDraws++;
            if (clouds && now - lastUpdate > UpdateInterval) HorizonDebug.FogStaleDraws++;
#endif
            return true;
        }

        public void Draw()
        {
            Material m = Gpu ? gpuMat : mat;
            m.color = CurrentColor;
            Graphics.DrawMesh(mesh, Matrix4x4.identity, m, 0);
        }

        private void UploadMask()
        {
            var data = new Color32[texW * texH];
            for (int i = 0; i < data.Length; i++)
                data[i] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(baseAlpha[i] * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(solid[i] * 255f), 0, 255), 0, 255);
            maskTex.SetPixels32(data);
            maskTex.Apply(false);
        }

        public void Dispose()
        {
            Object.Destroy(mesh);
            Object.Destroy(mat);
            Object.Destroy(tex);
            if (gpuMat != null) Object.Destroy(gpuMat);
            if (maskTex != null) Object.Destroy(maskTex);
            if (noiseTex != null) Object.Destroy(noiseTex);
        }
    }
}
