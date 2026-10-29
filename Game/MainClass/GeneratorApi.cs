using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    /// seed + difficulty + hero (+ length, geometry, tileset) in; level geometry + the computer's recording out, as JSON ("ck-level/1").
    /// Browser: [JSExport] GeneratorApi.Generate, page mode ?api=1. Native: --generate &lt;json-args&gt; --out &lt;file&gt;.
    /// </summary>
    public static partial class GeneratorApi
    {
        public const string Format = "ck-level/1";
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
        public const string HeroClassPrefix = "BobPhsx";
        /// <summary>The suffix the generator's parameter classes carry; stripped from emitted kind names.</summary>
        public const string AutoSuffix = "__Auto";

        /// <summary>True when the game was started to serve the API (no attract mode).</summary>
        public static bool ApiMode;
        /// <summary>True once content is loaded and Generate may be called.</summary>
        public static bool Ready;

        /// <summary>Native one-shot: the JSON args and the output file.</summary>
        public static string NativeArgs, NativeOut;
        public static int NativeRepeat = 1;

        public static double LastGenerateMs, LastExportMs, LastReplayMs;

        /// <summary>Consumes --api / --generate / --out / --repeat; returns the arguments the game should still see.</summary>
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

            if (NativeArgs != null)
            {
                string json = null;
                var times = new StringBuilder();
                for (int i = 0; i < NativeRepeat; i++)
                {
                    json = Generate(NativeArgs);
                    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))).Substring(0, 12);
                    times.AppendFormat(CultureInfo.InvariantCulture, "{0}generate={1:F1}ms export={2:F1}ms replay={3:F1}ms sha256={4}",
                        i == 0 ? "" : "\n", LastGenerateMs, LastExportMs, LastReplayMs, sha);
                }
                if (NativeOut != null) File.WriteAllText(NativeOut, json);
                else Console.WriteLine(json);
                if (NativeOut != null) File.WriteAllText(NativeOut + ".timing.txt", times.ToString());
                Console.WriteLine("[generator-api] wrote " + (NativeOut ?? "stdout"));
                Tools.TheGame.Exit();
            }
        }

#if BROWSER
        [JSExport]
        public static bool IsReady() { return Ready; }

        [JSExport]
        public static string GenerateJson(string argsJson) { return Generate(argsJson); }

        /// <summary>The last call's phase timings, as JSON (kept out of the document so the document stays byte-comparable).</summary>
        [JSExport]
        public static string LastTimings()
        {
            return string.Format(CultureInfo.InvariantCulture, "{{\"generateMs\":{0:F2},\"exportMs\":{1:F2},\"replayMs\":{2:F2}}}",
                LastGenerateMs, LastExportMs, LastReplayMs);
        }
#endif

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
        }

        static Args ParseArgs(string json)
        {
            var a = new Args();
            if (string.IsNullOrEmpty(json)) return a;
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
                        default: throw new ArgumentException("unknown argument '" + p.Name + "'");
                    }
                }
            }
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

        public static string Generate(string argsJson)
        {
            LastGenerateMs = LastExportMs = LastReplayMs = 0;

            GameData holdGame = Tools.CurGameData;
            Level holdLevel = Tools.CurLevel;
            Rand holdRnd = Tools.GlobalRnd;

            var buffer = new MemoryStream();
            GameData game = null;
            try
            {
                NonFinite = 0;
                Args a = ParseArgs(argsJson);
                if (!Ready) throw new InvalidOperationException("not ready: content still loading");

                var sw = Stopwatch.StartNew();

                // Every random draw of a generation descends from GlobalRnd (LevelSeedData's constructor seeds its own Rand from it,
                // DifficultyGroups draws from it directly), so the seed argument re-seeds it.
                Tools.GlobalRnd = new Rand(a.Seed);

                BobPhsx hero = ResolveHero(a.Hero);
                LevelGeometry geometry = (LevelGeometry)Enum.Parse(typeof(LevelGeometry), a.Geometry, true);

                LevelSeedData data = RegularLevel.HeroLevel(a.Difficulty, hero, null, geometry, a.Length, false);
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
                    w.WriteString("format", Format);

                    w.WriteStartObject("args");
                    w.WriteNumber("seed", a.Seed);
                    w.WriteNumber("difficulty", a.Difficulty);
                    w.WriteString("hero", a.Hero);
                    w.WriteNumber("length", a.Length);
                    w.WriteString("geometry", geometry.ToString());
                    w.WriteString("tileset", a.TileSet);
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
                    w.WriteEndObject();

                    WritePieces(w, level, a.Trace);
                    WriteObjects(w, "blocks", level.Blocks);
                    WriteObjects(w, "objects", level.Objects);

                    w.WriteNumber("nonFiniteValues", NonFinite);

                    w.WriteStartObject("goal");
                    WriteDoor(w, "startDoor", level.StartDoor);
                    WriteDoor(w, "door", level.FinalDoor);
                    w.WriteEndObject();

                    LastExportMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();

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
                    w.WriteString("format", Format);
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
                if (game != null) game.Release();
                Tools.CurGameData = holdGame;
                Tools.CurLevel = holdLevel;
                Tools.GlobalRnd = holdRnd;
            }
        }

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

        static void WritePieces(Utf8JsonWriter w, Level level, bool trace)
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

        static void WriteObjects<T>(Utf8JsonWriter w, string name, List<T> list) where T : ObjectBase
        {
            w.WriteStartArray(name);
            foreach (T obj in list)
            {
                if (obj == null || obj.Core.MarkedForDeletion) continue;

                w.WriteStartObject();
                w.WriteString("kind", KindName(obj.GetType()));
                WriteVec(w, "pos", obj.Core.Data.Position);
                if (!string.IsNullOrEmpty(obj.Core.EditorCode1)) w.WriteString("code", obj.Core.EditorCode1);

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
