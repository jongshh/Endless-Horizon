using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class ExtensionRing
    {
        internal const int ReuseDistance = 40;
        private const float QuietSeconds = 2.5f, QuickSeconds = 0.5f;
        internal const float LongestWaitSeconds = 10f;
        private const double FrameBudgetMs = 2.0;

        internal sealed class Strip
        {
            internal LayerRegistration Registration;
            internal CellRect Rect;
            internal SectionLayer Layer;
        }

        private sealed class Pass
        {
            internal IEnumerator<bool> Work;
            internal List<CellRect> Areas;
            internal readonly List<Strip> Strips = new List<Strip>();
            internal readonly HashSet<SectionLayer> Reused = new HashSet<SectionLayer>();
            internal readonly List<SectionLayer> Made = new List<SectionLayer>();
            internal double SpentMs, LongestMs;

            internal void Abort()
            {
                Work?.Dispose();
                Work = null;
                foreach (SectionLayer layer in Made) ExtensionSections.Free(layer);
                Made.Clear();
                Strips.Clear();
            }
        }

        internal sealed class OwnerWork
        {
            internal string Owner;
            internal List<StepRegistration> Steps;
            internal List<LayerRegistration> Layers;
            internal List<Strip> Shown = new List<Strip>();
            internal HashSet<Section> Joined = new HashSet<Section>();
        }

        private sealed class Refresh
        {
            internal bool Dirty, Global;
            internal float Due, First;
            internal readonly List<CellRect> Areas = new List<CellRect>();
            internal Pass Building, Ready;
            internal int ReadyFrame;
        }

        private readonly Map map;
        private readonly HorizonSurface surface;
        private readonly HorizonDomain domain;
        private readonly List<OwnerWork> owners = new List<OwnerWork>();
        private readonly List<Refresh> refreshes = new List<Refresh>();
        private readonly List<(int frame, SectionLayer layer)> retired = new List<(int, SectionLayer)>();
        private readonly Queue<(SectionLayer layer, LayerRegistration registration)> redraw = new Queue<(SectionLayer, LayerRegistration)>();
        private List<CellRect> strips;
        private Pass initial;
        private bool live;

        internal IReadOnlyList<OwnerWork> Owners => owners;

        internal ExtensionRing(HorizonSurface surface)
        {
            this.surface = surface;
            map = surface.Map;
            domain = surface.Domain;
            foreach (string owner in ExtensionRegistry.Owners)
            {
                owners.Add(new OwnerWork
                {
                    Owner = owner,
                    Steps = ExtensionRegistry.Steps.FindAll(s => s.Owner == owner),
                    Layers = ExtensionRegistry.Layers.FindAll(l => l.Owner == owner),
                });
                refreshes.Add(new Refresh());
            }
        }

        internal IEnumerable<bool> RunStepsFirst()
        {
            foreach (StepRegistration step in ExtensionRegistry.Steps)
                foreach (bool more in RunStep(step)) yield return more;
        }

        internal IEnumerable<bool> BuildLayersFirst()
        {
            for (int i = 0; i < owners.Count; i++)
            {
                initial = new Pass();
                foreach (bool more in BuildLayers(owners[i], initial)) yield return more;
                owners[i].Shown.AddRange(initial.Strips);
                initial = null;
                yield return false;
            }
        }

        private IEnumerable<bool> RefreshSteps(OwnerWork work, Pass pass)
        {
            foreach (StepRegistration step in work.Steps)
                foreach (bool more in RunStep(step)) yield return more;
            foreach (bool more in BuildLayers(work, pass)) yield return more;
        }

        private IEnumerable<bool> RunStep(StepRegistration step)
        {
            if (step.Failed) yield break;
            IEnumerator<bool> work = null;
            double spent = 0;
            try
            {
                while (true)
                {
                    bool more = false;
                    long started = Stopwatch.GetTimestamp();
                    using (SurfaceTable.Building(surface))
                    {
                        try
                        {
                            work ??= step.Build(map)?.GetEnumerator();
                            more = work != null && work.MoveNext();
                        }
                        catch (Exception ex) { ExtensionRegistry.Fail(step, ex); }
                    }
                    spent += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    if (!more) break;
                    yield return false;
                }
                HorizonPerf.Record($"extension {step.Owner} step {step.Id}", spent);
            }
            finally
            {
                if (work != null)
                {
                    using (SurfaceTable.Building(surface))
                    {
                        try { work.Dispose(); }
                        catch (Exception ex) { ExtensionRegistry.Fail(step, ex); }
                    }
                }
            }
        }

        private IEnumerable<bool> BuildLayers(OwnerWork work, Pass pass)
        {
            if (work.Layers.Count == 0) yield break;
            strips ??= domain.StripRects(Section.Size);
            foreach (LayerRegistration registration in work.Layers)
            {
                if (!ExtensionRegistry.Presents(registration)) continue;
                int asked = 0;
                foreach (CellRect rect in strips)
                {
                    bool built;
                    try { built = BuildStrip(work, pass, registration, rect); }
                    catch (Exception ex)
                    {
                        ExtensionRegistry.Fail(registration, ex);
                        break;
                    }
                    if (built || (++asked & 15) == 0) yield return false;
                }
                yield return false;
            }
        }

        private bool BuildStrip(OwnerWork work, Pass pass, LayerRegistration registration, CellRect rect)
        {
            using (SurfaceTable.Building(surface))
            {
                if (registration.Wanted != null && !registration.Wanted(map, rect)) return false;
                Strip old = Reusable(work, pass, registration, rect);
                if (old != null)
                {
                    pass.Strips.Add(old);
                    pass.Reused.Add(old.Layer);
                    return false;
                }
                var layer = (SectionLayer)Activator.CreateInstance(registration.Type, ExtensionSections.StandIn(map, rect));
                pass.Strips.Add(new Strip { Registration = registration, Rect = rect, Layer = layer });
                pass.Made.Add(layer);
                bool before = EdgeJoin.Regenerating;
                EdgeJoin.Regenerating = true;
                try
                {
                    using (ExtensionScopes.EnterAll(map, registration.Scopes)) layer.Regenerate();
                }
                finally { EdgeJoin.Regenerating = before; }
                return true;
            }
        }

        private static Strip Reusable(OwnerWork work, Pass pass, LayerRegistration registration, CellRect rect)
        {
            if (pass.Areas == null) return null;
            foreach (CellRect area in pass.Areas)
            {
                if (ExtensionSections.Gap(area, rect) <= ReuseDistance) return null;
            }
            foreach (Strip strip in work.Shown)
            {
                if (strip.Registration == registration && strip.Rect == rect) return strip;
            }
            return null;
        }

        internal void Show(ExtensionRing before)
        {
            if (before != null)
            {
                before.live = false;
                for (int i = 0; i < owners.Count; i++)
                {
                    int was = before.owners.FindIndex(o => o.Owner == owners[i].Owner);
                    if (was < 0) continue;
                    owners[i].Joined = before.owners[was].Joined;
                    before.owners[was].Joined = new HashSet<Section>();
                    Refresh late = before.refreshes[was];
                    if (!late.Dirty) continue;
                    refreshes[i].Dirty = true;
                    refreshes[i].Global = late.Global;
                    refreshes[i].Due = late.Due;
                    refreshes[i].First = late.First;
                    refreshes[i].Areas.AddRange(late.Areas);
                }
            }
            live = true;
            ExtensionMaps.SetRing(map, this);
            announcingBuild = true;
            try
            {
                ExtensionMaps.Publish(map, Vocabulary.Built, domain.Bounds);
                foreach (StepRegistration step in ExtensionRegistry.Steps)
                    if (!step.Failed) ExtensionMaps.Publish(map, step.Id, domain.Bounds);
                foreach (OwnerWork work in owners) PublishChannels(work, domain.Bounds);
            }
            finally { announcingBuild = false; }
            foreach (OwnerWork work in owners) QueueSections(work, null);
        }

        private void PublishChannels(OwnerWork work, CellRect area)
        {
            if (work.Steps.Count == 0 || work.Steps.TrueForAll(s => s.Failed)) return;
            foreach (ChannelRegistration channel in ExtensionRegistry.ChannelsOf(work.Owner))
                if (!channel.Failed) ExtensionMaps.Publish(map, channel.Id, area);
        }

        internal void Changed(string id, CellRect area)
        {
            if (!live || Array.IndexOf(Vocabulary.Events, id) >= 0) return;
            string producer = ExtensionRegistry.ProducerOf(id);
            if (producer != null && announcingBuild) return;
            for (int i = 0; i < owners.Count; i++)
            {
                OwnerWork work = owners[i];
                bool follows;
                if (producer != null)
                {
                    if (work.Owner == producer || !ExtensionRegistry.Linked(work.Owner, producer)) continue;
                    follows = work.Steps.Exists(s => !s.Failed && s.Follows(id))
                        || work.Layers.Exists(l => l.Scopes.Any(scope => ExtensionRegistry.ScopeOf(scope)?.Owner == producer));
                }
                else
                {
                    follows = work.Steps.Exists(s => !s.Failed && s.Follows(id));
                    if (!follows && work.Steps.Count == 0 && work.Layers.Count > 0)
                        follows = Vocabulary.IsTerrainChannel(id);
                }
                if (follows) Mark(i, area, id == Vocabulary.Frozen ? QuickSeconds : QuietSeconds);
            }
        }

        private bool announcingBuild;

        internal void HandOver()
        {
            foreach (Refresh refresh in refreshes)
            {
                refresh.Building?.Abort();
                refresh.Building = null;
                refresh.Dirty = refresh.Global = false;
                refresh.Areas.Clear();
            }
        }

        internal void Request(string owner, CellRect area)
        {
            int i = owners.FindIndex(o => o.Owner == owner);
            if (i < 0) return;
            bool all = area.minX <= domain.Bounds.minX && area.minZ <= domain.Bounds.minZ
                && area.maxX >= domain.Bounds.maxX && area.maxZ >= domain.Bounds.maxZ;
            Mark(i, area, all ? QuickSeconds : QuietSeconds);
        }

        private void Mark(int owner, CellRect area, float wait)
        {
            Refresh refresh = refreshes[owner];
            float now = Time.realtimeSinceStartup, until = now + wait;
            if (!refresh.Dirty) refresh.First = now;
            refresh.Due = refresh.Dirty ? Math.Min(Math.Max(refresh.Due, until), refresh.First + LongestWaitSeconds) : until;
            refresh.Dirty = true;
            CellRect bounds = domain.Bounds;
            if (area.minX <= bounds.minX && area.minZ <= bounds.minZ && area.maxX >= bounds.maxX && area.maxZ >= bounds.maxZ) refresh.Global = true;
            else if (refresh.Areas.Count >= 256) refresh.Global = true;
            else refresh.Areas.Add(area);
        }

        internal void Advance(bool paused)
        {
            int frame = Time.frameCount;
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                if (frame - retired[i].frame < 2) continue;
                ExtensionSections.Free(retired[i].layer);
                retired.RemoveAt(i);
            }
            for (int i = 0; i < owners.Count; i++)
            {
                OwnerWork work = owners[i];
                Refresh refresh = refreshes[i];
                if (paused)
                {
                    if (refresh.Building != null)
                    {
                        refresh.Building.Abort();
                        refresh.Building = null;
                        refresh.Dirty = refresh.Global = true;
                    }
                    continue;
                }
                if (refresh.Ready != null && frame > refresh.ReadyFrame) Publish(work, refresh, frame);
                if (refresh.Building != null) Step(work, refresh, frame);
                else if (refresh.Dirty && refresh.Ready == null && Time.realtimeSinceStartup >= refresh.Due)
                {
                    var pass = new Pass { Areas = refresh.Global ? null : new List<CellRect>(refresh.Areas) };
                    pass.Work = RefreshSteps(work, pass).GetEnumerator();
                    refresh.Building = pass;
                    refresh.Dirty = refresh.Global = false;
                    refresh.Areas.Clear();
                }
            }
            Redraw();
        }

        private void Step(OwnerWork work, Refresh refresh, int frame)
        {
            Pass pass = refresh.Building;
            var budget = Stopwatch.StartNew();
            bool more;
            do more = pass.Work.MoveNext();
            while (more && budget.Elapsed.TotalMilliseconds < FrameBudgetMs);
            double ms = budget.Elapsed.TotalMilliseconds;
            pass.SpentMs += ms;
            pass.LongestMs = Math.Max(pass.LongestMs, ms);
            if (more) return;
            pass.Work.Dispose();
            pass.Work = null;
            HorizonPerf.Record($"extension {work.Owner} rebuild total", pass.SpentMs);
            HorizonPerf.Record($"extension {work.Owner} rebuild longest frame", pass.LongestMs);
            refresh.Building = null;
            refresh.Ready = pass;
            refresh.ReadyFrame = frame;
        }

        private void Publish(OwnerWork work, Refresh refresh, int frame)
        {
            Pass pass = refresh.Ready;
            refresh.Ready = null;
            foreach (Strip strip in work.Shown)
            {
                if (!pass.Reused.Contains(strip.Layer)) retired.Add((frame, strip.Layer));
            }
            work.Shown = pass.Strips;
            CellRect area = domain.Bounds;
            if (pass.Areas != null && pass.Areas.Count > 0)
            {
                area = pass.Areas[0];
                foreach (CellRect a in pass.Areas)
                    area = CellRect.FromLimits(Math.Min(area.minX, a.minX), Math.Min(area.minZ, a.minZ), Math.Max(area.maxX, a.maxX), Math.Max(area.maxZ, a.maxZ));
            }
            foreach (StepRegistration step in work.Steps)
                if (!step.Failed) ExtensionMaps.Publish(map, step.Id, area);
            PublishChannels(work, area);
            QueueSections(work, pass.Areas);
        }

        private void QueueSections(OwnerWork work, List<CellRect> areas)
        {
            if (!work.Layers.Exists(l => l.JoinMapEdge)) return;
            Section[,] sections = ExtensionSections.SectionsOf(map);
            if (sections == null) return;
            var joined = new HashSet<Section>();
            foreach (Section section in sections)
            {
                if (section == null || !domain.AnyInBand(section.CellRect, EdgeJoin.Band)) continue;
                CellRect rect = section.CellRect;
                foreach (LayerRegistration registration in work.Layers)
                {
                    if (!registration.JoinMapEdge || !ExtensionRegistry.Presents(registration)) continue;
                    bool wanted;
                    try { wanted = registration.Wanted == null || registration.Wanted(map, rect); }
                    catch (Exception ex)
                    {
                        ExtensionRegistry.Fail(registration, ex);
                        continue;
                    }
                    if (wanted) joined.Add(section);
                    else if (!work.Joined.Contains(section)) continue;
                    if (areas != null && !areas.Exists(a => ExtensionSections.Gap(a, rect) <= ReuseDistance)) continue;
                    Queue(section, registration);
                }
            }
            foreach (Section section in work.Joined)
            {
                if (joined.Contains(section) || domain.AnyInBand(section.CellRect, EdgeJoin.Band)) continue;
                foreach (LayerRegistration registration in work.Layers)
                    if (registration.JoinMapEdge) Queue(section, registration);
            }
            work.Joined = joined;
        }

        private void Queue(Section section, LayerRegistration registration)
        {
            List<SectionLayer> layers = ExtensionSections.LayersOf(section);
            if (layers == null) return;
            foreach (SectionLayer layer in layers)
                if (registration.Type.IsInstanceOfType(layer)) redraw.Enqueue((layer, registration));
        }

        private void Redraw()
        {
            if (redraw.Count == 0) return;
            var budget = Stopwatch.StartNew();
            do
            {
                (SectionLayer layer, LayerRegistration registration) = redraw.Dequeue();
                try { layer.Regenerate(); }
                catch (Exception ex) { ExtensionRegistry.Fail(registration, ex); }
            }
            while (redraw.Count > 0 && budget.Elapsed.TotalMilliseconds < FrameBudgetMs);
            HorizonPerf.Record("extension edge sections (per frame)", budget.Elapsed.TotalMilliseconds);
        }

        private void ReleaseSections()
        {
            if (!live || !EndlessHorizon.Redraw.Possible(map)) return;
            foreach (OwnerWork work in owners)
            {
                foreach (Section section in work.Joined)
                {
                    try
                    {
                        EndlessHorizon.Redraw.Mark(map, section.botLeft, (ulong)RimWorld.MapMeshFlagDefOf.Terrain, adjacent: false);
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorOnce(HorizonLog.Prefix + "Returning a map edge section failed. " + ex, 0x45484A52);
                    }
                }
                work.Joined.Clear();
            }
        }

        internal int DrawLayers(CellRect view)
        {
            int calls = 0;
            foreach (OwnerWork work in owners)
            {
                foreach (Strip strip in work.Shown)
                {
                    if (!strip.Rect.Overlaps(view) || !ExtensionRegistry.Presents(strip.Registration)) continue;
                    try { calls += DrawStrip(strip); }
                    catch (Exception ex) { ExtensionRegistry.Fail(strip.Registration, ex); }
                }
            }
            return calls;
        }

        private static int DrawStrip(Strip strip)
        {
            SectionLayer layer = strip.Layer;
            if (!layer.Visible) return 0;
            if (strip.Registration.CallDrawLayer)
            {
                layer.DrawLayer();
                return 1;
            }
            int calls = 0;
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled && sm.mesh != null && sm.mesh.vertexCount > 0)
                {
                    Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, sm.material, sm.renderLayer);
                    calls++;
                }
            }
            return calls;
        }

        internal string Busy()
        {
            if (redraw.Count > 0) return "extension edge sections";
            for (int i = 0; i < owners.Count; i++)
            {
                Refresh refresh = refreshes[i];
                if (refresh.Dirty || refresh.Building != null || refresh.Ready != null) return "extension " + owners[i].Owner;
            }
            return null;
        }

        internal IEnumerable<bool> DisposeSteps()
        {
            ReleaseSections();
            live = false;
            ExtensionMaps.ClearRing(map, this);
            redraw.Clear();
            initial?.Abort();
            initial = null;
            var free = new HashSet<SectionLayer>();
            for (int i = 0; i < owners.Count; i++)
            {
                Refresh refresh = refreshes[i];
                refresh.Building?.Abort();
                refresh.Building = null;
                if (refresh.Ready != null)
                    foreach (Strip strip in refresh.Ready.Strips) free.Add(strip.Layer);
                refresh.Ready = null;
                foreach (Strip strip in owners[i].Shown) free.Add(strip.Layer);
                owners[i].Shown.Clear();
            }
            foreach ((int _, SectionLayer layer) in retired) free.Add(layer);
            retired.Clear();
            int freed = 0;
            foreach (SectionLayer layer in free)
            {
                ExtensionSections.Free(layer);
                if ((++freed & 7) == 0) yield return false;
            }
        }
    }
}
