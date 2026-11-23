# Endless Horizon API for modders

**API v1, frozen.** First released with Endless Horizon 1.0.0.4. v1 will not change; later versions only add.

Endless Horizon (EH) draws the land past the map edge. That land is a picture: no cells, things, pawns or saved data exist
there. This API lets your mod read that land as if it were part of the map, draw your own things on it, and take over how
some of it looks. Only EH decides what the land is.

Everything below is the whole contract. Anything not written here is internal and can change in any update. Do not use
reflection or Harmony on EH's internals: it is not supported and will break.

- [1. Getting started](#1-getting-started)
- [2. How EH sees the world](#2-how-eh-sees-the-world)
- [3. Recipes](#3-recipes)
- [4. Reference](#4-reference)
- [5. Vocabulary](#5-vocabulary)
- [6. Failures, timing and lifetimes](#6-failures-timing-and-lifetimes)
- [7. Compatibility promise](#7-compatibility-promise)

---

## 1. Getting started

### Load order

In `About.xml`, load after Endless Horizon:

```xml
<loadAfter>
  <li>jongsh.endlesshorizon</li>
</loadAfter>
```

### Two ways to depend on EH

**A. No reference (simplest for an optional feature).** Find the type by name and bind only what you use:

```csharp
Type info = GenTypes.GetTypeInAnyAssembly("EndlessHorizon.Api.HorizonInfo");
if (info != null)
{
    var isActive = (Func<Map, bool>)Delegate.CreateDelegate(typeof(Func<Map, bool>), info.GetMethod("IsActive"));
}
```

Binding by name alone is safe: no member name is ever overloaded.

**B. A reference, in a separate assembly.** Put every API call in an assembly that `LoadFolders.xml` only loads when EH is
active (`IfModActive="jongsh.endlesshorizon"`). Never put API calls in an assembly that also loads without EH.

The assembly's binding version is `1.0.0.4` and stays so for every later EH release, so your reference never breaks on an
EH update.

### Your owner name

Most calls take an `owner`: your packageId in lower case, for example `author.reefs`.

- IDs you register start with `owner + "."`, for example `author.reefs.field`.
- Refused (`false` or `null`): upper-case letters, spaces, the built-in names `waves`, `livingwaters`, `quietwonders`,
  `weatherreimagined`, `realisticrain`, and the first word of any EH ID: `terrain`, `space`, `surface`, `weather`,
  `light`, `fog`, `water`, `river`.
- Owners are not checked against your mod. Passing another mod's owner makes you act as that mod. Use your own.

### When to register

Register during startup: a `[StaticConstructorOnStartup]` class, or your `Mod` constructor's long event.

Registration closes when the first ring of any map starts to build, for the rest of the session. Later `Register...` and
`Replace` calls return `false` until the game restarts. `Observe`, `SetRegion` and `SetExternalDrawing` work at any time.

### Checking what is there

- `HorizonInfo.Version` is `1` for this API. It never goes down. A member added later is there when `Version` is at least
  the version that added it.
- `HorizonInfo.Has(id)` tells whether a channel, scope, replacement ID or draw stage is available right now.
- Check these, not EH's release number.

### Ground rules

- Main thread only.
- API calls never throw. A bad argument or an internal failure returns `false`, `null`, `0`, `NaN`, `IntVec3.Invalid`,
  or an empty rect or list. The exception is logged once per calling method.
- Reading never starts a build.

---

## 2. How EH sees the world

### One continuous world

The map and the land outside are read in the **same map-local coordinates**. A cell at `x = -5` is five cells past the
west edge. Inside you get the map's own values, outside you get EH's. You do not need to branch at the edge:
`HorizonWorld.TerrainAt(map, c)` works for both.

A vanilla name keeps its vanilla meaning: `TerrainAt` is the visible surface (bridges and ice included), `BaseTerrainAt`
is what lies under it.

### Playable area, ring and bounds

- **Playable area:** the cells of the map you can play on. Usually the map rectangle, but another mod can set any shape
  (`SetRegion`). Never assume a rectangle: ask `IsPlayable`, `NearestPlayable`, `DistanceFromPlayable`,
  `BoundaryCells`.
- **Ring:** the band of land EH builds around the playable area. `RingWidth` gives its width.
- **Bounds:** every cell you can read, the map plus the ring (`HorizonSpace.Bounds`). Its corner is negative.
- **Outside:** a cell EH draws (`IsOutside`). With a region set, cells inside the map rectangle can be outside. They
  answer EH's values for every query, and EH draws over them without hiding the map's own drawing there.
- Past the ring there is only fog. `DrawExtent` tells how far EH draws anything; use it to pad culling.

### Per map, never the current one

EH keeps everything per map. Never assume `Find.CurrentMap` or the camera. Draw callbacks get the map and the view.
Elsewhere, for example on a tick, ask `HorizonView.ViewOf(map)` for the view EH last drew that map with.

### Builds, refreshes and the two versions of your data

EH builds a ring for a map in steps over several frames while the old ring stays on screen, then swaps it in. After an
edit near the edge, only what is affected is rebuilt, again in the background.

That means your own data can exist in two versions:

- **current:** what the ring on screen was built with;
- **pending:** what your build step just made for the ring being built.

The rule:

1. Your build step writes **pending**.
2. While `HorizonInfo.IsBuilding(map)` is true, your channel and scope callbacks answer from **pending**. Otherwise from
   **current**.
3. When `Observe` reports your step ID, that ring is on screen: make pending current.

Queries inside build and regenerate callbacks answer from the ring being built; everything else answers from the ring on
screen. Notifications arrive after the change is on screen. This holds for EH's own values. Each mod rebuilds on its own,
so two mods' channels can come from different builds.

### Change notifications

`HorizonInfo.Observe(owner, (map, id, area) => ...)` is called once per change, after it is on screen:

| `id` | Meaning |
|---|---|
| `built` | A ring was built or replaced. |
| `disposed` | The ring of this map was freed. The map may be going away: free what you keep for it. |
| `settings` | EH's settings changed. |
| a channel ID | That channel changed in `area`. A registered channel changes when its owner's step result is on screen. |
| a build step ID | That step's result is on screen. |

EH watches the outside and the band of the map along the edge, not the inside of the map: use the game's own change
signals for that. A notification never arrives inside another one; what you call from `changed` takes effect later.

---

## 3. Recipes

### Read the land outside

```csharp
if (HorizonInfo.IsActive(map))
{
    TerrainDef ground = HorizonWorld.BaseTerrainAt(map, cell);   // also outside the map
    bool sea = HorizonWorld.Value(map, "water.kind", cell) == 3f;
    bool roofed = HorizonWorld.RoofAt(map, cell)?.isThickRoof == true;
}
```

For many cells use `Copy` or `CopyTerrain`; they fill your array in row order:
`index = (z - area.minZ) * area.Width + (x - area.minX)`. One call reads one point in time.

### Draw your own layer outside (a layer over Waves' sea)

```csharp
[StaticConstructorOnStartup]
static class ReefsOutside
{
    static ReefsOutside()
    {
        if (!HorizonInfo.Has("waves.shore")) return;
        HorizonExtensions.RegisterLayer("author.reefs", typeof(SectionLayer_Reefs), new[] { "waves.shore" },
            wanted: (map, strip) => TouchesSea(map, strip), joinMapEdge: true, callDrawLayer: false);
    }

    static bool TouchesSea(Map map, CellRect strip)
    {
        foreach (IntVec3 c in strip)
            if (HorizonWorld.Value(map, "water.kind", c) == 3f) return true;
        return false;
    }
}
```

EH creates your `SectionLayer` over the strips of land outside, regenerates, draws and frees it. Inside `Regenerate`, read
`HorizonWorld.BaseTerrainAt(map, c)` instead of the terrain grid and drop `InBounds` checks: the strip lies outside the
map and may reach past `Bounds`, where answers are invalid.

### Build data in the background and publish it

```csharp
const string Owner = "author.depth", Step = Owner + ".field";
static readonly ConditionalWeakTable<Map, Field> current = new(), pending = new();

static void Register()
{
    HorizonExtensions.RegisterBuildStep(Owner, Step, Build, new[] { "terrain.base" });
    HorizonExtensions.RegisterChannel(Owner, Owner + ".depth", Read);
    HorizonInfo.Observe(Owner, Changed);
}

static IEnumerable<bool> Build(Map map)
{
    var field = new Field(HorizonSpace.Bounds(map));
    foreach (bool more in field.FillInSlices(map)) yield return false;   // keep each slice short
    pending.AddOrUpdate(map, field);                                     // only a finished field becomes pending
}

static float Read(Map map, IntVec3 c)
{
    Field f = null;
    if (HorizonInfo.IsBuilding(map)) pending.TryGetValue(map, out f);
    if (f == null) current.TryGetValue(map, out f);
    return f?.At(c) ?? float.NaN;                                         // NaN where you have no answer
}

static void Changed(Map map, string id, CellRect area)
{
    if (id == Step && pending.TryGetValue(map, out Field f)) { current.AddOrUpdate(map, f); pending.Remove(map); }
    if (id == "disposed") { current.Remove(map); pending.Remove(map); }
}
```

- Steps run in registration order, after EH's land is final and before layers.
- `dependsOn` only decides when your step runs again: when a listed channel or another mod's step changes, or on
  `RequestRefresh`. It does not order steps.
- If EH disposes your enumerator early, the build was cancelled. A cancelled build is never announced.

### Draw with your own shaders or textures

```csharp
HorizonExtensions.RegisterDraw(Owner, "before", (map, view) => BindTextures(map));
HorizonExtensions.RegisterDraw(Owner, "after", (map, view) => RestoreTextures());
```

Set per-map shader globals in `before` and undo them in `after`. With several maps in one frame each map runs its own
`before` … `after`, so anything you leave set leaks into the next map. EH never restores render state for you.

### Take over a built-in presentation

```csharp
HorizonExtensions.Replace(Owner, "livingwaters");   // you draw Living Waters' water outside from now on
```

EH stops drawing that presentation but keeps deciding the land and keeps serving its channels. The first mod to replace
an ID gets it. The player sees the replacement in EH's settings and can undo it; `StateOf(id)` tells you which.

### Several maps on one screen, or a non-rectangular map

```csharp
HorizonSpace.SetRegion(map, Owner, cell => IsInsideMyShape(map, cell), myConvexDrawArea);
HorizonView.SetExternalDrawing(map, Owner, true);

// every frame, for each visible map, in that map's own coordinates:
if (!HorizonInfo.FogShown(map)) DrawMyClippers(map, HorizonInfo.RingWidth(map));
HorizonView.Draw(map, viewInThatMapsCoordinates);
```

- `drawArea` is a convex polygon in map coordinates (x, z) so neighbouring maps do not draw over each other.
- With external drawing on, EH draws that map only when you call `Draw`, and its background work only advances then.
- EH keeps vanilla's edge clippers off. If the fog is off (`FogShown` false), cover the void past the ring yourself.
- Inside the map rectangle, hide the map's own drawing in cells you made non-playable; EH draws over them but does not
  hide them.

---

## 4. Reference

Namespace `EndlessHorizon.Api`: five static classes, game and BCL types only.

### HorizonInfo

| Member | Meaning |
|---|---|
| `int Version { get; }` | `1`. Never goes down. |
| `bool Has(string id)` | The channel, scope, replacement ID or draw stage exists and has not failed. A replaced or restored presentation still counts. |
| `bool IsActive(Map map)` | EH draws this map. |
| `bool IsBuilt(Map map)` | A complete ring exists. |
| `bool IsBuilding(Map map)` | EH is building a ring of this map that is not on screen yet. Answer from pending while true. |
| `int RingWidth(Map map)` | Ring width in cells, `0` when not active. |
| `int FogStart(Map map)` | Cells past the edge where the fog starts to thicken. |
| `bool FogShown(Map map)` | The edge fog is drawn. If false, something must cover the void past the ring. |
| `int Revision(Map map, string channel)` | Goes up after a change of that channel is on screen. `-1` for an unknown channel. |
| `IDisposable Observe(string owner, Action<Map, string, CellRect> changed)` | Change notifications. Dispose to stop. |
| `void RequestRefresh(Map map, string owner, CellRect area)` | Rebuild your registrations around `area`. Debounced: each request pushes it back, at most 10 seconds after the first. A request during a rebuild starts the next one. Does not rebuild EH's land. |
| `void RedrawMap(Map map, CellRect area, ulong meshFlags)` | Redraw the map's own sections there without EH treating it as a change at the edge. |
| `void Report(string owner, string key, string value)` | A line in EH's diagnostics. Not saved. |

### HorizonSpace

| Member | Meaning |
|---|---|
| `CellRect Bounds(Map map)` | Every readable cell: the map plus the ring. |
| `int DrawExtent(Map map)` | Cells past the edge beyond which only uniform fog is drawn. |
| `bool IsPlayable(Map map, IntVec3 c)` | Part of the playable area. |
| `bool IsOutside(Map map, IntVec3 c)` | A cell EH draws. |
| `IntVec3 NearestPlayable(Map map, IntVec3 c)` | The nearest playable cell; ties are always broken the same way. |
| `float DistanceFromPlayable(Map map, IntVec3 c)` | Cells from centre to the nearest playable centre, `0` inside. |
| `IReadOnlyList<IntVec3> BoundaryCells(Map map)` | Playable cells with a non-playable cardinal neighbour. The list never changes; ask again after a region or ring width change. |
| `bool SetRegion(Map map, string owner, Predicate<IntVec3> playable, IReadOnlyList<Vector2> drawArea)` | Set the playable area and where this map may draw. See below. |
| `bool ClearRegion(Map map, string owner)` | Back to the rectangle. Only the owner that set it. |

`SetRegion`:

- `playable` is called once for every map cell during the call and not kept. At least one cell must be playable.
- `drawArea` is a convex polygon; `null` means no limit, an empty list means draw nowhere.
- Bad input (non-finite or non-convex `drawArea`, a `playable` that throws, no playable cell) returns `false` and keeps
  the current region.
- Everything EH draws follows the region. The same values again do nothing; other values rebuild in the background while
  the old ring stays on screen. While it rebuilds, build callbacks see the new region and everything else the old one.
- A region survives settings changes and rebuilds, is forgotten with the map, and is never saved. One owner at a time.

### HorizonWorld

| Member | Inside the playable area | Outside |
|---|---|---|
| `TerrainDef TerrainAt(Map, IntVec3)` | `TerrainGrid.TerrainAt` | Visible surface: flood water (not under a bridge), then road or bridge, then ice, then ground. Flood water from 1.0.0.4. |
| `TerrainDef BaseTerrainAt(Map, IntVec3)` | `TerrainGrid.BaseTerrainAt` | The ground. Sea under ice or a bridge is still sea. |
| `ThingDef RockAt(Map, IntVec3)` | Mineable natural rock there. | The rock EH draws, `null` for open ground. |
| `RoofDef RoofAt(Map, IntVec3)` | The roof grid. | Natural roof EH gives the cell: thick, thin or none. |
| `bool FoggedAt(Map, IntVec3)` | The fog grid. | The fog EH shows there. |
| `Plant PlantAt(Map, IntVec3)` | The plant. | The map plant whose picture EH shows there; its `Position` is not `c`. |
| `Vector3 WaterFlowAt(Map, IntVec3)` | `WaterInfo.GetWaterMovement` | The continued flow, same units. |

| Member | Meaning |
|---|---|
| `int RiverCount(Map map)` | Rivers continued outside. Indices `0 .. RiverCount - 1`, stable for one ring. |
| `bool TryGetRiver(Map map, int river, out Vector2 start, out Vector2 outward, out float width)` | Where it crosses the edge, the direction it leaves in, and its width in cells. |
| `bool TryRiverPath(Map map, IntVec3 c, out int river, out float along)` | True for an outside cell of a continued river; `along` is cells from the crossing, growing outward. |
| `float Value(Map map, string channel, IntVec3 c)` | One channel value. `NaN` outside `Bounds` or without an answer. |
| `bool Copy(Map map, string channel, CellRect area, float[] destination)` | A channel over an area, row order. |
| `bool CopyTerrain(Map map, string channel, CellRect area, TerrainDef[] destination)` | `terrain.top` or `terrain.base` over an area, row order. |

- Roofs and fog are what the player sees. `RoofAt` includes thin natural roofs outside; test `isThickRoof` if only thick
  ones matter. `FoggedAt` matches the screen, including rock faces revealed when the map's edge is mined.
- `PlantAt` returns the live map plant, not a copy. Never change it. If you keep it past the frame, check `Spawned`.
- For `Copy`, the array must hold at least `area.Area` values; it is yours. Cells outside `Bounds` get `NaN` or `null`.

### HorizonExtensions

| Member | Meaning |
|---|---|
| `bool RegisterLayer(string owner, Type sectionLayerType, string[] scopes, Func<Map, CellRect, bool> wanted, bool joinMapEdge, bool callDrawLayer)` | EH creates, regenerates, draws and frees your layer over the strips outside. |
| `bool RegisterBuildStep(string owner, string id, Func<Map, IEnumerable<bool>> build, string[] dependsOn)` | Background work done before layers regenerate. |
| `bool RegisterChannel(string owner, string id, Func<Map, IntVec3, float> read)` | Publish a value per cell to every mod. |
| `bool RegisterScope(string owner, string id, Func<Map, IDisposable> enter)` | While entered, another system's own lookups answer outside cells. |
| `IDisposable Enter(Map map, string scope)` | Enter a scope; `null` if unavailable. `using` accepts `null`. |
| `bool RegisterDraw(string owner, string stage, Action<Map, CellRect> draw)` | A callback in one draw stage. |
| `bool Replace(string owner, string id)` | Take over a presentation. First owner wins. |
| `string StateOf(string id)` | `absent`, `active`, `unrecognized`, `failed`, `replaced:<owner>` or `restored` (undone by the player). |

`RegisterLayer`:

- The type needs a public `(Section)` constructor.
- `scopes` are entered around every `Regenerate`. `wanted` filters strips (`null`: all).
- `joinMapEdge`: the map's own sections along the edge also regenerate inside your scopes, so both sides meet.
- `callDrawLayer`: EH calls your `DrawLayer`; otherwise it draws the finished sub-meshes and never calls it.
- `Visible` is respected.
- The same type again does nothing: `true` for the owner that registered it, `false` for any other.

`RegisterBuildStep`:

- EH runs your enumerator within its frame budget, but only stops between two `yield`s. Splitting the work is your job.
  Each mod's time is shown in EH's diagnostics.
- A dependency that would make two mods rebuild each other forever is ignored and logged. A layer using another mod's
  scope follows that mod the same way. Scope links are taken first, then `dependsOn` links in registration order; the
  one that would close the loop is dropped.
- An ID nothing provides is listed in diagnostics and never fires (its mod may simply be absent).

Scopes nest, and disposing restores what was active before, also after an exception. A scope can be entered by EH while
it builds and by any mod at any time: pick what it answers with `IsBuilding`.

Draw stages, in order:

| Stage | When |
|---|---|
| `before` | Before anything of this map. Bind per-map shader globals. |
| `afterTerrain` | After terrain and water depth. |
| `beforeLighting` | After things, snow and registered layers. |
| `beforeFog` | After lighting. |
| `after` | Last. Always runs when `before` ran, also after a failure. Restore globals. |

Stages order the submission of draw calls; what ends up on top is still decided by altitude and render queue.

### HorizonView

| Member | Meaning |
|---|---|
| `bool SetExternalDrawing(Map map, string owner, bool external)` | You draw this map's ring yourself; EH stops drawing it on its own. One owner at a time. |
| `bool Draw(Map map, CellRect view)` | Draw that map for a view in its own coordinates, building the ring if needed. Several calls in a frame advance EH's work once. `false` for a map that is not externally drawn. |
| `Map DrawingMap { get; }` | The map EH is drawing right now, else `null`. |
| `CellRect ViewOf(Map map)` | The view EH last drew that map with, in its coordinates. Empty until it has been drawn. |

If you stop calling `Draw`, that map shows nothing from EH and its ring stays as it was. Turn external drawing off to
hand the map back.

---

## 5. Vocabulary

New IDs may be added in any release. An existing ID never changes its meaning.

### Channels

| ID | Value |
|---|---|
| `terrain.top`, `terrain.base` | `CopyTerrain` only. |
| `space.outside` | 1 outside, 0 otherwise. |
| `space.distance` | As `DistanceFromPlayable`. |
| `surface.rock` | 1 rock, 0 open. |
| `surface.roof` | 0 none, 1 roof that is not thick, 2 thick roof. |
| `surface.fogged` | 1 fogged, 0 seen. |
| `surface.frozen` | 1 thin ice on water, 0 otherwise. |
| `surface.polluted` | 1 polluted, 0 clean. |
| `surface.snow` | Snow depth, 0 to 1. |
| `weather.exposed` | 1 where rain and snow can fall, 0 otherwise. |
| `light.glow` | Ground glow, 0 to 1, sky included. |
| `fog.opacity` | Edge fog opacity without the moving clouds, 0 to 1. |
| `water.kind` | 0 land, 1 still fresh water, 2 moving fresh water, 3 sea, 4 marsh. |
| `water.deep` | 1 impassable water, 0 otherwise. |
| `water.covered` | 1 water under a bridge or floor, 0 otherwise. |
| `water.shoreDistance` | Cells to the shore: positive in water, negative on land, limited to ±32. |
| `water.flow.x`, `water.flow.z` | As `WaterFlowAt`. |
| `river.index` | Index of the continued river, or -1. |
| `river.along` | As `TryRiverPath`, or `NaN`. |

### Channels from the built-in water integrations

Only while that integration is on (`Has` is true). Values mean what that mod's installed version means.

| ID | Value |
|---|---|
| `waves.kind` | Waves' sea classification. |
| `waves.shoreDistance`, `waves.calm`, `waves.reach`, `waves.clearance` | Waves' shore field continued outside. |
| `livingwaters.kind`, `livingwaters.flavor` | Living Waters' classification. |
| `livingwaters.shoreDistance`, `livingwaters.depth`, `livingwaters.seaDistance` | Its distance fields continued outside. |
| `livingwaters.along` | Its along-river coordinate. |
| `livingwaters.marshDistance`, `livingwaters.marshReach` | Its marsh fields. |
| `livingwaters.lake` | Its lake body index, or -1. |

### Scopes

| ID | Inside it |
|---|---|
| `waves.shore` | Waves' `ShoreField` lookups answer outside. |
| `livingwaters.water` | Living Waters' `WaterField` lookups answer outside. |

### Replacement IDs

| ID | What you take over |
|---|---|
| `waves`, `livingwaters`, `quietwonders`, `weatherreimagined`, `realisticrain` | That built-in integration's presentation. |
| `water.surface` | EH's outside water surface meshes. |
| `water.depth` | EH's outside water depth pass. |

---

## 6. Failures, timing and lifetimes

**What happens when my code throws?** Only the registration that threw is switched off for the session, and EH logs it
as an error with your owner and the full exception. Draw callbacks are the exception: when one of your draw callbacks
throws, all of yours stop, but your `after` still runs once for a frame in which your `before` ran. Other mods keep
running.

**A step of mine failed. What do others see?** It is never announced again. What it announced last stays current, and
steps that depend on it are not run again for it.

**The mod that replaced a presentation failed.** EH does not take the presentation back by itself (the rest of that mod
may still be drawing). The player can undo the replacement in EH's settings.

**Can my build step freeze the game?** EH only stops your step between `yield`s. Long work before a `yield` runs to the
end in that frame.

**I request a refresh every frame.** Each request pushes the rebuild back, but it starts at most 10 seconds after the
first request. A request during a rebuild starts the next one.

**Are bulk reads consistent?** One `Copy` call reads one point in time. Two calls can differ if a change was shown in
between. Other mods' channels can come from a different build than EH's.

**Can I keep what the API gives me?**

| What | How long |
|---|---|
| `PlantAt` result | The live plant. Check `Spawned` before using a kept one. Never change it. |
| `BoundaryCells` list | Never changes, but is stale after a region or ring width change. |
| `Observe` / `Enter` result | Dispose once or more, also after the map is gone. |
| Your per-map data | Until `disposed` for that map. Keep it in a weak table keyed by `Map`. |

**SetRegion during a rebuild?** Build callbacks see the new region, everything else the region on screen until the new
ring replaces it.

**Registration came too late.** Registration closes for the session when the first ring of any map starts to build.
Restarting the game is the only way back.

---

## 7. Compatibility promise

- This document is the contract. Behaviour it does not state may change in any update, also to fix a bug. A fix never
  changes a stated behaviour; if a stated behaviour turns out to be wrong, the changelog says what changed and from which
  version.
- v1 types, members, meanings, coordinates and draw stages never change and never go away.
- New channels, scopes and replacement IDs can appear in any release. Check `Has`.
- A later API version may add members, always under new names. Check `Version`.
- EH's own built-in integrations use this API and nothing else. Whatever they do, you can do too.
