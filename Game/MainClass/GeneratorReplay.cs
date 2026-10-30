using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

using Microsoft.Xna.Framework;

using CloudberryKingdom.Bobs;
using CloudberryKingdom.Blocks;
using CloudberryKingdom.Levels;
using CloudberryKingdom.InGameObjects;

namespace CloudberryKingdom
{
    /// <summary>
    /// ck-level/2 replays: every piece replayed from its start (where its checkpoint respawns the player) by the
    /// computer's recorded input alone, with the coins it collects (the witness tick), the things it stands on, and
    /// the platform items held (PlatformSuppression). The nested-piece verifier and its controls build on it.
    /// </summary>
    public static partial class GeneratorApi
    {
        class Contact { public ObjectBase Obj; public int FirstTick, Ticks; }

        class ProbeState
        {
            public readonly Dictionary<ulong, int[]> Coins = new Dictionary<ulong, int[]>();
            public readonly Dictionary<ulong, Contact> Contacts = new Dictionary<ulong, Contact>();
        }

        /// <summary>Set while a v2 replay runs; the engine's probe hooks (Coin.Interact, Bob.InteractWithBlock, BobPhsx.LandOnSomething) report into it.</summary>
        static ProbeState Probe;

        /// <summary>A non-final piece's replay reaches the end of its recording when the bob is this close to the recorded position there.</summary>
        public const float EndTolerance = 1;
        /// <summary>A replayed position further than this from the recorded one is a deviation (firstDeviationTick).</summary>
        public const float DeviationEpsilon = .01f;

        static int TickOf(Bob bob) { return bob.Core.MyLevel.CurPhsxStep - bob.IndexOffset; }

        public static void OnCoin(Coin coin, Bob bob)
        {
            if (Probe == null || Probe.Coins.ContainsKey(coin.Core.MyGuid)) return;
            Probe.Coins[coin.Core.MyGuid] = new[] { TickOf(bob), bob.MyPieceIndex };
        }

        public static void OnStood(Bob bob, ObjectBase obj)
        {
            if (Probe == null || obj == null) return;
            Contact c;
            if (!Probe.Contacts.TryGetValue(obj.Core.MyGuid, out c))
                Probe.Contacts[obj.Core.MyGuid] = c = new Contact { Obj = obj, FirstTick = TickOf(bob) };
            c.Ticks++;
        }

        class ReplayResult
        {
            public int Piece;
            public List<string> Held;
            public bool Last, ReachedGoal, ReachedEnd;
            public int GoalTick = -1, FirstDeviationTick = -1, Steps, Compared, Deaths, FirstDeathTick, Snaps, ResetStepDeaths;
            public float MaxDev, EndDeviation = -1;
            public string FirstDeathBy;
            public Vector2 FinalPos;
            public Dictionary<ulong, int[]> Coins;
            public Dictionary<ulong, Contact> Contacts;
            public List<int> Missed = new List<int>(), Other = new List<int>();
            public int CoinsInPiece, CoinsCollected;

            /// <summary>The recording was played to its end where it put the bob (or, final piece, the door was reached first).</summary>
            public bool Reached { get { return ReachedEnd || ReachedGoal; } }
            public bool Passed { get { return Reached && Deaths == 0 && Missed.Count == 0; } }
            public string FailedBy
            {
                get
                {
                    if (Deaths > 0) return "death";
                    if (!Reached) return "recordingEndNotReached";
                    if (Missed.Count > 0) return "coinsMissed";
                    return null;
                }
            }
        }

        /// <summary>
        /// Replays piece k (LevelPieces[k]) through Level.WatchComputer with the snap disabled when inputsOnly, the
        /// given items held (null = suppression off: everything solid), and the replay hero (null = the level's).
        /// Runs to the final door (last piece) or to the end of the recording (+ slack) otherwise.
        /// </summary>
        static ReplayResult ReplayPiece(Level level, LevelSeedData data, int k, bool inputsOnly, List<string> held, BobPhsx replayHero)
        {
            var r = new ReplayResult { Piece = k, Held = held, Last = k == level.LevelPieces.Count - 1 };
            BobPhsx holdHero = level.DefaultHeroType;
            try
            {
                if (level.Watching) level.EndReplay();
                level.SetCurrentPiece(k);
                if (held != null) PlatformSuppression.SetHeld(held); else PlatformSuppression.Disable();
                if (replayHero != null) level.DefaultHeroType = replayHero;

                Level.ReplayProbe = true;
                Level.ReplaySnapDisabled = inputsOnly;
                Level.ReplayRunPastEnd = true;
                Level.ReplaySnaps = 0;
                Level.ReplayMaxDrift = 0;
                Level.ReplayDeaths = 0;
                Level.ReplayFirstDeathTick = -1;
                Level.ReplayFirstDeathKind = null;
                Probe = new ProbeState();

                // The replay camera is a copy of the level's (Level.SaveCamera): start it at the piece, as a checkpoint
                // respawn does, so the first step's off-screen test (BobDeathType.LeftBehind) reads this piece's view.
                level.MainCamera.Data.Position = level.MainCamera.Target = level.LevelPieces[k].CamStartPos;
                level.MainCamera.Update();

                level.WatchComputer(false);

                Door door = r.Last ? level.FinalDoor : null;
                LevelPiece piece = level.LevelPieces[k];
                ComputerRecording rec = piece.Recording[0];
                // A piece's recording ends at the piece's end: the ladder to the next piece is crossed by no recording,
                // and the final piece's recording can stop short of the door. So a piece passes when its recording plays
                // to the end and leaves the bob where it recorded it (the final piece also when the door is reached
                // first); the final piece then runs on (with slack, no recorded input) to report the door.
                int limit = r.Last ? piece.PieceLength + ReplayGoalSlackTicks : piece.PieceLength;
                Bob bob = null;

                for (r.Steps = 0; r.Steps < limit; r.Steps++)
                {
                    level.PhsxStep(true);
                    if (r.Steps == 0 && Level.ReplayDeaths > 0)
                    {
                        // The reset step (ResetAll's own first step) runs with the camera at the piece's CamStartPos,
                        // which the engine leaves at (0,0) for every piece after the first: a bob there is off screen
                        // (BobDeathType.LeftBehind) for that one step. The game covers it with the respawn's
                        // invulnerability (Bob.ImmortalCountDown); the probe discounts it and reports it.
                        r.ResetStepDeaths = Level.ReplayDeaths;
                        Level.ReplayDeaths = 0;
                        Level.ReplayFirstDeathTick = -1;
                        Level.ReplayFirstDeathKind = null;
                    }
                    if (level.Bobs.Count == 0) break;
                    bob = level.Bobs[0];

                    int t = level.CurPhsxStep - 1 - bob.IndexOffset;
                    if (t >= 0 && t < piece.PieceLength)
                    {
                        float dev = (bob.Core.Data.Position - rec.AutoLocs[t]).Length();
                        if (dev > DeviationEpsilon && r.FirstDeviationTick < 0) r.FirstDeviationTick = t;
                        r.MaxDev = Math.Max(r.MaxDev, dev);
                        r.Compared++;
                        if (t == piece.PieceLength - 1)
                        {
                            r.EndDeviation = (bob.Core.Data.Position - rec.AutoLocs[t]).Length();
                            r.ReachedEnd = r.EndDeviation <= EndTolerance;
                        }
                    }
                    if (door != null)
                    {
                        bool inReach = false;
                        foreach (Bob b in level.Bobs) if (door.BobInReach(b)) inReach = true;
                        if (inReach) { r.GoalTick = t; r.ReachedGoal = true; break; }
                    }
                }
                if (bob != null) r.FinalPos = bob.Core.Data.Position;
                r.Deaths = Level.ReplayDeaths;
                r.FirstDeathTick = Level.ReplayFirstDeathTick;
                r.FirstDeathBy = Level.ReplayFirstDeathKind;
                r.Snaps = Level.ReplaySnaps;
                r.Coins = Probe.Coins;
                r.Contacts = Probe.Contacts;

                // Coins of this piece: collected or missed; coins of other pieces collected on the way.
                foreach (ObjectBase obj in level.Objects)
                {
                    Coin coin = obj as Coin;
                    if (coin == null || coin.Core.MarkedForDeletion) continue;
                    int id;
                    if (!ObjIds.TryGetValue(coin.Core.MyGuid, out id)) continue;
                    bool mine = PieceOf(data, coin.Core.GenSeedPiece) == k;
                    bool got = r.Coins.ContainsKey(coin.Core.MyGuid);
                    if (mine) { r.CoinsInPiece++; if (got) r.CoinsCollected++; else r.Missed.Add(id); }
                    else if (got) r.Other.Add(id);
                }
            }
            finally
            {
                if (level.Watching) level.EndReplay();
                level.DefaultHeroType = holdHero;
                Probe = null;
                PlatformSuppression.Disable();
                Level.ReplayProbe = false;
                Level.ReplaySnapDisabled = false;
                Level.ReplayRunPastEnd = false;
            }
            return r;
        }

        static void WriteIds(Utf8JsonWriter w, string name, IEnumerable<int> ids)
        {
            w.WriteStartArray(name);
            foreach (int id in ids) w.WriteNumberValue(id);
            w.WriteEndArray();
        }

        static void WriteResult(Utf8JsonWriter w, ReplayResult r, LevelSeedData data, bool contacts)
        {
            w.WriteStartObject();
            w.WriteNumber("piece", r.Piece);
            if (r.Held != null)
            {
                w.WriteStartArray("held");
                foreach (string h in r.Held) w.WriteStringValue(h);
                w.WriteEndArray();
            }
            w.WriteBoolean("passed", r.Passed);
            if (!r.Passed) w.WriteString("failedBy", r.FailedBy);
            w.WriteBoolean("reachedRecordingEnd", r.ReachedEnd);
            w.WriteNumber("recordingEndDeviation", r.EndDeviation);
            if (r.Last)
            {
                w.WriteBoolean("reachedGoal", r.ReachedGoal);
                w.WriteNumber("ticksToGoal", r.GoalTick);
            }
            w.WriteNumber("stepsRun", r.Steps);
            w.WriteNumber("ticksCompared", r.Compared);
            w.WriteNumber("maxDeviationFromTrace", r.MaxDev);
            w.WriteNumber("firstDeviationTick", r.FirstDeviationTick);
            w.WriteNumber("snaps", r.Snaps);
            w.WriteNumber("deaths", r.Deaths);
            if (r.ResetStepDeaths > 0) w.WriteNumber("resetStepDeathsDiscounted", r.ResetStepDeaths);
            w.WriteNumber("firstDeathTick", r.FirstDeathTick);
            w.WriteString("firstDeathBy", r.FirstDeathBy);
            WriteVec(w, "finalPos", r.FinalPos);
            w.WriteStartObject("coins");
            w.WriteNumber("inPiece", r.CoinsInPiece);
            w.WriteNumber("collected", r.CoinsCollected);
            WriteIds(w, "missed", r.Missed);
            WriteIds(w, "otherPieces", r.Other);
            w.WriteEndObject();
            if (contacts)
            {
                w.WriteStartArray("contacts");
                foreach (Contact c in r.Contacts.Values.OrderBy(c => c.FirstTick).ThenBy(c => ObjIdOf(c.Obj)))
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", ObjIdOf(c.Obj));
                    w.WriteString("kind", KindName(c.Obj.GetType()));
                    int piece = PieceOf(data, c.Obj.Core.GenSeedPiece), ladder = LadderOf(data, c.Obj.Core.GenSeedPiece);
                    if (piece >= 0) w.WriteNumber("piece", piece);
                    if (ladder >= 0) w.WriteNumber("ladder", ladder);
                    if (c.Obj.Core.GateItem >= 0) w.WriteString("gate", PlatformSuppression.Items[c.Obj.Core.GateItem].ToString());
                    w.WriteNumber("firstTick", c.FirstTick);
                    w.WriteNumber("ticks", c.Ticks);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        /// <summary>An exported object's id; -1 for a runtime object (an emitted elevator platform, say).</summary>
        static int ObjIdOf(ObjectBase obj)
        {
            int id;
            return ObjIds.TryGetValue(obj.Core.MyGuid, out id) ? id : -1;
        }

        /// <summary>The ticks the computer stood on objects gated by an item in piece k (an emitted platform counts for its emitter's piece).</summary>
        static int StoodOnItem(ReplayResult r, LevelSeedData data, int item, int k)
        {
            int ticks = 0;
            foreach (Contact c in r.Contacts.Values)
                if (c.Obj.Core.GateItem == item && PieceOf(data, c.Obj.Core.GenSeedPiece) == k) ticks += c.Ticks;
            return ticks;
        }

        static void WriteV2Replays(Utf8JsonWriter w, Level level, LevelSeedData data, Args a, Stopwatch sw)
        {
            int n = level.LevelPieces.Count;
            var baseline = new ReplayResult[n];
            BobPhsx replayHero = null;
            if (a.ReplayHero != null)
            {
                replayHero = level.DefaultHeroType.Clone();
                GeneratorSettings.Apply(replayHero, a.ReplayHero.Value, "replayHero");
            }

            if (a.Replay)
            {
                w.WriteStartObject("replayCheck");
                if (replayHero != null) WriteHeroAbility(w, "replayHero", replayHero);
                w.WriteStartArray("inputsOnly");
                for (int k = 0; k < n; k++)
                {
                    baseline[k] = ReplayPiece(level, data, k, true, null, replayHero);
                    WriteResult(w, baseline[k], data, true);
                }
                w.WriteEndArray();
                if (a.Held != null)
                {
                    w.WriteStartArray("held");
                    for (int k = 0; k < n; k++)
                        WriteResult(w, ReplayPiece(level, data, k, true, a.Held, replayHero), data, true);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            LastReplayMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();

            if (a.Coins) WriteCoins(w, level, data, a.Replay ? baseline : null);

            if (a.Verify && a.Replay) WriteVerify(w, level, data, baseline, replayHero);
            LastVerifyMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Every coin with the tick at which the computer's replay of its piece collects it (null when no replay does).</summary>
        static void WriteCoins(Utf8JsonWriter w, Level level, LevelSeedData data, ReplayResult[] baseline)
        {
            int n = level.LevelPieces.Count;
            var perPiece = new int[n];
            var witnessedPerPiece = new int[n];
            var unwitnessed = new List<int>();
            int total = 0, witnessed = 0;

            w.WriteStartObject("coins");
            w.WriteStartArray("list");
            foreach (ObjectBase obj in level.Objects)
            {
                Coin coin = obj as Coin;
                int id;
                if (coin == null || coin.Core.MarkedForDeletion || !ObjIds.TryGetValue(coin.Core.MyGuid, out id)) continue;
                int piece = PieceOf(data, coin.Core.GenSeedPiece);
                total++;
                if (piece >= 0) perPiece[piece]++;

                w.WriteStartObject();
                w.WriteNumber("id", id);
                WriteVec(w, "pos", coin.Core.Data.Position);
                w.WriteNumber("piece", piece);
                int[] hit = null;
                int by = -1;
                if (baseline != null)
                {
                    // The replay of the coin's own piece first; else any piece's replay that collects it.
                    if (piece >= 0 && baseline[piece] != null && baseline[piece].Coins.TryGetValue(coin.Core.MyGuid, out hit)) by = piece;
                    else
                        for (int k = 0; k < n && hit == null; k++)
                            if (baseline[k] != null && baseline[k].Coins.TryGetValue(coin.Core.MyGuid, out hit)) by = k;
                }
                if (hit != null)
                {
                    w.WriteStartObject("witness");
                    w.WriteNumber("replayOfPiece", by);
                    w.WriteNumber("tick", hit[0]);
                    w.WriteNumber("bob", hit[1]);
                    w.WriteEndObject();
                    witnessed++;
                    if (piece >= 0) witnessedPerPiece[piece]++;
                }
                else
                {
                    w.WriteNull("witness");
                    unwitnessed.Add(id);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("count", total);
            w.WriteNumber("witnessed", witnessed);
            WriteIds(w, "unwitnessed", unwitnessed);
            w.WriteStartArray("perPiece");
            for (int k = 0; k < n; k++)
            {
                w.WriteStartObject();
                w.WriteNumber("piece", k);
                w.WriteNumber("coins", perPiece[k]);
                w.WriteNumber("witnessed", witnessedPerPiece[k]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        /// <summary>
        /// The nested-piece rule verifier. S_k = the platform items of pieces 0..k (the items the player holds to be in
        /// piece k). For each piece: the gated objects it holds outside S_k (should be none); a replay with exactly S_k
        /// held (should pass as the all-solid baseline does); and one CONTROL per item new in S_k (S_k minus that item,
        /// which the rule says the player lacks) — a control that still passes means that item does not gate the piece.
        /// The baseline's contacts say whether the computer stood on the new item in that piece.
        /// </summary>
        static void WriteVerify(Utf8JsonWriter w, Level level, LevelSeedData data, ReplayResult[] baseline, BobPhsx replayHero)
        {
            int n = level.LevelPieces.Count;
            var cumulative = new List<int>();
            int controls = 0, controlsFailed = 0, stoodPassed = 0, notStoodFailed = 0, stoodFailed = 0, notStoodPassed = 0, withSPassed = 0;

            w.WriteStartObject("verify");
            w.WriteStartArray("pieces");
            for (int k = 0; k < n; k++)
            {
                PieceSeedData seed = level.LevelPieces[k].MyData;
                var mine = ItemsOf(seed);
                var newItems = mine.Where(i => !cumulative.Contains(i)).ToList();
                cumulative.AddRange(newItems);
                var S = cumulative.Select(i => PlatformSuppression.Items[i].ToString()).ToList();

                w.WriteStartObject();
                w.WriteNumber("piece", k);
                w.WriteStartArray("S");
                foreach (string s in S) w.WriteStringValue(s);
                w.WriteEndArray();
                w.WriteStartArray("new");
                foreach (int i in newItems) w.WriteStringValue(PlatformSuppression.Items[i].ToString());
                w.WriteEndArray();

                // Structural: gated objects of this piece whose item is outside S_k.
                var stray = new SortedDictionary<string, int>();
                foreach (ObjectBase obj in level.Blocks.Cast<ObjectBase>().Concat(level.Objects))
                    if (obj != null && !obj.Core.MarkedForDeletion && obj.Core.GateItem >= 0 && PieceOf(data, obj.Core.GenSeedPiece) == k
                        && !cumulative.Contains(obj.Core.GateItem))
                    {
                        string key = PlatformSuppression.Items[obj.Core.GateItem] + ":" + obj.Core.MyType;
                        int c; stray.TryGetValue(key, out c); stray[key] = c + 1;
                    }
                w.WriteStartObject("strayGated");
                foreach (var kv in stray) w.WriteNumber(kv.Key, kv.Value);
                w.WriteEndObject();

                // Stood on per new item (baseline, this piece).
                w.WriteStartObject("stoodOnNew");
                foreach (int i in newItems) w.WriteNumber(PlatformSuppression.Items[i].ToString(), StoodOnItem(baseline[k], data, i, k));
                w.WriteEndObject();

                w.WriteBoolean("baselinePassed", baseline[k].Passed);
                w.WritePropertyName("withS");
                ReplayResult withS = ReplayPiece(level, data, k, true, S, replayHero);
                if (withS.Passed) withSPassed++;
                WriteResult(w, withS, data, false);

                w.WriteStartArray("controls");
                foreach (int drop in newItems)
                {
                    var held = cumulative.Where(i => i != drop).Select(i => PlatformSuppression.Items[i].ToString()).ToList();
                    ReplayResult c = ReplayPiece(level, data, k, true, held, replayHero);
                    bool stood = StoodOnItem(baseline[k], data, drop, k) > 0;
                    controls++;
                    if (!c.Passed) controlsFailed++;
                    if (stood && c.Passed) stoodPassed++;
                    if (stood && !c.Passed) stoodFailed++;
                    if (!stood && c.Passed) notStoodPassed++;
                    if (!stood && !c.Passed) notStoodFailed++;
                    w.WriteStartObject();
                    w.WriteString("drop", PlatformSuppression.Items[drop].ToString());
                    w.WriteBoolean("stoodOnInBaseline", stood);
                    w.WritePropertyName("result");
                    WriteResult(w, c, data, false);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("summary");
            w.WriteNumber("pieces", n);
            w.WriteNumber("withSPassed", withSPassed);
            w.WriteNumber("controls", controls);
            w.WriteNumber("controlsFailed", controlsFailed);
            w.WriteNumber("controlsPassed", controls - controlsFailed);
            w.WriteNumber("stoodAndFailed", stoodFailed);
            w.WriteNumber("stoodButPassed", stoodPassed);
            w.WriteNumber("notStoodButFailed", notStoodFailed);
            w.WriteNumber("notStoodAndPassed", notStoodPassed);
            w.WriteEndObject();
            w.WriteEndObject();
        }
    }
}
