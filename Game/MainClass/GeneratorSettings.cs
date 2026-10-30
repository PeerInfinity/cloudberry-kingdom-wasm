using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using Microsoft.Xna.Framework;

using CoreEngine.Random;
using CloudberryKingdom.Bobs;
using CloudberryKingdom.Levels;

namespace CloudberryKingdom
{
    /// <summary>
    /// The generator's settings, found by reflection rather than listed by hand: every public field (and read/write
    /// property) of a plain type on LevelSeedData, PieceSeedData, the geometry's StyleData and each generator's
    /// AutoGen_Parameters, plus the Upgrade levels and the Hero Factory's physics. GeneratorApi applies a JSON object of
    /// overrides to any of them and window.cloudberry.schema() lists them.
    /// </summary>
    public static class GeneratorSettings
    {
        /// <summary>A settable member: a field or a read/write property.</summary>
        public class Member
        {
            public string Name;
            public Type Type;
            public Type DeclaringType;
            public FieldInfo Field;
            public PropertyInfo Property;

            public object Get(object target) { return Field != null ? Field.GetValue(target) : Property.GetValue(target); }
            public void Set(object target, object value)
            {
                if (Field != null) Field.SetValue(target, value);
                else Property.SetValue(target, value);
            }
        }

        static bool IsPlain(Type t)
        {
            return t == typeof(bool) || t == typeof(int) || t == typeof(float) || t == typeof(Vector2) || t.IsEnum
                || t == typeof(Param) || t == typeof(VectorParam);
        }

        static readonly Dictionary<Type, List<Member>> Cache = new Dictionary<Type, List<Member>>();

        /// <summary>
        /// Every public instance field and read/write property of a plain type that the class hierarchy declares
        /// (most-derived class last), in a stable order: base class first, then by name.
        /// </summary>
        public static List<Member> Members(Type type)
        {
            List<Member> found;
            if (Cache.TryGetValue(type, out found)) return found;

            found = new List<Member>();
            var hierarchy = new List<Type>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType) hierarchy.Add(t);
            hierarchy.Reverse();
            var seen = new HashSet<string>();
            foreach (Type t in hierarchy)
            {
                var list = new List<Member>();
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (!f.IsInitOnly && IsPlain(f.FieldType))
                        list.Add(new Member { Name = f.Name, Type = f.FieldType, DeclaringType = t, Field = f });
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.SetMethod.IsPublic && IsPlain(p.PropertyType))
                        list.Add(new Member { Name = p.Name, Type = p.PropertyType, DeclaringType = t, Property = p });
                list.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
                foreach (Member m in list)
                    if (seen.Add(m.Name)) found.Add(m);
            }
            Cache[type] = found;
            return found;
        }

        public static Member Find(Type type, string name)
        {
            foreach (Member m in Members(type))
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        /// <summary>
        /// LevelSeedData members the API sets from its own arguments (Initialize overwrites them): refused as overrides.
        /// </summary>
        public static readonly string[] LevelSeedControlled = { "NumPieces", "Length", "PieceLength", "Difficulty", "MyGeometry", "Seed" };

        /// <summary>Applies {name: value, …} to an object's settable members; an unknown name or a bad value throws.</summary>
        public static void Apply(object target, JsonElement overrides, string where, string[] refused = null)
        {
            if (overrides.ValueKind != JsonValueKind.Object) throw new ArgumentException(where + ": expected an object");
            foreach (JsonProperty p in overrides.EnumerateObject())
            {
                if (refused != null && refused.Any(r => string.Equals(r, p.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(where + "." + p.Name + " is set by the API's own arguments");
                Member m = Find(target.GetType(), p.Name);
                if (m == null) throw new ArgumentException(where + ": no setting '" + p.Name + "' on " + target.GetType().Name);
                m.Set(target, Convert(m.Type, p.Value, where + "." + m.Name));
            }
        }

        public static object Convert(Type t, JsonElement v, string where)
        {
            try
            {
                if (t == typeof(bool)) return v.GetBoolean();
                if (t == typeof(int)) return v.GetInt32();
                if (t == typeof(float)) return v.GetSingle();
                if (t.IsEnum)
                {
                    if (v.ValueKind == JsonValueKind.Number) return Enum.ToObject(t, v.GetInt32());
                    return Enum.Parse(t, v.GetString(), true);
                }
                if (t == typeof(Vector2)) return new Vector2(v[0].GetSingle(), v[1].GetSingle());
                // A Param overridden is a constant: the same value along the whole piece.
                if (t == typeof(Param)) return new Param(v.GetSingle());
                if (t == typeof(VectorParam))
                {
                    var vp = new VectorParam();
                    vp.X = new Param(v[0].GetSingle());
                    vp.Y = new Param(v[1].GetSingle());
                    return vp;
                }
            }
            catch (Exception e) when (!(e is ArgumentException))
            {
                throw new ArgumentException(where + ": cannot read " + v.GetRawText() + " as " + t.Name);
            }
            throw new ArgumentException(where + ": unsupported type " + t.Name);
        }

        public static string TypeName(Type t)
        {
            if (t == typeof(bool)) return "bool";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(Vector2)) return "vec2";
            if (t == typeof(Param)) return "param";
            if (t == typeof(VectorParam)) return "vecparam";
            if (t.IsEnum) return "enum";
            return t.Name;
        }

        /// <summary>Writes a member's current value (a Param as the value at the piece's start).</summary>
        public static void WriteValue(Utf8JsonWriter w, Type t, object v)
        {
            if (v == null) { w.WriteNullValue(); return; }
            if (v is bool b) w.WriteBooleanValue(b);
            else if (v is int i) w.WriteNumberValue(i);
            else if (v is float f) { if (float.IsFinite(f)) w.WriteNumberValue(f); else w.WriteNullValue(); }
            else if (v is Vector2 vec) { w.WriteStartArray(); WriteFloat(w, vec.X); WriteFloat(w, vec.Y); w.WriteEndArray(); }
            else if (v is Param p) WriteFloat(w, p.Val);
            else if (v is VectorParam vp) { w.WriteStartArray(); WriteFloat(w, vp.X.Val); WriteFloat(w, vp.Y.Val); w.WriteEndArray(); }
            else if (t.IsEnum) w.WriteStringValue(v.ToString());
            else w.WriteNullValue();
        }

        static void WriteFloat(Utf8JsonWriter w, float f)
        {
            if (float.IsFinite(f)) w.WriteNumberValue(f); else w.WriteNullValue();
        }

        /// <summary>Writes {name: value} for every settable member of the object.</summary>
        public static void WriteValues(Utf8JsonWriter w, string name, object target)
        {
            w.WriteStartObject(name);
            foreach (Member m in Members(target.GetType()))
            {
                w.WritePropertyName(m.Name);
                object v;
                try { v = m.Get(target); }
                catch (TargetInvocationException) { w.WriteNullValue(); continue; }
                WriteValue(w, m.Type, v);
            }
            w.WriteEndObject();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Generators and their parameter classes

        public const string AutoGenSuffix = "_AutoGen";
        public const string ParamsSuffix = "_Parameters";

        /// <summary>The generator's name: its class name without "_AutoGen".</summary>
        public static string GenName(AutoGen gen)
        {
            string n = gen.GetType().Name;
            return n.EndsWith(AutoGenSuffix, StringComparison.Ordinal) ? n.Substring(0, n.Length - AutoGenSuffix.Length) : n;
        }

        public static AutoGen FindGen(string name)
        {
            foreach (AutoGen gen in Generators.Gens)
                if (string.Equals(GenName(gen), name, StringComparison.OrdinalIgnoreCase)) return gen;
            return null;
        }

        /// <summary>The parameter class a generator returns, by the engine's naming (X_AutoGen → X_Parameters), or null.</summary>
        public static Type ParamsType(AutoGen gen)
        {
            Type t = gen.GetType().Assembly.GetType(gen.GetType().Namespace + "." + GenName(gen) + ParamsSuffix, false);
            return t != null && typeof(AutoGen_Parameters).IsAssignableFrom(t) ? t : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Heroes

        public const string HeroClassPrefix = "BobPhsx";

        /// <summary>Every hero class with a static Instance (the hero argument is the class name without "BobPhsx").</summary>
        public static List<string> HeroNames()
        {
            var names = new List<string>();
            foreach (Type t in typeof(BobPhsx).Assembly.GetTypes())
            {
                if (!typeof(BobPhsx).IsAssignableFrom(t) || !t.Name.StartsWith(HeroClassPrefix, StringComparison.Ordinal)) continue;
                if (t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) == null) continue;
                names.Add(t.Name.Substring(HeroClassPrefix.Length));
            }
            names.Sort(string.CompareOrdinal);
            return names;
        }

        // ---------------------------------------------------------------------------------------------------------
        // The schema

        /// <summary>
        /// The JSON schema of generate()'s arguments: the API's own arguments, then each settings group found by
        /// reflection with its members' types, enum values and defaults. StyleData members that the style's random
        /// draw sets are marked "drawn" (measured: two StyleData built from different seeds disagree on them).
        /// </summary>
        public static void WriteSchema(Utf8JsonWriter w, Action<Utf8JsonWriter> writeApiArgs)
        {
            Rand holdRnd = Tools.GlobalRnd;
            try
            {
                w.WriteStartObject();
                w.WriteString("format", "ck-level/2 schema");

                w.WriteStartObject("args");
                writeApiArgs(w);
                w.WriteEndObject();

                w.WriteStartArray("heroes");
                foreach (string h in HeroNames()) w.WriteStringValue(h);
                w.WriteEndArray();

                WriteEnum(w, "geometry", typeof(LevelGeometry));

                w.WriteStartArray("tilesets");
                var tilesets = new List<string>(TileSets.NameLookup.Keys);
                tilesets.Sort(string.CompareOrdinal);
                foreach (string t in tilesets) w.WriteStringValue(t);
                w.WriteEndArray();

                // Upgrades: the per-piece levels that difficulty otherwise draws. 0 = absent.
                w.WriteStartObject("upgrades");
                w.WriteString("type", "float");
                w.WriteNumber("min", 0);
                w.WriteNumber("max", 10);
                w.WriteNumber("customLevelMenuMax", 9);
                w.WriteStartArray("names");
                foreach (Upgrade u in Enum.GetValues(typeof(Upgrade))) w.WriteStringValue(u.ToString());
                w.WriteEndArray();
                w.WriteStartArray("platformItems");
                for (int i = 0; i < PlatformSuppression.Items.Length; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("item", PlatformSuppression.Items[i].ToString());
                    w.WriteStartArray("kinds");
                    foreach (ObjectType k in PlatformSuppression.KindsOf(i)) w.WriteStringValue(k.ToString());
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                Tools.GlobalRnd = new Rand(0);
                var seed = new LevelSeedData();
                WriteGroup(w, "levelSeed", typeof(LevelSeedData), seed, null, LevelSeedControlled);

                var piece = new PieceSeedData(seed);
                WriteGroup(w, "piece", typeof(PieceSeedData), piece, null, null);

                // One style class per geometry (PieceSeedData.Init picks it).
                w.WriteStartObject("style");
                foreach (LevelGeometry g in Enum.GetValues(typeof(LevelGeometry)))
                {
                    var a = new PieceSeedData(0, g, NewSeed(1)).Style;
                    var b = new PieceSeedData(0, g, NewSeed(2)).Style;
                    var c = new PieceSeedData(0, g, NewSeed(3)).Style;
                    WriteGroup(w, g.ToString(), a.GetType(), a, m => !Same(m.Get(a), m.Get(b)) || !Same(m.Get(a), m.Get(c)), null);
                }
                w.WriteEndObject();

                // Each generator's parameters (values are set from the upgrades at make time: no static default).
                w.WriteStartObject("params");
                foreach (AutoGen gen in Generators.Gens)
                {
                    Type pt = ParamsType(gen);
                    w.WriteStartObject(GenName(gen));
                    w.WriteString("class", pt == null ? null : pt.Name);
                    if (pt != null)
                    {
                        w.WriteStartObject("members");
                        foreach (Member m in Members(pt)) WriteMember(w, m, null, false);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                // Hero Factory
                w.WriteStartObject("heroCustom");
                WriteEnum(w, "base", typeof(Hero_BaseType));
                WriteEnum(w, "shape", typeof(Hero_Shape));
                WriteEnum(w, "move", typeof(Hero_MoveMod));
                WriteEnum(w, "special", typeof(Hero_Special));
                w.WriteStartObject("phsx");
                foreach (BobPhsx.CustomData d in Enum.GetValues(typeof(BobPhsx.CustomData)))
                {
                    var bounds = BobPhsx.CustomPhsxData.Bounds(d);
                    w.WriteStartObject(d.ToString());
                    w.WriteString("type", "float");
                    w.WriteNumber("default", bounds.DefaultValue);
                    w.WriteNumber("min", bounds.MinValue);
                    w.WriteNumber("max", bounds.MaxValue);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();

                // replayHero: BobPhsx members a replay may change (e.g. NumJumps, JetPack): Normal's hierarchy.
                w.WriteStartObject("replayHero");
                foreach (Member m in Members(typeof(BobPhsxNormal))) WriteMember(w, m, BobPhsxNormal.Instance, false);
                w.WriteEndObject();

                w.WriteEndObject();
            }
            finally
            {
                Tools.GlobalRnd = holdRnd;
            }
        }

        static LevelSeedData NewSeed(int seed)
        {
            Tools.GlobalRnd = new Rand(seed);
            return new LevelSeedData();
        }

        static bool Same(object a, object b)
        {
            if (a is Param pa && b is Param pb) return pa.Val == pb.Val;
            if (a is VectorParam va && b is VectorParam vb) return va.X.Val == vb.X.Val && va.Y.Val == vb.Y.Val;
            return Equals(a, b);
        }

        static void WriteEnum(Utf8JsonWriter w, string name, Type t)
        {
            w.WriteStartArray(name);
            foreach (object v in Enum.GetValues(t)) w.WriteStringValue(v.ToString());
            w.WriteEndArray();
        }

        static void WriteGroup(Utf8JsonWriter w, string name, Type t, object instance, Func<Member, bool> drawn, string[] refused)
        {
            w.WriteStartObject(name);
            w.WriteString("class", t.Name);
            w.WriteStartObject("members");
            foreach (Member m in Members(t))
            {
                if (refused != null && refused.Contains(m.Name)) continue;
                WriteMember(w, m, instance, drawn != null && drawn(m));
            }
            w.WriteEndObject();
            if (refused != null)
            {
                w.WriteStartArray("setByApiArgs");
                foreach (string r in refused) w.WriteStringValue(r);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        static void WriteMember(Utf8JsonWriter w, Member m, object instance, bool drawn)
        {
            w.WriteStartObject(m.Name);
            w.WriteString("type", TypeName(m.Type));
            if (m.DeclaringType != null) w.WriteString("declaredBy", m.DeclaringType.Name);
            if (m.Type.IsEnum)
            {
                w.WriteStartArray("values");
                foreach (object v in Enum.GetValues(m.Type)) w.WriteStringValue(v.ToString());
                w.WriteEndArray();
            }
            if (instance != null)
            {
                object v = null;
                bool ok = true;
                try { v = m.Get(instance); } catch (TargetInvocationException) { ok = false; }
                if (ok) { w.WritePropertyName("default"); WriteValue(w, m.Type, v); }
            }
            if (drawn) w.WriteBoolean("drawn", true);
            w.WriteEndObject();
        }
    }
}
