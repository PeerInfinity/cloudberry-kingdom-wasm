using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Xna.Framework;

using CoreEngine.Random;
using CloudberryKingdom.Bobs;
using CloudberryKingdom.Blocks;
using CloudberryKingdom.Levels;
using CloudberryKingdom.InGameObjects;

#if BROWSER
using System.Runtime.InteropServices.JavaScript;
#endif

namespace CloudberryKingdom
{
    /// <summary>
    /// The level generator + computer player as a callable.
    /// "ck-level/1": seed + difficulty + hero (+ length, geometry, tileset) in; level geometry + the computer's recording out.
    /// "ck-level/2" (any v2 argument, or format: "ck-level/2"): adds nested pieces with explicit upgrades, every generator
    /// setting found by reflection (GeneratorSettings), Hero Factory heroes, per-object piece and platform-item tags, coins
    /// with the tick the computer collects them, replays under platform suppression and the nested-piece verifier.
    /// A ck-level/1 call returns exactly the bytes it always did.
    /// Browser: [JSExport] GeneratorApi.GenerateJson / SchemaJson / SetHeldJson, page mode ?api=1.
    /// Native: --generate &lt;json-args&gt; --out &lt;file&gt;; --schema &lt;file&gt;.
    /// </summary>
    public static partial class GeneratorApi
    {
        public const string Format = "ck-level/1";
        public const string Format2 = "ck-level/2";
        public const int PhysicsHz = 60;
        public const string Units = "world (game px)";

        public const int DefaultSeed = 1;
        public const float DefaultDifficulty = 2;
        public const string DefaultHero = "Normal";
        public const int DefaultLength = 6700;
        public const string DefaultGeometry = "Right";
        public const string DefaultTileSet = "cave";

        /// <summary>Extra ticks the replay check may run past the piece's recorded length before it gives up on the goal.</summary>
        public const int ReplayGoalSlackTicks = 120;

        /// <summary>The prefix every hero physics class carries; the hero argument is the rest of the class name.</summary>
        public const string HeroClassPrefix = GeneratorSettings.HeroClassPrefix;
        /// <summary>The suffix the generator's parameter classes carry; stripped from emitted kind names.</summary>
        public const string AutoSuffix = "__Auto";

        /// <summary>True when the game was started to serve the API (no attract mode).</summary>
        public static bool ApiMode;
        /// <summary>True once content is loaded and Generate may be called.</summary>
        public static bool Ready;

        /// <summary>Native one-shot: the JSON args and the output file.</summary>
        public static string NativeArgs, NativeOut, NativeSchemaOut;
        public static int NativeRepeat = 1;

        public static double LastGenerateMs, LastExportMs, LastReplayMs, LastVerifyMs;

        /// <summary>Consumes --api / --generate / --out / --repeat / --schema; returns the arguments the game should still see.</summary>
        public static string[] ProcessArgs(string[] args)
        {
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--api": ApiMode = true; break;
                    case "--generate": ApiMode = true; NativeArgs = args[++i]; if (NativeArgs.StartsWith("@")) NativeArgs = File.ReadAllText(NativeArgs.Substring(1)); break;
                    case "--out": NativeOut = args[++i]; break;
                    case "--repeat": NativeRepeat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--schema": ApiMode = true; NativeSchemaOut = args[++i]; break;
                    default: rest.Add(args[i]); break;
                }
            }
            return rest.ToArray();
        }

        /// <summary>Called by the logo phase once loading is done, instead of starting the ScreenSaver.</summary>
        public static void OnLoaded()
        {
            // The game's own empty level (DebugHelper_MakeTestLevel.cs), so the API idles on a level with no obstacles.
            Tools.TheGame.MakeEmptyLevel();
            Ready = true;
            Console.WriteLine("[generator-api] ready");

            if (NativeSchemaOut != null)
                File.WriteAllText(NativeSchemaOut, Schema());

            if (NativeArgs != null)
            {
                string json = null;
                var times = new StringBuilder();
                // A file of one JSON array runs each element as its own call (one process, many levels).
                var calls = new List<string>();
                using (var doc = JsonDocument.Parse(NativeArgs))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var e in doc.RootElement.EnumerateArray()) calls.Add(e.GetRawText());
                    else calls.Add(NativeArgs);
                }
                var outs = new List<string>();
                foreach (string call in calls)
                    for (int i = 0; i < NativeRepeat; i++)
                    {
                        json = Generate(call);
                        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))).Substring(0, 12);
                        times.AppendFormat(CultureInfo.InvariantCulture, "{0}generate={1:F1}ms export={2:F1}ms replay={3:F1}ms verify={4:F1}ms sha256={5}",
                            times.Length == 0 ? "" : "\n", LastGenerateMs, LastExportMs, LastReplayMs, LastVerifyMs, sha);
                        if (i == 0) outs.Add(json);
                    }
                if (calls.Count > 1) json = "[" + string.Join(",\n", outs) + "]";
                if (NativeOut != null) File.WriteAllText(NativeOut, json);
                else Console.WriteLine(json);
                if (NativeOut != null) File.WriteAllText(NativeOut + ".timing.txt", times.ToString());
                Console.WriteLine("[generator-api] wrote " + (NativeOut ?? "stdout"));
            }
            if (NativeArgs != null || NativeSchemaOut != null) Tools.TheGame.Exit();
        }

#if BROWSER
        [JSExport]
        public static bool IsReady() { return Ready; }

        [JSExport]
        public static string GenerateJson(string argsJson) { return Generate(argsJson); }

        [JSExport]
        public static string SchemaJson() { return Schema(); }

        /// <summary>The runtime platform switch: a JSON array of held items (PlatformSuppression.Items), or "null" for everything solid.</summary>
        [JSExport]
        public static string SetHeldJson(string heldJson) { return SetHeld(heldJson); }

        /// <summary>The last call's phase timings, as JSON (kept out of the document so the document stays byte-comparable).</summary>
        [JSExport]
        public static string LastTimings()
        {
            return string.Format(CultureInfo.InvariantCulture, "{{\"generateMs\":{0:F2},\"exportMs\":{1:F2},\"replayMs\":{2:F2},\"verifyMs\":{3:F2}}}",
                LastGenerateMs, LastExportMs, LastReplayMs, LastVerifyMs);
        }
#endif

        /// <summary>Sets the runtime platform switch; returns {"enabled":…, "held":[…]} or {"error":…}.</summary>
        public static string SetHeld(string heldJson)
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                try
                {
                    if (string.IsNullOrEmpty(heldJson) || heldJson.Trim() == "null") PlatformSuppression.Disable();
                    else
                    {
                        var names = new List<string>();
                        using (var doc = JsonDocument.Parse(heldJson))
                            foreach (var e in doc.RootElement.EnumerateArray()) names.Add(e.GetString());
                        PlatformSuppression.SetHeld(names);
                    }
                }
                catch (Exception e)
                {
                    w.WriteString("error", e.GetType().Name + ": " + e.Message);
                }
                w.WriteBoolean("enabled", PlatformSuppression.Enabled);
                w.WriteStartArray("held");
                foreach (string h in PlatformSuppression.HeldNames()) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // -------------------------------------------------------------------------------------------------------------
        // Arguments

        class PieceSpec
        {
            public JsonElement? Upgrades, Style, Params, Piece;
            public float? Difficulty;
        }

        class Args
        {
            public int Seed = DefaultSeed;
            public float Difficulty = DefaultDifficulty;
            public string Hero = DefaultHero;
            public int Length = DefaultLength;
            public string Geometry = DefaultGeometry;
            public string TileSet = DefaultTileSet;
            public bool Replay = true;
            public bool Trace = true;
            /// <summary>Negative control: when &gt;= 0, a third replay ("control") runs inputsOnly with the recorded input neutralised from this tick on.</summary>
            public int ControlFromTick = -1;

            // ck-level/2
            public bool V2;
            public List<PieceSpec> Pieces;
            public int NumPieces = -1;
            public int PieceLength = -1;
            public JsonElement? LevelSeed, HeroCustom, ReplayHero, Upgrades, Style, Params;
            public bool Coins = true;
            public bool Settings = true;
            public bool Verify;
            public List<string> Held;
            public string Raw;
        }

        /// <summary>The API's own arguments, for the schema (the settings groups are found by reflection).</summary>
        static void WriteApiArgs(Utf8JsonWriter w)
        {
            void Arg(string name, string type, object def, string what)
            {
                w.WriteStartObject(name);
                w.WriteString("type", type);
                w.WritePropertyName("default");
                if (def == null) w.WriteNullValue();
                else if (def is int i) w.WriteNumberValue(i);
                else if (def is float f) w.WriteNumberValue(f);
                else if (def is bool b) w.WriteBooleanValue(b);
                else w.WriteStringValue(def.ToString());
                w.WriteString("controls", what);
                w.WriteEndObject();
            }
            Arg("format", "string", Format, "\"ck-level/2\" forces the v2 document; any v2 argument implies it");
            Arg("seed", "int", DefaultSeed, "re-seeds Tools.GlobalRnd: every random draw of the generation");
            Arg("difficulty", "float", DefaultDifficulty, "0 Easy .. 4 Masochistic: draws each piece's upgrades (DifficultyGroups) unless the piece gives its own");
            Arg("hero", "string", DefaultHero, "a hero class (schema.heroes)");
            Arg("heroCustom", "object", null, "Hero Factory hero {base, shape, move, special, phsx:{…}} (schema.heroCustom); replaces hero");
            Arg("length", "int", DefaultLength, "level length; split over pieces as the Custom Level menu does (1: L, 2: .7L, 3: .5L, 4: .4L, n: L/n)");
            Arg("pieceLength", "int", null, "each piece's length, instead of splitting length");
            Arg("geometry", "string", DefaultGeometry, "schema.geometry; more than one piece needs Right");
            Arg("tileset", "string", DefaultTileSet, "schema.tilesets");
            Arg("pieces", "array|int", null, "k pieces (checkpoints between them). Each: {upgrades:{Upgrade: level}, difficulty, style:{…}, params:{Gen:{…}}, piece:{…}}; an int = that many pieces at difficulty");
            Arg("upgrades", "object", null, "{Upgrade: level} for every piece without its own (replaces difficulty's draw)");
            Arg("style", "object", null, "StyleData overrides for every piece (schema.style.<geometry>), applied after the style's draw");
            Arg("params", "object", null, "{Gen: {member: value}} AutoGen parameter overrides for every piece (schema.params); a param is set to a constant");
            Arg("levelSeed", "object", null, "LevelSeedData overrides (schema.levelSeed), set before Initialize");
            Arg("replayHero", "object", null, "BobPhsx members changed on the hero for every replay (e.g. {NumJumps:1}, {JetPack:false})");
            Arg("held", "array", null, "platform items held for an extra replay of every piece (schema.upgrades.platformItems)");
            Arg("verify", "bool", false, "nested-piece verifier: each piece replayed with S_k held, and with each new item of S_k dropped");
            Arg("coins", "bool", true, "v2: the coins section (witness tick per coin)");
            Arg("settings", "bool", true, "v2: each piece's resolved style and generator parameters in the document");
            Arg("replay", "bool", true, "replay checks");
            Arg("trace", "bool", true, "the dense per-tick trace of each recording");
            Arg("controlFromTick", "int", -1, "v1 negative control: input neutralised from this tick on (piece 0)");
        }

        public static string Schema()
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
                GeneratorSettings.WriteSchema(w, WriteApiArgs);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        static JsonElement? Obj(JsonElement v) { return v.ValueKind == JsonValueKind.Null ? (JsonElement?)null : v.Clone(); }

        static Args ParseArgs(string json)
        {
            var a = new Args();
            if (string.IsNullOrEmpty(json)) return a;
            a.Raw = json;
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "seed": a.Seed = p.Value.GetInt32(); break;
                        case "difficulty": a.Difficulty = p.Value.GetSingle(); break;
                        case "hero": a.Hero = p.Value.GetString(); break;
                        case "length": a.Length = p.Value.GetInt32(); break;
                        case "geometry": a.Geometry = p.Value.GetString(); break;
                        case "tileset": a.TileSet = p.Value.GetString(); break;
                        case "replay": a.Replay = p.Value.GetBoolean(); break;
                        case "trace": a.Trace = p.Value.GetBoolean(); break;
                        case "controlFromTick": a.ControlFromTick = p.Value.GetInt32(); break;

                        case "format":
                            if (p.Value.GetString() == Format2) a.V2 = true;
                            else if (p.Value.GetString() != Format) throw new ArgumentException("unknown format '" + p.Value.GetString() + "'");
                            break;
                        case "pieces":
                            a.V2 = true;
                            if (p.Value.ValueKind == JsonValueKind.Number) a.NumPieces = p.Value.GetInt32();
                            else
                            {
                                a.Pieces = new List<PieceSpec>();
                                foreach (var e in p.Value.EnumerateArray())
                                {
                                    var spec = new PieceSpec();
                                    foreach (var q in e.EnumerateObject())
                                    {
                                        switch (q.Name)
                                        {
                                            case "upgrades": spec.Upgrades = Obj(q.Value); break;
                                            case "difficulty": spec.Difficulty = q.Value.GetSingle(); break;
                                            case "style": spec.Style = Obj(q.Value); break;
                                            case "params": spec.Params = Obj(q.Value); break;
                                            case "piece": spec.Piece = Obj(q.Value); break;
                                            default: throw new ArgumentException("unknown piece argument '" + q.Name + "'");
                                        }
                                    }
                                    a.Pieces.Add(spec);
                                }
                                a.NumPieces = a.Pieces.Count;
                            }
                            if (a.NumPieces < 1) throw new ArgumentException("pieces: at least one");
                            break;
                        case "pieceLength": a.V2 = true; a.PieceLength = p.Value.GetInt32(); break;
                        case "levelSeed": a.V2 = true; a.LevelSeed = Obj(p.Value); break;
                        case "heroCustom": a.V2 = true; a.HeroCustom = Obj(p.Value); break;
                        case "replayHero": a.V2 = true; a.ReplayHero = Obj(p.Value); break;
                        case "upgrades": a.V2 = true; a.Upgrades = Obj(p.Value); break;
                        case "style": a.V2 = true; a.Style = Obj(p.Value); break;
                        case "params": a.V2 = true; a.Params = Obj(p.Value); break;
                        case "coins": a.V2 = true; a.Coins = p.Value.GetBoolean(); break;
                        case "settings": a.V2 = true; a.Settings = p.Value.GetBoolean(); break;
                        case "verify": a.V2 = true; a.Verify = p.Value.GetBoolean(); break;
                        case "held":
                            a.V2 = true;
                            if (p.Value.ValueKind == JsonValueKind.Null) a.Held = null;
                            else { a.Held = new List<string>(); foreach (var e in p.Value.EnumerateArray()) a.Held.Add(e.GetString()); }
                            break;
                        default: throw new ArgumentException("unknown argument '" + p.Name + "'");
                    }
                }
            }
            if (a.V2 && a.NumPieces < 0) a.NumPieces = 1;
            return a;
        }

        static BobPhsx ResolveHero(string name)
        {
            string typeName = typeof(BobPhsx).Namespace + "." + HeroClassPrefix + name;
            Type t = typeof(BobPhsx).Assembly.GetType(typeName, false, true);
            if (t == null || !typeof(BobPhsx).IsAssignableFrom(t))
                throw new ArgumentException("unknown hero '" + name + "' (no class " + typeName + ")");
            PropertyInfo inst = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (inst == null) throw new ArgumentException("hero class " + t.Name + " has no static Instance");
            return (BobPhsx)inst.GetValue(null);
        }

        static T EnumArg<T>(JsonElement o, string name, T def) where T : struct
        {
            JsonElement v;
            if (!o.TryGetProperty(name, out v)) return def;
            return (T)GeneratorSettings.Convert(typeof(T), v, "heroCustom." + name);
        }

        /// <summary>The Hero Factory's path (CustomHero_GUI): MakeCustom(base, shape, move, special) + SetCustomPhsx(sliders).</summary>
        static BobPhsx MakeCustomHero(JsonElement hc, List<string> clamped)
        {
            foreach (var p in hc.EnumerateObject())
                if (p.Name != "base" && p.Name != "shape" && p.Name != "move" && p.Name != "special" && p.Name != "phsx")
                    throw new ArgumentException("unknown heroCustom argument '" + p.Name + "'");

            BobPhsx hero = BobPhsx.MakeCustom(EnumArg(hc, "base", Hero_BaseType.Classic), EnumArg(hc, "shape", Hero_Shape.Classic),
                EnumArg(hc, "move", Hero_MoveMod.Classic), EnumArg(hc, "special", Hero_Special.Classic));

            // CustomPhsxData.Init reads the bounds table without filling it (InitBounds runs inside Bounds(), which the
            // Hero Factory menu always calls first): fill it, or every slider defaults to 0.
            BobPhsx.CustomPhsxData.Bounds(0);
            var data = new BobPhsx.CustomPhsxData();
            data.Init();
            JsonElement phsx;
            if (hc.TryGetProperty("phsx", out phsx))
            {
                foreach (var p in phsx.EnumerateObject())
                {
                    BobPhsx.CustomData d = (BobPhsx.CustomData)GeneratorSettings.Convert(typeof(BobPhsx.CustomData), JsonDocument.Parse("\"" + p.Name + "\"").RootElement, "heroCustom.phsx");
                    var bounds = BobPhsx.CustomPhsxData.Bounds(d);
                    float v = p.Value.GetSingle();
                    float r = CoreMath.Restrict(bounds.MinValue, bounds.MaxValue, v);
                    if (r != v) clamped.Add(p.Name);
                    data[d] = r;
                }
            }
            hero.SetCustomPhsx(data);
            return hero;
        }

        /// <summary>The Custom Level menu's split of a level length over its pieces (CustomLevel_GUI's length slider).</summary>
        static int SplitLength(int length, int pieces)
        {
            switch (pieces)
            {
                case 1: return length;
                case 2: return (int)(length * .7f);
                case 3: return (int)(length * .5f);
                case 4: return (int)(length * .4f);
                default: return length / pieces;
            }
        }

        static void ApplyUpgrades(Upgrades u, JsonElement levels, string where)
        {
            foreach (var p in levels.EnumerateObject())
            {
                Upgrade up;
                if (!Enum.TryParse(p.Name, true, out up)) throw new ArgumentException(where + ": unknown upgrade '" + p.Name + "'");
                float v = p.Value.GetSingle();
                if (v < 0 || v > 10) throw new ArgumentException(where + "." + p.Name + ": level " + v + " outside 0..10");
                u[up] = v;
            }
        }

        static void ApplyStyleAndParams(PieceSeedData piece, Args a, PieceSpec spec, bool withParams)
        {
            if (a.Style != null) GeneratorSettings.Apply(piece.Style, a.Style.Value, "style");
            if (spec != null && spec.Style != null) GeneratorSettings.Apply(piece.Style, spec.Style.Value, "pieces[" + piece.MyPieceIndex + "].style");
            if (!withParams) return;
            ApplyParams(piece, a.Params, "params");
            if (spec != null) ApplyParams(piece, spec.Params, "pieces[" + piece.MyPieceIndex + "].params");
        }

        static void ApplyParams(PieceSeedData piece, JsonElement? overrides, string where)
        {
            if (overrides == null) return;
            foreach (var g in overrides.Value.EnumerateObject())
            {
                AutoGen gen = GeneratorSettings.FindGen(g.Name);
                if (gen == null) throw new ArgumentException(where + ": unknown generator '" + g.Name + "'");
                AutoGen_Parameters p = piece.Style.FindParams(gen);
                if (p == null) throw new ArgumentException(where + "." + g.Name + ": the generator has no parameters");
                GeneratorSettings.Apply(p, g.Value, where + "." + g.Name);
            }
        }

        /// <summary>
        /// One piece's settings, in the engine's own order: the upgrade levels (explicit = the Custom Level menu's path,
        /// CopyUpgrades + StandardClose; otherwise difficulty's draw), then PieceSeedData and style overrides (after the
        /// style's draw), then — once the generators have set their parameters from the upgrades — parameter overrides
        /// (a StyleData.MyModParams callback, the engine's hook for exactly this).
        /// </summary>
        static void ModPiece(PieceSeedData piece, LevelSeedData.CustomDifficulty difficulty, Args a, Func<LevelSeedData> data)
        {
            PieceSpec spec = a.Pieces != null && piece.MyPieceIndex >= 0 && piece.MyPieceIndex < a.Pieces.Count ? a.Pieces[piece.MyPieceIndex] : null;

            JsonElement? levels = spec != null && spec.Upgrades != null ? spec.Upgrades : a.Upgrades;
            if (levels != null)
            {
                RndDifficulty.ZeroUpgrades(piece.MyUpgrades1);
                ApplyUpgrades(piece.MyUpgrades1, levels.Value, "pieces[" + piece.MyPieceIndex + "].upgrades");
                piece.StandardClose();
            }
            else if (spec != null && spec.Difficulty != null)
                DifficultyGroups.FixedPieceMod(spec.Difficulty.Value, data(), false, false)(piece);
            else
                difficulty(piece);

            if (spec != null && spec.Piece != null) GeneratorSettings.Apply(piece, spec.Piece.Value, "pieces[" + piece.MyPieceIndex + "].piece");
            ApplyStyleAndParams(piece, a, spec, false);
            piece.Style.MyModParams = (level, p) => ApplyStyleAndParams(p, a, spec, true);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Generate

        public static string Generate(string argsJson)
        {
            LastGenerateMs = LastExportMs = LastReplayMs = LastVerifyMs = 0;

            GameData holdGame = Tools.CurGameData;
            Level holdLevel = Tools.CurLevel;
            Rand holdRnd = Tools.GlobalRnd;
            bool holdSuppression = PlatformSuppression.Enabled;
            var holdHeld = new List<string>(PlatformSuppression.HeldNames());

            var buffer = new MemoryStream();
            GameData game = null;
            Args a = null;
            try
            {
                NonFinite = 0;
                a = ParseArgs(argsJson);
                if (!Ready) throw new InvalidOperationException("not ready: content still loading");
                PlatformSuppression.Disable();

                var sw = Stopwatch.StartNew();

                // Every random draw of a generation descends from GlobalRnd (LevelSeedData's constructor seeds its own Rand from it,
                // DifficultyGroups draws from it directly), so the seed argument re-seeds it.
                Tools.GlobalRnd = new Rand(a.Seed);

                var clamped = new List<string>();
                BobPhsx hero = a.HeroCustom != null ? MakeCustomHero(a.HeroCustom.Value, clamped) : ResolveHero(a.Hero);
                LevelGeometry geometry = (LevelGeometry)Enum.Parse(typeof(LevelGeometry), a.Geometry, true);

                LevelSeedData data;
                if (!a.V2)
                    data = RegularLevel.HeroLevel(a.Difficulty, hero, null, geometry, a.Length, false);
                else
                {
                    if (a.NumPieces > 1 && geometry != LevelGeometry.Right)
                        throw new ArgumentException("more than one piece needs geometry Right (the Custom Level menu forces vertical levels to one piece)");
                    int pieceLength = a.PieceLength > 0 ? a.PieceLength : SplitLength(a.Length, a.NumPieces);
                    LevelSeedData made = null;
                    data = RegularLevel.HeroLevel(a.Difficulty, hero, null, geometry, a.NumPieces, pieceLength, false,
                        d => { made = d; if (a.LevelSeed != null) GeneratorSettings.Apply(d, a.LevelSeed.Value, "levelSeed", GeneratorSettings.LevelSeedControlled); },
                        custom => piece => ModPiece(piece, custom, a, () => made));
                }
                data.SetTileSet(a.TileSet);
                data.NoMusicStart = true;

                game = new NormalGameData();
                game.Init();
                game.DefaultHeroType = data.DefaultHeroType;
                game.DefaultHeroType2 = data.DefaultHeroType2;

                Tools.CurGameData = game;
                Level level = data.MakeLevel(true, game);
                if (level == null) throw new InvalidOperationException("MakeLevel returned null");
                if (level.ReturnedEarly) throw new InvalidOperationException("MakeLevel returned early");

                game.MyLevel = level;
                level.MyGame = game;
                Tools.CurLevel = level;
                level.CanWatchComputer = level.CanWatchReplay = true;
                level.PlayMode = 0;
                level.ResetAll(false, false);

                LastGenerateMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();

                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    w.WriteString("format", a.V2 ? Format2 : Format);

                    w.WriteStartObject("args");
                    w.WriteNumber("seed", a.Seed);
                    w.WriteNumber("difficulty", a.Difficulty);
                    w.WriteString("hero", a.Hero);
                    w.WriteNumber("length", a.Length);
                    w.WriteString("geometry", geometry.ToString());
                    w.WriteString("tileset", a.TileSet);
                    if (a.V2)
                    {
                        // The whole request, as given (so the document carries its own inputs).
                        w.WritePropertyName("request");
                        using (var req = JsonDocument.Parse(a.Raw)) req.RootElement.WriteTo(w);
                    }
                    w.WriteEndObject();

                    w.WriteStartObject("engine");
                    w.WriteString("build", Environment.GetEnvironmentVariable("CK_BUILD") ?? "unknown");
                    w.WriteNumber("physicsHz", PhysicsHz);
                    w.WriteString("units", Units);
                    w.WriteEndObject();

                    w.WriteStartObject("level");
                    w.WriteString("tileset", data.MyTileSet == null ? null : data.MyTileSet.Name);
                    w.WriteString("heroResolved", level.DefaultHeroType.GetType().Name);
                    w.WriteNumber("seedDrawn", data.Seed);
                    WriteVec(w, "bl", level.BL);
                    WriteVec(w, "tr", level.TR);
                    w.WriteNumber("par", level.Par);
                    if (a.V2)
                    {
                        w.WriteNumber("numPieces", level.LevelPieces.Count);
                        if (a.HeroCustom != null)
                        {
                            w.WriteString("heroSpecification", level.DefaultHeroType.Specification.ToString());
                            w.WriteString("heroPhsx", level.DefaultHeroType.MyCustomPhsxData.ToString());
                            w.WriteStartArray("heroPhsxClamped");
                            foreach (string c in clamped) w.WriteStringValue(c);
                            w.WriteEndArray();
                            WriteHeroAbility(w, "heroAbility", level.DefaultHeroType);
                        }
                        if (a.LevelSeed != null) GeneratorSettings.WriteValues(w, "levelSeed", data);
                    }
                    w.WriteEndObject();

                    ObjIds.Clear();
                    if (a.V2) WriteSeedPieces(w, data, a.Settings);
                    WritePieces(w, level, a.Trace, a.V2);
                    WriteObjects(w, "blocks", level.Blocks, a.V2, data);
                    WriteObjects(w, "objects", level.Objects, a.V2, data);

                    w.WriteNumber("nonFiniteValues", NonFinite);

                    w.WriteStartObject("goal");
                    WriteDoor(w, "startDoor", level.StartDoor);
                    WriteDoor(w, "door", level.FinalDoor);
                    w.WriteEndObject();

                    LastExportMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();

                    if (!a.V2)
                    {
                        if (a.Replay)
                        {
                            w.WriteStartObject("replayCheck");
                            ReplayCheck(w, "engine", level, false);
                            ReplayCheck(w, "inputsOnly", level, true);
                            if (a.ControlFromTick >= 0)
                            {
                                ComputerRecording rec = level.CurPiece.Recording[0];
                                BobInput[] hold = (BobInput[])rec.Input.Clone();
                                for (int t = a.ControlFromTick; t < rec.Input.Length; t++) rec.Input[t].Clean();
                                try { ReplayCheck(w, "control", level, true); }
                                finally { Array.Copy(hold, rec.Input, hold.Length); }
                            }
                            w.WriteEndObject();
                        }
                        LastReplayMs = sw.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        WriteV2Replays(w, level, data, a, sw);
                    }

                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (Exception e)
            {
                var err = new MemoryStream();
                using (var w = new Utf8JsonWriter(err))
                {
                    w.WriteStartObject();
                    w.WriteString("format", a != null && a.V2 ? Format2 : Format);
                    w.WriteString("error", e.GetType().Name + ": " + e.Message);
                    w.WriteEndObject();
                }
                Console.WriteLine("[generator-api] error: " + e);
                return Encoding.UTF8.GetString(err.ToArray());
            }
            finally
            {
                Level.ReplayProbe = false;
                Level.ReplaySnapDisabled = false;
                Level.ReplayRunPastEnd = false;
                Probe = null;
                if (holdSuppression) PlatformSuppression.SetHeld(holdHeld); else PlatformSuppression.Disable();
                if (game != null) game.Release();
                Tools.CurGameData = holdGame;
                Tools.CurLevel = holdLevel;
                Tools.GlobalRnd = holdRnd;
            }
        }

        static void WriteHeroAbility(Utf8JsonWriter w, string name, BobPhsx hero)
        {
            w.WriteStartObject(name);
            BobPhsxNormal normal = hero as BobPhsxNormal;
            if (normal != null)
            {
                w.WriteNumber("NumJumps", normal.NumJumps);
                w.WriteBoolean("JetPack", normal.JetPack);
                w.WriteNumber("JetPackAccel", normal.JetPackAccel);
                w.WriteNumber("JetPackLength", normal.JetPackLength);
                w.WriteNumber("BobJumpLength", normal.BobJumpLength);
                w.WriteNumber("BobJumpLength2", normal.BobJumpLength2);
            }
            w.WriteNumber("Gravity", hero.Gravity);
            w.WriteNumber("MaxSpeed", hero.MaxSpeed);
            w.WriteEndObject();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Export

        /// <summary>Non-finite values (JSON has no Infinity/NaN) are written as null and counted in level.nonFiniteValues.</summary>
        static int NonFinite;
        static void WriteF(Utf8JsonWriter w, float f)
        {
            if (float.IsFinite(f)) w.WriteNumberValue(f);
            else { NonFinite++; w.WriteNullValue(); }
        }

        static void WriteVec(Utf8JsonWriter w, string name, Vector2 v)
        {
            w.WriteStartArray(name);
            WriteF(w, v.X);
            WriteF(w, v.Y);
            w.WriteEndArray();
        }

        static void WriteDoor(Utf8JsonWriter w, string name, Door door)
        {
            if (door == null) { w.WriteNull(name); return; }
            WriteVec(w, name, door.Pos);
        }

        /// <summary>The real piece index of a PieceSeeds entry (-1 for a ladder).</summary>
        static int PieceOf(LevelSeedData data, int seedPiece)
        {
            if (seedPiece < 0 || seedPiece >= data.PieceSeeds.Count) return -1;
            PieceSeedData p = data.PieceSeeds[seedPiece];
            return p.Ladder == Level.LadderType.None ? p.MyPieceIndex : -1;
        }

        /// <summary>The ladder index of a PieceSeeds entry: ladder k joins piece k to piece k + 1 (-1 for a piece).</summary>
        static int LadderOf(LevelSeedData data, int seedPiece)
        {
            if (seedPiece < 0 || seedPiece >= data.PieceSeeds.Count) return -1;
            int k = -1;
            for (int j = 0; j <= seedPiece; j++)
                if (data.PieceSeeds[j].Ladder != Level.LadderType.None) k++;
            return data.PieceSeeds[seedPiece].Ladder != Level.LadderType.None ? k : -1;
        }

        static void WriteUpgrades(Utf8JsonWriter w, string name, Upgrades u)
        {
            w.WriteStartObject(name);
            foreach (Upgrade up in Enum.GetValues(typeof(Upgrade)))
                if (u[up] != 0) w.WriteNumber(up.ToString(), u[up]);
            w.WriteEndObject();
        }

        /// <summary>The platform items a piece's upgrades switch on (level &gt; 0).</summary>
        static List<int> ItemsOf(PieceSeedData p)
        {
            var items = new List<int>();
            for (int i = 0; i < PlatformSuppression.Items.Length; i++)
                if (p.MyUpgrades1[PlatformSuppression.Items[i]] > 0) items.Add(i);
            return items;
        }

        static void WriteSeedPieces(Utf8JsonWriter w, LevelSeedData data, bool settings)
        {
            w.WriteStartArray("seedPieces");
            for (int j = 0; j < data.PieceSeeds.Count; j++)
            {
                PieceSeedData p = data.PieceSeeds[j];
                w.WriteStartObject();
                w.WriteNumber("seedPiece", j);
                bool ladder = p.Ladder != Level.LadderType.None;
                w.WriteString("kind", ladder ? "ladder" : "piece");
                if (ladder)
                {
                    w.WriteNumber("ladder", LadderOf(data, j));
                    w.WriteString("ladderType", p.Ladder.ToString());
                }
                else
                {
                    w.WriteNumber("piece", p.MyPieceIndex);
                    w.WriteNumber("paths", p.Paths);
                    WriteUpgrades(w, "upgrades", p.MyUpgrades1);
                    w.WriteStartArray("items");
                    foreach (int i in ItemsOf(p)) w.WriteStringValue(PlatformSuppression.Items[i].ToString());
                    w.WriteEndArray();
                }
                WriteVec(w, "start", p.Start);
                WriteVec(w, "end", p.End);
                if (!ladder && settings && p.Style != null)
                {
                    GeneratorSettings.WriteValues(w, "piece", p);
                    GeneratorSettings.WriteValues(w, "style", p.Style);
                    if (p.Style.GenParams != null)
                    {
                        w.WriteStartObject("params");
                        foreach (AutoGen gen in Generators.Gens)
                        {
                            AutoGen_Parameters gp;
                            if (p.Style.GenParams.TryGetValue(gen, out gp) && gp != null)
                                GeneratorSettings.WriteValues(w, GeneratorSettings.GenName(gen), gp);
                        }
                        w.WriteEndObject();
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void WritePieces(Utf8JsonWriter w, Level level, bool trace, bool v2)
        {
            w.WriteStartArray("pieces");
            for (int p = 0; p < level.LevelPieces.Count; p++)
            {
                LevelPiece piece = level.LevelPieces[p];
                w.WriteStartObject();
                w.WriteNumber("index", p);
                w.WriteNumber("startStep", piece.StartPhsxStep);
                w.WriteNumber("pieceLength", piece.PieceLength);
                w.WriteNumber("bobs", piece.NumBobs);

                w.WriteStartArray("replay");
                for (int b = 0; b < piece.NumBobs; b++)
                {
                    ComputerRecording rec = piece.Recording[b];
                    w.WriteStartObject();
                    WriteVec(w, "start", piece.StartData[b].Position);
                    WriteVec(w, "startVel", piece.StartData[b].Velocity);

                    int ticks = Math.Min(piece.PieceLength, rec.Input == null ? 0 : rec.Input.Length);
                    w.WriteNumber("ticks", ticks);

                    // Sparse input: one row whenever the input changes. [tick, xVec.x, xVec.y, A, B]
                    w.WriteStartArray("input");
                    BobInput prev = new BobInput();
                    for (int t = 0; t < ticks; t++)
                    {
                        BobInput cur = rec.Input[t];
                        if (t == 0 || cur.xVec != prev.xVec || cur.A_Button != prev.A_Button || cur.B_Button != prev.B_Button)
                        {
                            w.WriteStartArray();
                            w.WriteNumberValue(t);
                            WriteF(w, cur.xVec.X);
                            WriteF(w, cur.xVec.Y);
                            w.WriteNumberValue(cur.A_Button ? 1 : 0);
                            w.WriteNumberValue(cur.B_Button ? 1 : 0);
                            w.WriteEndArray();
                        }
                        prev = cur;
                    }
                    w.WriteEndArray();

                    // Dense trace: [tick, x, y, vx, vy, onGround]
                    if (trace && rec.AutoLocs != null)
                    {
                        w.WriteStartArray("trace");
                        for (int t = 0; t < ticks; t++)
                        {
                            w.WriteStartArray();
                            w.WriteNumberValue(t);
                            WriteF(w, rec.AutoLocs[t].X);
                            WriteF(w, rec.AutoLocs[t].Y);
                            WriteF(w, rec.AutoVel[t].X);
                            WriteF(w, rec.AutoVel[t].Y);
                            w.WriteNumberValue(rec.AutoOnGround != null && rec.AutoOnGround[t] ? 1 : 0);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static string KindName(Type t)
        {
            string n = t.Name;
            return n.EndsWith(AutoSuffix, StringComparison.Ordinal) ? n.Substring(0, n.Length - AutoSuffix.Length) : n;
        }

        static readonly Dictionary<Type, FieldInfo[]> ShapeFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>Every AABox / CircleBox / MovingLine field an object's class hierarchy declares, in a stable order.</summary>
        static FieldInfo[] GetShapeFields(Type type)
        {
            FieldInfo[] found;
            if (ShapeFields.TryGetValue(type, out found)) return found;

            var list = new List<FieldInfo>();
            var hierarchy = new List<Type>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType) hierarchy.Add(t);
            hierarchy.Reverse();
            foreach (Type t in hierarchy)
            {
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Array.Sort(fields, (x, y) => string.CompareOrdinal(x.Name, y.Name));
                foreach (FieldInfo f in fields)
                    if (f.FieldType == typeof(AABox) || f.FieldType == typeof(CircleBox) || f.FieldType == typeof(MovingLine))
                        list.Add(f);
            }
            found = list.ToArray();
            ShapeFields[type] = found;
            return found;
        }

        static readonly Dictionary<Type, FieldInfo[]> ExtraFields = new Dictionary<Type, FieldInfo[]>();

        static bool IsExtraType(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(bool) || t == typeof(Vector2) || t == typeof(string) || t.IsEnum;
        }

        /// <summary>
        /// The kind-specific parameters: every PUBLIC instance field of a plain type (int, float, bool, Vector2, string, enum)
        /// that the object's classes below ObjectBase declare, base class first, by name within a class.
        /// </summary>
        static FieldInfo[] GetExtraFields(Type type)
        {
            FieldInfo[] found;
            if (ExtraFields.TryGetValue(type, out found)) return found;

            var list = new List<FieldInfo>();
            var hierarchy = new List<Type>();
            for (Type t = type; t != null && t != typeof(ObjectBase); t = t.BaseType) hierarchy.Add(t);
            hierarchy.Reverse();
            foreach (Type t in hierarchy)
            {
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                Array.Sort(fields, (x, y) => string.CompareOrdinal(x.Name, y.Name));
                foreach (FieldInfo f in fields)
                    if (IsExtraType(f.FieldType)) list.Add(f);
            }
            found = list.ToArray();
            ExtraFields[type] = found;
            return found;
        }

        static void WriteExtra(Utf8JsonWriter w, ObjectBase obj)
        {
            w.WriteStartObject("extra");
            foreach (FieldInfo f in GetExtraFields(obj.GetType()))
            {
                object v = f.GetValue(obj);
                if (v is int i) w.WriteNumber(f.Name, i);
                else if (v is float fl) { w.WritePropertyName(f.Name); WriteF(w, fl); }
                else if (v is bool b) w.WriteBoolean(f.Name, b);
                else if (v is Vector2 vec) WriteVec(w, f.Name, vec);
                else if (v is string str) w.WriteString(f.Name, str);
                else if (v != null && v.GetType().IsEnum) w.WriteString(f.Name, v.ToString());
                else w.WriteNull(f.Name);
            }
            w.WriteEndObject();
        }

        /// <summary>v2: the id every exported object carries (blocks first, then objects), for coins and contacts; keyed by
        /// Core.MyGuid, which survives Level.ResetAll (a reset replaces every object with a clone).</summary>
        static readonly Dictionary<ulong, int> ObjIds = new Dictionary<ulong, int>();

        static void WriteObjects<T>(Utf8JsonWriter w, string name, List<T> list, bool v2, LevelSeedData data) where T : ObjectBase
        {
            w.WriteStartArray(name);
            foreach (T obj in list)
            {
                if (obj == null || obj.Core.MarkedForDeletion) continue;

                w.WriteStartObject();
                if (v2)
                {
                    int id = ObjIds.Count;
                    ObjIds[obj.Core.MyGuid] = id;
                    w.WriteNumber("id", id);
                }
                w.WriteString("kind", KindName(obj.GetType()));
                WriteVec(w, "pos", obj.Core.Data.Position);
                if (!string.IsNullOrEmpty(obj.Core.EditorCode1)) w.WriteString("code", obj.Core.EditorCode1);
                if (v2)
                {
                    int piece = PieceOf(data, obj.Core.GenSeedPiece), ladder = LadderOf(data, obj.Core.GenSeedPiece);
                    if (piece >= 0) w.WriteNumber("piece", piece);
                    if (ladder >= 0) w.WriteNumber("ladder", ladder);
                    if (obj.Core.GateItem >= 0) w.WriteString("gate", PlatformSuppression.Items[obj.Core.GateItem].ToString());
                }

                w.WriteStartArray("shapes");
                foreach (FieldInfo f in GetShapeFields(obj.GetType()))
                {
                    object v = f.GetValue(obj);
                    if (v == null) continue;
                    w.WriteStartObject();
                    w.WriteString("field", f.Name);
                    if (v is AABox box)
                    {
                        if (box.Current == null) { w.WriteEndObject(); continue; }
                        w.WriteString("type", "box");
                        WriteVec(w, "bl", box.Current.BL);
                        WriteVec(w, "tr", box.Current.TR);
                        if (box.TopOnly) w.WriteBoolean("topOnly", true);
                    }
                    else if (v is CircleBox circle)
                    {
                        w.WriteString("type", "circle");
                        WriteVec(w, "center", circle.Center);
                        w.WritePropertyName("radius"); WriteF(w, circle.Radius);
                    }
                    else if (v is MovingLine line)
                    {
                        w.WriteString("type", "line");
                        WriteVec(w, "p1", line.Current.p1);
                        WriteVec(w, "p2", line.Current.p2);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                WriteExtra(w, obj);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // -------------------------------------------------------------------------------------------------------------
        // v1 replay check (unchanged: ck-level/1 bytes)

        /// <summary>
        /// Plays the level's first piece back through the engine's own computer-watch path (Level.WatchComputer).
        /// "engine": as the game does it — the bob follows the recorded input and is snapped back onto the recorded
        /// position whenever it drifts (Level.UpdateBobs); the snaps are counted.
        /// "inputsOnly": the snap is disabled, so the bob is driven by the recorded input alone.
        /// In both, deaths are counted instead of taken (Bob.Die under Level.ReplayProbe), and the goal is the
        /// final door's own reach test (Door.BobInReach).
        /// </summary>
        static void ReplayCheck(Utf8JsonWriter w, string name, Level level, bool inputsOnly)
        {
            Level.ReplayProbe = true;
            Level.ReplaySnapDisabled = inputsOnly;
            Level.ReplayRunPastEnd = true;
            Level.ReplaySnaps = 0;
            Level.ReplayMaxDrift = 0;
            Level.ReplayDeaths = 0;
            Level.ReplayFirstDeathTick = -1;
            Level.ReplayFirstDeathKind = null;

            if (level.Watching) level.EndReplay();
            level.WatchComputer(false);

            Door door = level.FinalDoor;
            LevelPiece piece = level.CurPiece;
            ComputerRecording rec = piece.Recording[0];
            int limit = piece.PieceLength + ReplayGoalSlackTicks;
            int goalTick = -1, steps, compared = 0, firstCompared = -1;
            float maxDev = 0;
            Bob bob = null;
            Vector2 atEnd = Vector2.Zero;

            for (steps = 0; steps < limit; steps++)
            {
                level.PhsxStep(true);
                if (level.Bobs.Count == 0) break;
                bob = level.Bobs[0];

                // The step just taken wrote tick CurPhsxStep - 1; compare it with the recording (the generator's own trace).
                int t = level.CurPhsxStep - 1 - bob.IndexOffset;
                if (t >= 0 && t < piece.PieceLength)
                {
                    maxDev = Math.Max(maxDev, (bob.Core.Data.Position - rec.AutoLocs[t]).Length());
                    if (compared++ == 0) firstCompared = t;
                    if (t == piece.PieceLength - 1) atEnd = bob.Core.Data.Position;
                }
                if (door != null && door.BobInReach(bob)) { goalTick = t; break; }
            }

            w.WriteStartObject(name);
            w.WriteBoolean("reachedGoal", goalTick >= 0);
            w.WriteNumber("ticksToGoal", goalTick);
            w.WriteBoolean("goalWithinRecording", goalTick >= 0 && goalTick < piece.PieceLength);
            w.WriteNumber("stepsRun", steps);
            w.WriteNumber("ticksCompared", compared);
            w.WriteNumber("firstTickCompared", firstCompared);
            w.WriteNumber("maxDeviationFromTrace", maxDev);
            w.WriteNumber("snaps", Level.ReplaySnaps);
            w.WriteNumber("maxSnapDrift", Level.ReplayMaxDrift);
            w.WriteNumber("deaths", Level.ReplayDeaths);
            w.WriteNumber("firstDeathTick", Level.ReplayFirstDeathTick);
            w.WriteString("firstDeathBy", Level.ReplayFirstDeathKind);
            if (door != null) w.WriteNumber("doorDistanceAtRecordingEnd", (atEnd - door.Pos).Length());
            if (bob != null) WriteVec(w, "finalPos", bob.Core.Data.Position);
            w.WriteEndObject();

            level.EndReplay();
            Level.ReplayProbe = false;
            Level.ReplaySnapDisabled = false;
            Level.ReplayRunPastEnd = false;
        }
    }
}
