using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class GenerationGeological : IDisposable
    {
        [ThreadStatic] private static GenerationGeological active;
        private static Type landformType;
        private static bool hooked;
        private readonly GenerationContext context;
        private readonly object tile;
        private readonly IList graphs;
        private List<object> writeOrder = new();
        private readonly HashSet<UnityEngine.Object> owned = new();
        private readonly Dictionary<string, object> saved = new();
        private readonly List<DictionaryEntry> savedNamed = new();
        private readonly Dictionary<(object, string), object> inputSuppliers = new();
        private readonly Dictionary<object, Dictionary<string, Delegate>> graphInputs = new();
        private bool entered;
        private bool namedCaptured;
        private long scopeStarted;
        private MethodInfo priority;
        private object priorityOptions;
        private Func<TerrainDef, TerrainDef, object, TerrainDef> priorityCall;
        private readonly Times timing = new();
        internal readonly int Seed;
        internal string Stage = "load";
        internal bool FiniteRiver;
        internal bool TransitionsApplied;
        internal Func<double, double, double> ElevationInput, FertilityInput, CavesInput;
        internal Func<double, double, TerrainDef> TerrainInput;
        internal Func<double, double, RimWorld.BiomeDef> BiomeInput;

        internal sealed class Times
        {
            internal double Load, Traverse, Enter, Setup, Elevation, Fertility, Caves, Biome, Terrain;
            internal int Scopes, FastGrids, ProxyGrids;
            internal string Priority = "unused";

            internal void Add(string stage, double ms)
            {
                Scopes++;
                switch (stage)
                {
                    case "elevation cells": Elevation += ms; break;
                    case "fertility cells": Fertility += ms; break;
                    case "caves cells": Caves += ms; break;
                    case "terrain cells": Terrain += ms; break;
                    default:
                        if (stage != null && stage.StartsWith("OutputBiomeGrid", StringComparison.Ordinal)) Biome += ms;
                        else Setup += ms;
                        break;
                }
            }
        }

        private static readonly ConditionalWeakTable<Map, Times> timesByMap = new();

        internal static Times TimesOf(Map map) => map != null && timesByMap.TryGetValue(map, out Times t) ? t : null;

        private static double MsSince(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

        private static object Get(object o, string name)
        {
            Type t = o as Type ?? o.GetType(); object instance = o is Type ? null : o;
            PropertyInfo p = AccessTools.Property(t, name);
            return p != null ? p.GetValue(instance) : AccessTools.Field(t, name)?.GetValue(instance);
        }
        private static void Set(object o, string name, object value)
        {
            Type t = o as Type ?? o.GetType(); object instance = o is Type ? null : o;
            PropertyInfo p = AccessTools.Property(t, name);
            if (p != null) p.SetValue(instance, value); else AccessTools.Field(t, name).SetValue(instance, value);
        }
        private static object Call(object o, string name, params object[] args)
        {
            Type t = o as Type ?? o.GetType();
            var methods = new List<MethodInfo>();
            for (Type current = t; current != null; current = current.BaseType) methods.AddRange(AccessTools.GetDeclaredMethods(current));
            MethodInfo method = methods.FirstOrDefault(m => m.Name == name && !m.IsGenericMethodDefinition
                && m.GetParameters().Length >= args.Length && m.GetParameters().Skip(args.Length).All(p => p.IsOptional)
                && m.GetParameters().Take(args.Length).Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
            if (method == null) throw new MissingMethodException(t.FullName, name);
            object[] supplied = method.GetParameters().Select((p,i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
            return method.Invoke(o is Type ? null : o, supplied);
        }

        internal static void Setup(Harmony harmony)
        {
            if (hooked) return;
            landformType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.Landform");
            if (landformType == null) return;
            foreach (string kind in new[] { "Elevation", "Fertility", "Caves", "Terrain", "BiomeGrid" })
            {
                Type type = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.NodeInput" + kind);
                MethodInfo target = type == null ? null : AccessTools.DeclaredMethod(type, "Calculate");
                if (target == null) throw new MissingMethodException("GL input " + kind);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(GenerationGeological), nameof(InputPrefix)));
            }
            hooked = true;
        }

        internal static GenerationGeological TryCreate(GenerationContext context)
        {
            if (AccessTools.TypeByName("GeologicalLandforms.GraphEditor.Landform") == null) return null;
            Setup(new Harmony("jongsh.endlesshorizon.generation"));
            if (MapGenerator.mapBeingGenerated != null || (bool)Get(landformType, "AnyGenerating"))
                throw new InvalidOperationException("GL replay requested while another generation owns its graph context.");
            var session = new GenerationGeological(context);
            if (session.graphs.Count == 0) { session.Dispose(); return null; }
            return session;
        }

        private GenerationGeological(GenerationContext context)
        {
            this.context = context;
            if (context.Map != null) timesByMap.AddOrUpdate(context.Map, timing);
            if (context.SeedSource=="world/tile replay") context.Unsupported.Add("GL reload: generated/custom GenStep list not captured");
            Type worldInfo = AccessTools.TypeByName("GeologicalLandforms.WorldTileInfo");
            tile = Call(worldInfo, "Get", context.Map, false);
            Seed = ReadMapSeed(Find.World.components, context.Map.Tile, Gen.HashCombineInt(Find.World.info.Seed, context.Map.Tile.GetHashCode()));
            if (context.SeedSource == "world/tile replay") { context.Seed = Seed; context.SeedSource = "GL/MapPreview replay"; }
            graphs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(landformType));
            string loading = null;
            var clock = Stopwatch.StartNew();
            try
            {
                if (Get(tile, "Landforms") is IEnumerable forms)
                    foreach (object form in forms)
                    {
                        loading = (string)Get(form, "Id");
                        object req = Get(form, "WorldTileReq");
                        if (req != null && !(bool)Call(req, "CheckWorldObject", Get(tile, "WorldObject"))) continue;
                        string path = LandformFile(form, out bool custom);
                        loading += custom ? "(custom)" : "(original)";
                        object copy = ImportDetached(form, path, custom, out HashSet<UnityEngine.Object> imported);
                        owned.UnionWith(imported);
                        graphs.Add(copy);
                        context.Geological.Add(loading);
                        Set(copy, "RandSeed", Seed);
                        if (Get(copy,"OutputTerrainPatches")!=null) context.Unsupported.Add("GL OutputTerrainPatches");
                        if (Get(copy,"CustomGenSteps") is IEnumerable custom2)
                            foreach (object node in custom2) context.Unsupported.Add("GL custom GenStep: "+Get(node,"GenStepDef"));
                        if (((IEnumerable)Get(copy,"nodes")).Cast<object>().Any(n => n.GetType().Name=="NodePathTrace")) FiniteRiver = true;
                    }
                if (graphs.Count>0)
                    writeOrder = WriteOrder(graphs.Cast<object>(), g => "GL_"+Get(g,"Id"), context.Map.TileInfo.Mutators.Select(m => m.defName).ToList());
                if (graphs.Count>1) context.Unsupported.Add("GL multiple layers: intermediate stage materialisation");
                if (FiniteRiver) context.Unsupported.Add("GL finite node: pathTrace (rivers continue from the map edge)");
                if (graphs.Count>0 && Get(tile,"BiomeVariants") is IEnumerable variants && variants.Cast<object>().Any())
                    context.Unsupported.Add("GL biome variants");
            }
            catch (Exception ex)
            {
                Dispose();
                throw new InvalidOperationException("load "+loading+": "+ex.GetBaseException().Message, ex);
            }
            finally
            {
                timing.Load = clock.Elapsed.TotalMilliseconds;
                HorizonPerf.Record("GL graph load", timing.Load);
            }
        }

        internal static List<object> WriteOrder(IEnumerable<object> layers, Func<object,string> defName, List<string> mutators) =>
            layers.OrderBy(layer => mutators.IndexOf(defName(layer))).ToList();

        internal static int ReadMapSeed(IEnumerable components, RimWorld.Planet.PlanetTile tile, int original)
        {
            if (components == null) return original;
            foreach (object component in components)
            {
                if (component?.GetType().FullName != "MapPreview.SeedRerollData") continue;
                if (tile.tileId < 0 || tile.Layer.LayerID != 0) return original;
                object[] query = { tile.tileId, 0 };
                return (bool)AccessTools.Method(component.GetType(), "TryGet").Invoke(component, query) ? (int)query[1] : original;
            }
            return original;
        }

        private static string LandformFile(object source, out bool custom)
        {
            custom = (bool)Get(source, "IsEdited") || (bool)Get(source, "IsCustom");
            string path = custom
                ? Path.Combine((string)Call(AccessTools.TypeByName("GeologicalLandforms.GraphEditor.LandformManager"), "CustomLandformsDir", 1),
                    "Landform" + Get(source, "Id") + ".xml")
                : (string)Get(source, "OriginalFileLocation");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("no saved landform file to replay from");
            return path;
        }

        internal static object ImportDetached(object source, string path, bool compareSaved, out HashSet<UnityEngine.Object> imported)
        {
            object copy = Import(path) ?? throw new InvalidDataException("the landform file holds no graph");
            HashSet<UnityEngine.Object> originals = GraphObjects(source);
            imported = GraphObjects(copy);
            if (imported.Overlaps(originals)) throw new InvalidOperationException("GL import retained a source graph object; replay refused.");
            try
            {
                RefuseSharedCollections(originals, imported);
                if (!Equals(Get(source,"Id"), Get(copy,"Id"))) throw new InvalidDataException("the file holds another landform");
                if (compareSaved && SavedState(source) != SavedState(copy))
                    throw new InvalidOperationException("the edited landform differs from its saved file");
                return copy;
            }
            catch
            {
                foreach (UnityEngine.Object obj in imported) if (obj != null) UnityEngine.Object.Destroy(obj);
                throw;
            }
        }

        private static object Import(string path)
        {
            Type editor = AccessTools.TypeByName("NodeEditorFramework.NodeEditor");
            Type manager = AccessTools.TypeByName("NodeEditorFramework.IO.ImportExportManager");
            object canvas = Get(editor, "curNodeCanvas"), state = Get(editor, "curEditorState");
            object[] stacks = { Get(editor, "editCanvasStack"), Get(editor, "editEditorStateStack") };
            int[] depths = stacks.Select(s => (int)Get(s, "Count")).ToArray();
            try
            {
                object format = Call(manager, "ParseFormat", "XML");
                return Call(manager, "ImportCanvas", format, new object[] { path });
            }
            finally
            {
                for (int i = 0; i < stacks.Length; i++)
                    while ((int)Get(stacks[i], "Count") > depths[i]) Call(stacks[i], "Pop");
                Set(editor, "curNodeCanvas", canvas); Set(editor, "curEditorState", state);
            }
        }

        private static void RefuseSharedCollections(HashSet<UnityEngine.Object> originals, HashSet<UnityEngine.Object> imported)
        {
            var shared = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach ((UnityEngine.Object _, object value) in InstanceValues(originals))
                if (Mutable(value)) shared.Add(value);
            foreach ((UnityEngine.Object owner, object value) in InstanceValues(imported))
                if (Mutable(value) && shared.Contains(value))
                    throw new InvalidOperationException("GL import retained a mutable source collection ("+owner.GetType().Name+"); replay refused.");
        }

        private static bool Mutable(object value) => value is IList list && !list.IsReadOnly && !(list.IsFixedSize && list.Count==0)
            || value is IDictionary dictionary && !dictionary.IsReadOnly;

        private static IEnumerable<(UnityEngine.Object, object)> InstanceValues(HashSet<UnityEngine.Object> objects)
        {
            foreach (UnityEngine.Object obj in objects)
                for (Type type=obj.GetType();type!=null && type!=typeof(UnityEngine.Object);type=type.BaseType)
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                        yield return (obj, field.GetValue(obj));
        }

        private static string SavedState(object canvas)
        {
            Type nodeBase = AccessTools.TypeByName("NodeEditorFramework.Node");
            Type utility = AccessTools.TypeByName("NodeEditorFramework.Utilities.ReflectionUtility");
            PropertyInfo connections = AccessTools.DeclaredProperty(AccessTools.TypeByName("NodeEditorFramework.ConnectionPort"),"connections");
            var nodes = ((IEnumerable)Get(canvas,"nodes")).Cast<object>().ToList();
            var lines = new List<string>();
            for (int i = 0; i < nodes.Count; i++)
            {
                object node = nodes[i];
                string name = node.GetType().Name;
                lines.Add(i+" "+name);
                if (name != "NodeUILandformManifest")
                    foreach (FieldInfo field in (FieldInfo[])Call(utility,"getSerializedFields",node.GetType(),nodeBase))
                    {
                        if (!field.FieldType.IsValueType && field.FieldType != typeof(string)) continue;
                        object value = field.GetValue(node);
                        string text = value is float f ? f.ToString("G6",CultureInfo.InvariantCulture)
                            : value is double d ? d.ToString("G9",CultureInfo.InvariantCulture) : value?.ToString() ?? "";
                        lines.Add(i+"."+field.Name+"="+text);
                    }
                var ports = ((IEnumerable)Get(node,"connectionPorts")).Cast<object>().ToList();
                for (int p = 0; p < ports.Count; p++)
                    foreach (string link in ((IEnumerable)connections.GetValue(ports[p])).Cast<object>()
                        .Select(other => i+":"+p+">"+nodes.IndexOf(Get(other,"body"))+":"+((UnityEngine.Object)other).name)
                        .OrderBy(s => s, StringComparer.Ordinal))
                        lines.Add(link);
            }
            return string.Join("\n", lines);
        }

        private static HashSet<UnityEngine.Object> GraphObjects(object canvas)
        {
            var result=new HashSet<UnityEngine.Object> { (UnityEngine.Object)canvas };
            void Add(object values)
            {
                if (values is IEnumerable all) foreach (object value in all) if (value is UnityEngine.Object u) result.Add(u);
            }
            if (Get(canvas,"nodes") is IEnumerable nodes) foreach (object node in nodes)
            {
                result.Add((UnityEngine.Object)node);
                Add(Call(node,"GetScriptableObjects")); Add(Get(node,"staticConnectionPorts")); Add(Get(node,"dynamicConnectionPorts"));
            }
            Add(Call(canvas,"GetScriptableObjects")); Add(Get(canvas,"editorStates"));
            result.Remove(null);
            return result;
        }

        internal void Enter(bool traverse = true)
        {
            if (active != null || entered) throw new InvalidOperationException("Nested GL replay context.");
            long started = Stopwatch.GetTimestamp();
            double traversed = 0;
            entered = true; active = this; scopeStarted = started;
            try
            {
                if (traverse) Stage = "traverse";
                foreach (string name in new[] { "GeneratingTile", "GeneratingMapSize", "GeneratingGridFullSize", "GeneratingLandforms" }) saved[name] = Get(landformType, name);
                IDictionary named = (IDictionary)Get(landformType, "NamedFeatureCache");
                foreach (DictionaryEntry entry in named) savedNamed.Add(entry);
                namedCaptured=true;
                named.Clear();
                Set(landformType, "GeneratingTile", tile);
                var size = new IntVec2(context.Width, context.Height);
                Set(landformType, "GeneratingMapSize", size);
                object point = Get(AccessTools.TypeByName("GeologicalLandforms.GeologicalLandformsAPI"), "LandformGridSize");
                Set(landformType, "GeneratingGridFullSize", Call(point, "Apply", size));
                Set(landformType, "GeneratingLandforms", graphs);
                var inputs = new Dictionary<string, Delegate>
                {
                    ["NodeInputElevation"] = new Func<double,double,double>((x,z) => ElevationInput(x,z)),
                    ["NodeInputFertility"] = new Func<double,double,double>((x,z) => FertilityInput(x,z)),
                    ["NodeInputCaves"] = new Func<double,double,double>((x,z) => CavesInput(x,z)),
                    ["NodeInputTerrain"] = new Func<double,double,TerrainDef>((x,z) => TerrainInput(x,z)),
                    ["NodeInputBiomeGrid"] = new Func<double,double,RimWorld.BiomeDef>((x,z) => BiomeInput(x,z))
                };
                IList previous = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(landformType));
                if (traverse) Set(landformType,"GeneratingLandforms",previous);
                if (traverse) foreach (object graph in graphs)
                {
                    graphInputs[graph] = new Dictionary<string, Delegate>(inputs);
                    Call(graph, "ClearNamedInputs"); Call(graph, "SetNamedInputs", previous);
                    var clock = Stopwatch.StartNew();
                    Call(graph, "TraverseAll");
                    double ms = clock.Elapsed.TotalMilliseconds;
                    traversed += ms;
                    HorizonPerf.Record("GL traverse", ms);
                    foreach (var output in new[] { ("OutputElevation","Get","NodeInputElevation"), ("OutputFertility","Get","NodeInputFertility"),
                        ("OutputCaves","Get","NodeInputCaves"), ("OutputTerrain","GetBase","NodeInputTerrain"), ("OutputBiomeGrid","GetBiomeGrid","NodeInputBiomeGrid") })
                    {
                        object function = GraphOutput(graph, output.Item1, output.Item2);
                        if (function != null) inputs[output.Item3] = Typed(function);
                    }
                    previous.Add(graph);
                }
                if (traverse) Set(landformType,"GeneratingLandforms",graphs);
            }
            catch { Exit(); throw; }
            finally
            {
                if (timing != null) { timing.Traverse += traversed; timing.Enter += MsSince(started) - traversed; }
            }
        }

        internal void Exit()
        {
            if (!entered) return;
            try
            {
                foreach (var pair in saved) Set(landformType, pair.Key, pair.Value);
                if (namedCaptured)
                {
                    IDictionary named = (IDictionary)Get(landformType, "NamedFeatureCache");
                    named.Clear(); foreach (DictionaryEntry entry in savedNamed) named.Add(entry.Key, entry.Value);
                }
            }
            finally
            {
                entered = false; active = null; saved.Clear(); savedNamed.Clear();
                namedCaptured=false;
                timing?.Add(Stage, MsSince(scopeStarted));
            }
        }

        private static bool InputPrefix(object __instance, ref bool __result)
        {
            GenerationGeological s = active;
            if (s == null || !s.owned.Contains((UnityEngine.Object)__instance)) return true;
            object knob = Get(__instance, "Knob");
            string kind = __instance.GetType().Name;
            object graph = Get(__instance, "canvas");
            if (!s.inputSuppliers.TryGetValue((graph, kind), out object supplier))
            {
                Type valueType = kind == "NodeInputTerrain" ? typeof(TerrainDef) : kind == "NodeInputBiomeGrid" ? typeof(RimWorld.BiomeDef) : typeof(double);
                supplier = s.InputSupplier(valueType, s.graphInputs[graph][kind]);
                s.inputSuppliers.Add((graph, kind), supplier);
            }
            MethodInfo setter = AccessTools.GetDeclaredMethods(knob.GetType()).FirstOrDefault(m => m.Name == "SetValue" && m.IsGenericMethodDefinition);
            setter ??= AccessTools.Method(knob.GetType(), "SetValue");
            setter.MakeGenericMethod(supplier.GetType().GetInterfaces().First(t => t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("ISupplier"))).Invoke(knob, new[] { supplier });
            __result = true; return false;
        }

        private object InputSupplier(Type valueType, Delegate value)
        {
            Type gridType = AccessTools.TypeByName("TerrainGraph.IGridFunction`1").MakeGenericType(valueType);
            double scale = (double)Math.Min(context.Width, context.Height) / (int)Get(landformType, "GeneratingGridFullSize");
            Delegate scaled = (Delegate)AccessTools.Method(typeof(GenerationGeological), nameof(Scaled)).MakeGenericMethod(valueType)
                .Invoke(null, new object[] { value, scale });
            object grid = scaled == null ? null : GridFactory.Create(gridType, valueType, scaled);
            if (grid == null)
            {
                if (timing != null) timing.ProxyGrids++;
                return Supplier(valueType, Boxed(value));
            }
            if (timing != null) timing.FastGrids++;
            return GridSupplier(gridType, grid);
        }

        private static Func<double,double,T> Scaled<T>(Delegate value, double scale)
        {
            if (value is not Func<double,double,T> f) return null;
            return (x,z) => f(x*scale, z*scale);
        }

        private object Supplier(Type valueType, Func<double,double,object> value)
        {
            Type gridType = AccessTools.TypeByName("TerrainGraph.IGridFunction`1").MakeGenericType(valueType);
            double scale = (double)Math.Min(context.Width, context.Height) / (int)Get(landformType, "GeneratingGridFullSize");
            object grid = DispatchProxy.Create(gridType, typeof(GenerationGridProxy));
            ((GenerationGridProxy)grid).Function = (m, a) => m.Name == "ValueAt" ? value(Convert.ToDouble(a[0])*scale, Convert.ToDouble(a[1])*scale) : null;
            return GridSupplier(gridType, grid);
        }

        private static object GridSupplier(Type gridType, object grid)
        {
            Type supplierType = AccessTools.TypeByName("TerrainGraph.ISupplier`1").MakeGenericType(gridType);
            object supplier = DispatchProxy.Create(supplierType, typeof(GenerationGridProxy));
            ((GenerationGridProxy)supplier).Function = (m, a) => m.Name == "Get" ? grid : null;
            return supplier;
        }

        internal static class GridFactory
        {
            private static ModuleBuilder module;
            private static int made;
            private static readonly Dictionary<Type, ConstructorInfo> constructors = new();
            private static readonly HashSet<Type> failed = new();

            internal static object Create(Type gridType, Type valueType, Delegate function)
            {
                if (failed.Contains(gridType)) return null;
                try
                {
                    if (!constructors.TryGetValue(gridType, out ConstructorInfo constructor))
                    {
                        constructor = Emit(gridType, valueType);
                        constructors.Add(gridType, constructor);
                    }
                    return constructor.Invoke(new object[] { function });
                }
                catch (Exception ex)
                {
                    failed.Add(gridType);
                    Log.ErrorOnce(HorizonLog.Prefix + "Typed GL input grid for " + valueType + " could not be made; GL inputs use the slower reflection path (same values). " + ex, 0x45484749);
                    return null;
                }
            }

            private static ConstructorInfo Emit(Type gridType, Type valueType)
            {
                MethodInfo api = gridType.GetMethod("ValueAt");
                ParameterInfo[] parameters = api?.GetParameters();
                if (api == null || api.ReturnType != valueType || parameters.Length != 2
                    || parameters[0].ParameterType != typeof(double) || parameters[1].ParameterType != typeof(double))
                    throw new MissingMethodException(gridType.FullName, "T ValueAt(double, double)");
                module ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EndlessHorizon.GeneratedGrids"), AssemblyBuilderAccess.RunAndCollect)
                    .DefineDynamicModule("EndlessHorizon.GeneratedGrids");
                Type function = typeof(Func<,,>).MakeGenericType(typeof(double), typeof(double), valueType);
                TypeBuilder type = module.DefineType("EndlessHorizon.GeneratedGrids.Grid" + made++,
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object), new[] { gridType });
                FieldBuilder field = type.DefineField("function", function, FieldAttributes.Private | FieldAttributes.InitOnly);

                ConstructorBuilder constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { function });
                ILGenerator il = constructor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, field);
                il.Emit(OpCodes.Ret);

                MethodBuilder valueAt = type.DefineMethod("ValueAt",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    valueType, new[] { typeof(double), typeof(double) });
                il = valueAt.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, function.GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(valueAt, api);

                return type.CreateType().GetConstructor(new[] { function })
                    ?? throw new MissingMethodException("generated grid constructor");
            }
        }

        internal Func<double,double,T> Output<T>(string slot, string method)
        {
            Stage = slot + "." + method;
            var clock = Stopwatch.StartNew();
            object output = null;
            IEnumerable order = slot == "OutputRoofGrid" ? graphs : writeOrder;
            foreach (object graph in order)
            {
                object f = GraphOutput(graph, slot, method);
                if (f != null) output = f;
            }
            HorizonPerf.Record("GL output " + Stage, clock.Elapsed.TotalMilliseconds);
            return Bind<T>(output);
        }

        internal static Func<double,double,T> Bind<T>(object function)
        {
            if (function == null) return null;
            Type api = function.GetType().GetInterfaces().First(t => t.IsGenericType
                && t.GetGenericTypeDefinition().Name.StartsWith("IGridFunction") && t.GetGenericArguments()[0] == typeof(T));
            return (Func<double,double,T>)Delegate.CreateDelegate(typeof(Func<double,double,T>), function, api.GetMethod("ValueAt"));
        }

        private static Delegate Typed(object function)
        {
            Type valueType = function.GetType().GetInterfaces().First(t => t.IsGenericType
                && t.GetGenericTypeDefinition().Name.StartsWith("IGridFunction")).GetGenericArguments()[0];
            return (Delegate)AccessTools.Method(typeof(GenerationGeological), nameof(Bind))
                .MakeGenericMethod(valueType).Invoke(null, new[] { function });
        }

        private static Func<double,double,object> Box<T>(Func<double,double,T> value) => (x,z) => value(x,z);

        private static Func<double,double,object> Boxed(Delegate typed)
        {
            Type valueType = typed.GetType().GetMethod("Invoke").ReturnType;
            return (Func<double,double,object>)AccessTools.Method(typeof(GenerationGeological), nameof(Box))
                .MakeGenericMethod(valueType).Invoke(null, new object[] { typed });
        }

        private object GraphOutput(object graph, string slot, string method)
        {
            object node = Get(graph, slot);
            if (node == null) return null;
            object output = Call(node, method);
            if (output == null) return null;
            MethodInfo transform = AccessTools.GetDeclaredMethods(landformType).Single(m => m.Name == "TransformIntoMapSpace" && m.IsGenericMethodDefinition);
            Type valueType = output.GetType().GetInterfaces().First(t => t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IGridFunction")).GetGenericArguments()[0];
            return transform.MakeGenericMethod(valueType).Invoke(null, new[] { output });
        }

        internal float? Mineables()
        {
            Stage = "OutputScatterers.GetMineables";
            object value = null;
            foreach (object graph in graphs)
                if (Get(graph,"OutputScatterers") is object node && Call(node,"GetMineables") is object result) value = result;
            return value == null ? (float?)null : Convert.ToSingle(value);
        }

        internal Func<double,double,RimWorld.BiomeDef> BiomeOutput()
        {
            Stage = "OutputBiomeGrid";
            var clock = Stopwatch.StartNew();
            object properties = Call(AccessTools.TypeByName("GeologicalLandforms.ExtensionUtils"), "Properties", context.Map.Biome);
            if (!(bool)Get(properties, "AllowBiomeTransitions")) return null;
            bool bordering = Get(tile, "BorderingBiomes") is IEnumerable all && all.Cast<object>().Any();
            object value = null;
            foreach (object graph in writeOrder)
            {
                object node = Get(graph, "OutputBiomeGrid");
                if (node == null) continue;
                object own = GraphOutput(graph, "OutputBiomeGrid", "GetBiomeGrid");
                if (bordering)
                {
                    object next = Call(node, "ApplyBiomeTransitions", tile, new IntVec2(context.Width, context.Height), own);
                    if (next != null) { own = next; TransitionsApplied = true; }
                }
                if (own != null) value = own;
            }
            HorizonPerf.Record("GL output " + Stage, clock.Elapsed.TotalMilliseconds);
            return Bind<RimWorld.BiomeDef>(value);
        }

        internal TerrainDef Priority(TerrainDef a, TerrainDef b)
        {
            if (a == null) return b;
            if (b == null) return a;
            if (priority == null)
            {
                Type type = AccessTools.TypeByName("GeologicalLandforms.TerrainPriority");
                priorityOptions = Get(type, "DefaultOptions");
                priority = AccessTools.Method(type, "Apply");
                priorityCall = PriorityCall(priority);
                if (timing != null) timing.Priority = priorityCall != null ? "compiled" : "reflection";
            }
            return priorityCall != null ? priorityCall(a, b, priorityOptions) : (TerrainDef)priority.Invoke(null, new[] { a, b, priorityOptions });
        }

        private static readonly Dictionary<MethodInfo, Func<TerrainDef, TerrainDef, object, TerrainDef>> priorityCalls = new();

        internal static Func<TerrainDef, TerrainDef, object, TerrainDef> PriorityCall(MethodInfo apply)
        {
            if (apply == null) return null;
            if (priorityCalls.TryGetValue(apply, out var call)) return call;
            try
            {
                ParameterInfo[] p = apply.GetParameters();
                if (!apply.IsStatic || p.Length != 3) throw new MissingMethodException(apply.DeclaringType?.FullName, "static Apply(TerrainDef, TerrainDef, options)");
                ParameterExpression a = Expression.Parameter(typeof(TerrainDef), "a"), b = Expression.Parameter(typeof(TerrainDef), "b");
                ParameterExpression options = Expression.Parameter(typeof(object), "options");
                Expression body = Expression.Call(apply, Expression.Convert(a, p[0].ParameterType), Expression.Convert(b, p[1].ParameterType),
                    Expression.Convert(options, p[2].ParameterType));
                if (body.Type != typeof(TerrainDef)) body = Expression.Convert(body, typeof(TerrainDef));
                call = Expression.Lambda<Func<TerrainDef, TerrainDef, object, TerrainDef>>(body, a, b, options).Compile();
            }
            catch (Exception ex)
            {
                call = null;
                Log.ErrorOnce(HorizonLog.Prefix + "Compiled GL terrain priority could not be made; it is called through reflection (same values). " + ex, 0x45484750);
            }
            priorityCalls[apply] = call;
            return call;
        }

        internal bool PostTransitions(out float root, out float pass)
        {
            Type transition=AccessTools.TypeByName("GeologicalLandforms.BiomeTransition");
            root=(float)Get(transition,"ThresholdForRoot"); pass=(float)Get(transition,"ThresholdForPassCheck");
            object component=context.Map.components.FirstOrDefault(c => c.GetType().FullName=="GeologicalLandforms.BiomeGrid");
            return component!=null && (bool)Call(Get(transition,"PostProcessBiomeTransitions"),"Apply",component);
        }

        internal static T Value<T>(Func<double,double,T> function, int x, int z, T fallback) => function == null ? fallback : function(x, z);

        public void Dispose()
        {
            Exit();
            foreach (UnityEngine.Object obj in owned) if (obj != null) UnityEngine.Object.Destroy(obj);
            owned.Clear();
        }
    }

    internal class GenerationGridProxy : DispatchProxy
    {
        public GenerationGridProxy() { }
        internal Func<MethodInfo, object[], object> Function;
        protected override object Invoke(MethodInfo targetMethod, object[] args) => Function(targetMethod, args);
    }
}
