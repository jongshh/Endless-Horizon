using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EndlessHorizon
{
    internal abstract class Registration
    {
        internal string Owner;
        internal bool Failed;

        internal abstract string Name { get; }
    }

    internal sealed class LayerRegistration : Registration
    {
        internal Type Type;
        internal string[] Scopes;
        internal Func<Map, CellRect, bool> Wanted;
        internal bool JoinMapEdge, CallDrawLayer;

        internal override string Name => "layer " + Type.FullName;
    }

    internal sealed class StepRegistration : Registration
    {
        internal string Id;
        internal Func<Map, IEnumerable<bool>> Build;
        internal string[] DependsOn;
        internal readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.Ordinal);

        internal bool Follows(string id) => Array.IndexOf(DependsOn, id) >= 0 && !Ignored.Contains(id);

        internal override string Name => "build step " + Id;
    }

    internal sealed class ChannelRegistration : Registration
    {
        internal string Id;
        internal Func<Map, IntVec3, float> Read;

        internal override string Name => "channel " + Id;
    }

    internal sealed class ScopeRegistration : Registration
    {
        internal string Id;
        internal Func<Map, IDisposable> Enter;

        internal override string Name => "scope " + Id;
    }

    internal sealed class DrawRegistration : Registration
    {
        internal string Stage;
        internal Action<Map, CellRect> Draw;

        internal override string Name => "draw " + Stage;
    }

    internal static class ExtensionRegistry
    {
        internal static readonly List<LayerRegistration> Layers = new List<LayerRegistration>();
        internal static readonly List<StepRegistration> Steps = new List<StepRegistration>();
        private static readonly List<DrawRegistration> draws = new List<DrawRegistration>();
        private static readonly Dictionary<string, List<DrawRegistration>> drawsByStage = new Dictionary<string, List<DrawRegistration>>();
        private static readonly Dictionary<string, ChannelRegistration> channels = new Dictionary<string, ChannelRegistration>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ScopeRegistration> scopes = new Dictionary<string, ScopeRegistration>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, LayerRegistration> layerByType = new Dictionary<Type, LayerRegistration>();
        private static readonly Dictionary<string, string> replacedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> builtIn = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> failures = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool closed;

        internal static string SettingUp;

        internal static bool Closed => closed;

        internal static void Close()
        {
            if (closed) return;
            closed = true;
            try { CheckLinks(); }
            catch (Exception ex) { Log.Error($"{HorizonLog.Prefix}Checking the build step links failed; every link is followed as given. {ex}"); }
        }

        internal static bool OwnerAllowed(string owner)
        {
            if (owner.NullOrEmpty() || owner != owner.ToLowerInvariant() || owner.Any(char.IsWhiteSpace)) return false;
            if (Array.IndexOf(Vocabulary.Namespaces, owner) >= 0) return false;
            return Array.IndexOf(Vocabulary.BuiltInOwners, owner) < 0 || owner == SettingUp;
        }

        internal static bool CanRegister(string owner) => !closed && OwnerAllowed(owner);

        private static bool CanName(string owner, string id)
        {
            if (!CanRegister(owner) || id == null || id.Length <= owner.Length + 1 || !id.StartsWith(owner + ".", StringComparison.Ordinal)) return false;
            return !Vocabulary.IsChannel(id) && Array.IndexOf(Vocabulary.Replacements, id) < 0;
        }

        internal static bool AddLayer(string owner, Type type, string[] scopeIds, Func<Map, CellRect, bool> wanted, bool join, bool callDraw)
        {
            if (!CanRegister(owner) || type == null) return false;
            if (layerByType.TryGetValue(type, out LayerRegistration known)) return known.Owner == owner;
            if (type.IsAbstract || !typeof(SectionLayer).IsAssignableFrom(type) || type.GetConstructor(new[] { typeof(Section) }) == null) return false;
            if (!ExtensionSections.Ready) return false;
            if (join && !EdgeJoin.Patch(type)) return false;
            var layer = new LayerRegistration
            {
                Owner = owner, Type = type, Wanted = wanted, JoinMapEdge = join, CallDrawLayer = callDraw,
                Scopes = scopeIds?.ToArray() ?? Array.Empty<string>(),
            };
            Layers.Add(layer);
            layerByType.Add(type, layer);
            return true;
        }

        internal static bool AddStep(string owner, string id, Func<Map, IEnumerable<bool>> build, string[] dependsOn)
        {
            if (build == null || !CanName(owner, id) || Steps.Exists(s => s.Id == id)) return false;
            Steps.Add(new StepRegistration { Owner = owner, Id = id, Build = build, DependsOn = dependsOn?.ToArray() ?? Array.Empty<string>() });
            return true;
        }

        internal static bool AddChannel(string owner, string id, Func<Map, IntVec3, float> read)
        {
            if (read == null || !CanName(owner, id) || channels.ContainsKey(id)) return false;
            channels.Add(id, new ChannelRegistration { Owner = owner, Id = id, Read = read });
            return true;
        }

        internal static bool AddScope(string owner, string id, Func<Map, IDisposable> enter)
        {
            if (enter == null || !CanName(owner, id) || scopes.ContainsKey(id)) return false;
            scopes.Add(id, new ScopeRegistration { Owner = owner, Id = id, Enter = enter });
            return true;
        }

        internal static bool AddDraw(string owner, string stage, Action<Map, CellRect> draw)
        {
            if (draw == null || !CanRegister(owner) || Array.IndexOf(Vocabulary.Stages, stage) < 0) return false;
            var registration = new DrawRegistration { Owner = owner, Stage = stage, Draw = draw };
            draws.Add(registration);
            if (!drawsByStage.TryGetValue(stage, out List<DrawRegistration> list)) drawsByStage[stage] = list = new List<DrawRegistration>();
            list.Add(registration);
            return true;
        }

        internal static bool Replace(string owner, string id)
        {
            if (!CanRegister(owner) || id == null || id == owner || Array.IndexOf(Vocabulary.Replacements, id) < 0) return false;
            if (replacedBy.TryGetValue(id, out string first)) return first == owner;
            replacedBy.Add(id, owner);
            return true;
        }

        internal static void SetBuiltIn(string id, string state) => builtIn[id] = state;

        private static bool ReplaceableNow(string id) => id == Vocabulary.WaterSurface || id == Vocabulary.WaterDepth
            || builtIn.TryGetValue(id, out string state) && state == "active" && !failures.ContainsKey(id);

        internal static IEnumerable<KeyValuePair<string, string>> Replaced => replacedBy;

        internal static bool IsReplaced(string id) =>
            replacedBy.Count > 0 && replacedBy.ContainsKey(id) && HorizonSettings.Current?.Restored(id) != true;

        internal static string StateOf(string id)
        {
            if (id == null) return "absent";
            if (Array.IndexOf(Vocabulary.Replacements, id) >= 0)
            {
                bool own = id == Vocabulary.WaterSurface || id == Vocabulary.WaterDepth;
                string state = own ? "active" : builtIn.TryGetValue(id, out string known) ? known : "absent";
                if (state != "active") return state;
                if (failures.ContainsKey(id)) return "failed";
                if (!replacedBy.TryGetValue(id, out string owner)) return "active";
                return HorizonSettings.Current?.Restored(id) == true ? "restored" : "replaced:" + owner;
            }
            if (Vocabulary.IsChannel(id) || Array.IndexOf(Vocabulary.Stages, id) >= 0) return "active";
            Registration found = channels.TryGetValue(id, out ChannelRegistration channel) ? channel
                : scopes.TryGetValue(id, out ScopeRegistration scope) ? scope
                : (Registration)Steps.Find(s => s.Id == id);
            return found == null ? "absent" : found.Failed ? "failed" : "active";
        }

        internal static bool Has(string id)
        {
            if (id == null) return false;
            if (Vocabulary.IsChannel(id) || Array.IndexOf(Vocabulary.Stages, id) >= 0) return true;
            if (Array.IndexOf(Vocabulary.Replacements, id) >= 0) return ReplaceableNow(id);
            return channels.TryGetValue(id, out ChannelRegistration channel) && !channel.Failed
                || scopes.TryGetValue(id, out ScopeRegistration scope) && !scope.Failed;
        }

        internal static bool HasRevision(string id) =>
            id != null && (Vocabulary.IsChannel(id) || channels.ContainsKey(id) || Steps.Exists(s => s.Id == id));

        internal static LayerRegistration LayerOf(Type type)
        {
            for (Type t = type; t != null && t != typeof(SectionLayer); t = t.BaseType)
                if (layerByType.TryGetValue(t, out LayerRegistration layer)) return layer;
            return null;
        }

        internal static ChannelRegistration ChannelOf(string id) => channels.TryGetValue(id, out ChannelRegistration channel) ? channel : null;

        internal static IEnumerable<ChannelRegistration> ChannelsOf(string owner) => channels.Values.Where(c => c.Owner == owner);

        internal static string ProducerOf(string id)
        {
            if (id == null) return null;
            StepRegistration step = Steps.Find(s => s.Id == id);
            if (step != null) return step.Owner;
            return channels.TryGetValue(id, out ChannelRegistration channel) ? channel.Owner : null;
        }

        private static readonly HashSet<(string follower, string producer)> links = new HashSet<(string, string)>();

        internal static bool Linked(string follower, string producer) => links.Contains((follower, producer));

        private static void CheckLinks()
        {
            links.Clear();
            var unknown = new List<string>();
            foreach (LayerRegistration layer in Layers)
            {
                foreach (string id in layer.Scopes)
                {
                    string producer = ScopeOf(id)?.Owner;
                    if (producer == null || producer == layer.Owner) continue;
                    if (!Reaches(producer, layer.Owner)) links.Add((layer.Owner, producer));
                    else Log.Warning($"{HorizonLog.Prefix}{layer.Owner}: {layer.Name} uses scope {id}, but {producer} already follows {layer.Owner}; it is not rebuilt when {producer} changes.");
                }
            }
            foreach (StepRegistration step in Steps)
            {
                step.Ignored.Clear();
                foreach (string id in step.DependsOn)
                {
                    if (id == null) continue;
                    string producer = ProducerOf(id);
                    if (producer == null)
                    {
                        if (!Vocabulary.IsChannel(id)) unknown.Add(step.Owner + " " + step.Id + " -> " + id);
                        continue;
                    }
                    if (producer == step.Owner || links.Contains((step.Owner, producer))) continue;
                    if (Reaches(producer, step.Owner))
                    {
                        step.Ignored.Add(id);
                        Log.Warning($"{HorizonLog.Prefix}{step.Owner}: build step {step.Id} depends on {id}, but {producer} already follows {step.Owner}; that dependency is ignored so neither rebuilds the other for ever.");
                        continue;
                    }
                    links.Add((step.Owner, producer));
                }
            }
            if (unknown.Count > 0) ExtensionMaps.Report("endlesshorizon", "dependsOn without a provider", string.Join("; ", unknown));
        }

        private static bool Reaches(string from, string to)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                string at = stack.Pop();
                if (at == to) return true;
                if (!seen.Add(at)) continue;
                foreach ((string follower, string producer) in links)
                    if (follower == at) stack.Push(producer);
            }
            return false;
        }

        private static readonly HashSet<string> drawsOff = new HashSet<string>(StringComparer.Ordinal);

        internal static bool DrawsOff(string owner) => drawsOff.Contains(owner);

        internal static ScopeRegistration ScopeOf(string id) => id != null && scopes.TryGetValue(id, out ScopeRegistration scope) ? scope : null;

        internal static bool Presents(LayerRegistration layer) =>
            !layer.Failed && !failures.ContainsKey(layer.Owner) && !IsReplaced(layer.Owner);

        internal static List<DrawRegistration> DrawsAt(string stage) => drawsByStage.TryGetValue(stage, out List<DrawRegistration> list) ? list : null;

        internal static IEnumerable<string> Owners =>
            Steps.Select(s => s.Owner).Concat(Layers.Select(l => l.Owner)).Distinct();

        internal static IEnumerable<Registration> All =>
            Steps.Cast<Registration>().Concat(Layers).Concat(channels.Values).Concat(scopes.Values).Concat(draws);

        internal static string FailureOf(string owner) => failures.TryGetValue(owner, out string reason) ? reason : null;

        internal static void Fail(Registration registration, Exception ex)
        {
            if (registration.Failed) return;
            registration.Failed = true;
            if (registration is DrawRegistration) drawsOff.Add(registration.Owner);
            if (Array.IndexOf(Vocabulary.BuiltInOwners, registration.Owner) >= 0) failures[registration.Owner] = registration.Name + ": " + ex.GetType().Name;
            string what = registration is DrawRegistration ? "its draw callbacks are" : "it is";
            Log.Error($"{HorizonLog.Prefix}{registration.Owner}: {registration.Name} failed and {what} off for this session. {ex}");
        }
    }
}
