using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using CloudberryKingdom.Bobs;
using CloudberryKingdom.Levels;
using CloudberryKingdom.Blocks;
using CoreEngine;

namespace CloudberryKingdom
{
    /// <summary>
    /// P7c: the Playback Bot crosses the elevator ladder between two pieces (LadderType.DoubleMoving: two BlockEmitter
    /// columns of moving platforms) instead of respawning at the next checkpoint.
    ///
    /// No recording crosses a ladder, and every piece's recording starts at level step 0 (each piece's StartPhsxStep), so a
    /// crossing has two parts:
    ///   1. the walk: from the end of piece k's recording, across the ladder, to standing on piece k+1's start;
    ///   2. the re-sync: the game's own reset at checkpoint k+1 (the bob is already there), so that piece k+1's recording
    ///      plays at the step it was recorded at.
    ///
    /// The walk is PLANNED with the engine's own physics. The ladder is deterministic: each platform's position is a
    /// function of the level step (MovingPlatform: StartData + (step - Offset) * velocity), the emitters emit every 100
    /// steps (AlwaysOn), and the bob at rest at the piece's end is the same bob whatever step it is. So a plan made at one
    /// step holds at any step with the same phase (step mod LadderPeriod). The plan is made by a REHEARSAL on the played
    /// level itself: settle the bob at the end of piece k, then try reactive policies from that pose (each try starts at the
    /// step it happens to start at, so the tries cover many phases), keeping the ones that stand on piece k+1. Then the
    /// level is put back exactly as it was by the game's reset at checkpoint k and a silent replay of piece k's recording
    /// (deterministic), and the live bob waits at the pose for a planned phase and plays that policy. A live walk that does
    /// not go as rehearsed (a death, a fall, a timeout) FALLS BACK to the P7b respawn at checkpoint k+1 and its note.
    /// </summary>
    public static partial class EmbedMode
    {
        /// <summary>The ladder emitters' period (BlockEmitter.Delay of LadderType.DoubleMoving): the ladder's phase.</summary>
        public const int LadderPeriod = 100;
        /// <summary>Settling: the bob counts as at rest after this many steps on the ground with |vx| below LadderRestSpeed.</summary>
        public const int LadderRestTicks = 3;
        public const float LadderRestSpeed = .5f;
        /// <summary>Settling gives up after this many steps (the bob is not coming to rest at the piece's end).</summary>
        public const int LadderSettleMax = 240;
        /// <summary>After a teleport to the pose, no input for this many steps before a policy may start.</summary>
        public const int LadderPreTicks = 2;
        /// <summary>The live bob is moved to the rehearsed pose only when it settled this close to it (else: fall back).</summary>
        public const float LadderPoseTolerance = 40;
        /// <summary>A try fails after this many steps without standing on the next piece.</summary>
        public const int LadderTryMax = 420;
        /// <summary>A try has fallen when the bob is this far below the lower of the pose and the target.</summary>
        public const float LadderFallDepth = 700;
        /// <summary>Standing on piece k+1 = on the ground this many steps, at or past (target.X - LadderReachX), within LadderReachY.</summary>
        public const int LadderStandTicks = 2;
        public const float LadderReachY = 450;
        /// <summary>Across: on a block (not a ladder platform) this far right of the left column's centre (its platforms are
        /// 240 px wide; piece k ends left of it, so a block there is piece k+1's, which can reach under the right column).</summary>
        public const float LadderPastLeft = 150;
        /// <summary>A try has overshot when the bob rides a rising platform this far above the target.</summary>
        public const float LadderOvershoot = 700;
        /// <summary>After crossing, the bob walks to the recording's start (within LadderArrive px) for at most LadderArriveMax steps.</summary>
        public const float LadderArrive = 12;
        public const int LadderArriveMax = 90;
        /// <summary>The re-sync picks the recording's nearest standing tick among its first this many after TargetTick.</summary>
        public const int LadderResyncTicks = 120;
        /// <summary>⚖ Robert 2026-11-07: a re-sync that would move the bob more than this falls back to the respawn + note.</summary>
        public const float LadderResyncCap = 150;
        /// <summary>Default rehearsal budget: at most this many tries; stop once this many have crossed.</summary>
        public const int LadderDefaultBudget = 144, LadderDefaultKeep = 3;
        /// <summary>The rehearsal's tries stop after this many level steps.</summary>
        public const int LadderStepBudget = 6000;
        /// <summary>options.ladderSteps: the step budgets multiplied (Phase A headroom measurement).</summary>
        static float LadderStepScale = 1;
        /// <summary>At most this many planned crossings are validated (each one a replay of piece k and the live walk).</summary>
        public const int LadderValidations = 3;
        /// <summary>Exact tries (no rest at the recording's end) stop after this many level steps.</summary>
        public const int LadderExactBudget = 8000;
        /// <summary>Exact tries launch this many steps after the recording's end (cycled with the policies).</summary>
        static readonly int[] LadderExactDelays = { 1, 8, 20, 40 };

        /// <summary>
        /// A crossing policy. Kind 'aim' (P7c): hop by hop, reading the ladder's platforms each step (their motion is a
        /// function of the step, so a rehearsed walk replays exactly): onto the next column when one of its platforms is
        /// Lo..Hi px above the bob (centres), holding jump HoldC steps, right in the air DutyC of every 4; onto the next
        /// piece once the right column (which descends) has brought the bob down to M px above the target (at once if it
        /// is already lower), holding HoldT, DutyT; the right column only once the bob is MinRight px above the target.
        /// Kind 'timer' (P5b's family): on landing wait Wait steps, hold jump Hold steps; Pause = no direction while
        /// waiting on the ground; in the air, right on AirDuty of every 4 steps.
        /// </summary>
        class LadderPolicy
        {
            public bool Aim;
            public int Wait, Hold, AirDuty;
            public bool Pause;
            public float Lo, Hi, MinRight, M;
            public int HoldC, DutyC, HoldT, DutyT;
            public override string ToString()
            {
                return Aim ? "aim " + Lo + ".." + Hi + " h" + HoldC + "d" + DutyC + " r" + MinRight + " t" + M + " h" + HoldT + "d" + DutyT
                           : "timer " + Wait + "/" + Hold + "/" + (Pause ? "p" : "-") + "/" + AirDuty;
            }
        }

        static readonly LadderPolicy[] LadderPolicies = MakeLadderPolicies();

        static LadderPolicy[] MakeLadderPolicies()
        {
            var aim = new List<LadderPolicy>();
            float[][] windows = { new float[] { -250, -50 }, new float[] { -150, 50 }, new float[] { -50, 150 }, new float[] { 50, 250 } };
            foreach (var win in windows)
                foreach (int hc in new[] { 1, 8, 20 })
                    foreach (int dc in new[] { 4, 2 })
                        foreach (float mr in new[] { -100f, 0, 100, 250 })
                            foreach (int ht in new[] { 8, 20, 40 })
                                foreach (int dt in new[] { 4, 2 })
                                    foreach (float m in new[] { -100f, 50, 200, 400 })
                                        aim.Add(new LadderPolicy { Aim = true, Lo = win[0], Hi = win[1], HoldC = hc, DutyC = dc, MinRight = mr, HoldT = ht, DutyT = dt, M = m });
            var timer = new List<LadderPolicy>();
            int[] waits = { 0, 3, 8, 15, 25, 35, 50, 70, 90 }, holds = { 4, 10, 20, 40 };
            foreach (int d in new[] { 4, 2 })
                foreach (bool p in new[] { false, true })
                    foreach (int h in holds)
                        foreach (int w in waits)
                            timer.Add(new LadderPolicy { Wait = w, Hold = h, Pause = p, AirDuty = d });
            // A fixed shuffle (seeded) of each family, so that a small budget already samples every kind; the timer family
            // (P5b's 144) after the aim family's.
            var rng = new Random(7);
            return aim.OrderBy(x => rng.Next()).Concat(timer.OrderBy(x => rng.Next())).ToArray();
        }

        /// <summary>One try: launched at Key (see LadderPlan), with policy Policy; Steps from the launch to its end.</summary>
        class LadderTry { public int Key, Policy, Steps; public string Result; public bool Invalid; }

        /// <summary>
        /// The bob at rest at the end of piece k's recording stands on a SUPPORT. Either a static block (the launch is keyed
        /// by the ladder's phase, step mod LadderPeriod), or a platform of a ladder emitter (the "piece-end elevator": the
        /// recordings mostly end on the left column's rising platform). A carried bob's launch is keyed by how far its
        /// platform has risen since the rehearsal's own rest (in steps): the phase of the whole ladder is locked to it, since
        /// both columns emit on the same step every LadderPeriod steps.
        /// </summary>
        class LadderPlan
        {
            public int Piece, TargetTick;
            public Vector2 Pose, Target, RestVelocity, RestCamera;
            public BlockEmitter Emitter;   // null: a static support
            public Vector2 EmitterPos;     // the emitter's identity (a reset makes the level's objects anew)
            public bool Across;            // the recording already ends across the ladder: only the walk to the target
            public bool Exact;             // tries from the recording's end (no rest): Key = steps after it
            public float LeftX = float.NaN, RightX = float.NaN;  // the ladder's two emitter columns (x)
            public Vector2 Offset;         // carried: the bob's position relative to its platform at rest
            public float PlatformY0, Rise; // carried: the platform's y at the rehearsal's rest; px per step
            public string Abilities;
            public string Refusal;   // non-null: no plan (why)
            public readonly List<LadderTry> Tries = new List<LadderTry>();
            public readonly List<LadderTry> Crossings = new List<LadderTry>();
            public int SettleSteps, Steps, ReplaySteps, ValidatedSteps;
            public double Ms;
            public LadderTry Validated;
            public readonly List<string> ValidationLog = new List<string>();
        }

        enum CrossStage { Settle, Wait, Walk, Arrive }

        class Crossing
        {
            public CrossStage Stage;
            public LadderPlan Plan;
            public LadderTry Chosen;
            public MovingPlatform Platform;
            public int Steps, StageSteps, Rest, Stand, ResyncTick;
            public Vector2 Goal;
            public float ResyncDistance;
        }

        /// <summary>'cross' (P7c, the default) or 'respawn' (P7b: the respawn at checkpoint k+1 and its note; options.ladder).</summary>
        static string LadderMode = "cross";
        static int LadderBudget = LadderDefaultBudget, LadderKeep = LadderDefaultKeep;
        /// <summary>Plans by "piece:abilities" (PlanLadders at configure; a missing one is made when the ladder is reached).</summary>
        static readonly Dictionary<string, LadderPlan> LadderPlans = new Dictionary<string, LadderPlan>();
        static double LadderPlanMs;
        static Crossing Cross;
        static bool Rehearsing;
        static int StatCrossed, StatFellBack, StatRehearsals;
        static readonly List<string> LadderLog = new List<string>();

        static string AbilityKey() { return (JetpackHeld ? "J" : "") + (DoubleJumpHeld ? "D" : ""); }

        /// <summary>The first tick at which piece k+1's recording stands (it starts at the spawn point, in the air, and
        /// falls onto the piece's start); its position is the crossing's target.</summary>
        static int LadderTargetTick(Level level, int k)
        {
            LevelPiece next = level.LevelPieces[k + 1];
            ComputerRecording rec = next.Recording[0];
            int n = Math.Min(next.PieceLength, rec.AutoOnGround.Length);
            for (int i = 0; i < n; i++) if (rec.AutoOnGround[i]) return i;
            return 0;
        }

        static Vector2 LadderTarget(Level level, int k)
        {
            return level.LevelPieces[k + 1].Recording[0].AutoLocs[LadderTargetTick(level, k)];
        }

        // ---------------------------------------------------------------------------------------------------------------
        // The rehearsal

        static void ZeroInput(Bob b)
        {
            b.CompControl = false;
            b.MyRecord = null;
            b.CodeControl = true;
            b.ControlFunc = count => { b.CurInput.A_Button = false; b.CurInput.xVec = Vector2.Zero; };
        }

        static Action<int> PolicyControl(Level level, Bob b, LadderPlan plan, LadderPolicy pol)
        {
            if (pol.Aim) return AimControl(level, b, plan, pol);
            int ground = 0, holdLeft = 0, air = 0;
            return count =>
            {
                bool on = b.MyPhsx.OnGround;
                ground = on ? ground + 1 : 0;
                if (on && holdLeft == 0 && ground == pol.Wait + 1) holdLeft = Math.Max(1, pol.Hold);
                b.CurInput.A_Button = holdLeft > 0;
                if (holdLeft > 0) holdLeft--;
                bool waiting = on && ground <= pol.Wait;
                air = on ? 0 : air + 1;
                bool right = on ? !(pol.Pause && waiting) : (air % 4) < pol.AirDuty;
                b.CurInput.xVec = new Vector2(right ? 1 : 0, 0);
            };
        }

        /// <summary>Which column the bob stands on: 0 = before the ladder (piece k), 1 = left, 2 = right, 3 = past it.</summary>
        static int ColumnOf(Bob b, LadderPlan plan)
        {
            MovingPlatform mp = b.MyPhsx.ObjectLandedOn as MovingPlatform;
            if (b.MyPhsx.OnGround && mp != null && mp.Parent != null)
            {
                float x = mp.Parent.EmitData.Position.X;
                if (Math.Abs(x - plan.LeftX) < 1) return 1;
                if (Math.Abs(x - plan.RightX) < 1) return 2;
            }
            float bx = b.Core.Data.Position.X;
            return bx < plan.LeftX ? 0 : bx >= plan.LeftX + LadderPastLeft ? 3 : -1;
        }

        /// <summary>The platform of column x nearest to dy in Lo..Hi above the bob (centres), or NaN.</summary>
        static float PlatformInWindow(Level level, Bob b, float columnX, float lo, float hi)
        {
            float best = float.NaN, by = b.Core.Data.Position.Y;
            foreach (BlockBase block in level.Blocks)
            {
                MovingPlatform mp = block as MovingPlatform;
                if (mp == null || mp.Parent == null || !mp.Active || mp.Core.MarkedForDeletion) continue;
                if (Math.Abs(mp.Parent.EmitData.Position.X - columnX) >= 1) continue;
                float dy = mp.Core.Data.Position.Y - by;
                if (dy >= lo && dy <= hi && (float.IsNaN(best) || Math.Abs(dy - (lo + hi) / 2) < Math.Abs(best - (lo + hi) / 2))) best = dy;
            }
            return best;
        }

        static Action<int> AimControl(Level level, Bob b, LadderPlan plan, LadderPolicy pol)
        {
            int holdLeft = 0, air = 0, duty = 4, hop = -1;
            return count =>
            {
                bool on = b.MyPhsx.OnGround;
                air = on ? 0 : air + 1;
                b.CurInput.A_Button = false;
                b.CurInput.xVec = Vector2.Zero;
                if (holdLeft > 0)
                {
                    // The jump in progress: hold it, right.
                    holdLeft--;
                    b.CurInput.A_Button = true;
                    b.CurInput.xVec = new Vector2(1, 0);
                    return;
                }
                if (!on)
                {
                    if (hop >= 0) b.CurInput.xVec = new Vector2((air % 4) < duty ? 1 : 0, 0);
                    return;
                }
                hop = -1;
                int col = ColumnOf(b, plan);
                float y = b.Core.Data.Position.Y;
                bool go = false;
                if (col == 0 || col == 1)
                {
                    float colX = col == 0 ? plan.LeftX : plan.RightX;
                    bool high = col == 0 || y >= plan.Target.Y + pol.MinRight;
                    go = high && !float.IsNaN(PlatformInWindow(level, b, colX, pol.Lo, pol.Hi));
                    if (go) { holdLeft = pol.HoldC; duty = pol.DutyC; }
                }
                else if (col == 2)
                {
                    // The right column descends: go once the bob is down to M px above the target (at once if already lower).
                    go = y - plan.Target.Y <= pol.M;
                    if (go) { holdLeft = pol.HoldT; duty = pol.DutyT; }
                }
                if (go)
                {
                    hop = col;
                    holdLeft = Math.Max(1, holdLeft);
                    holdLeft--;
                    b.CurInput.A_Button = true;
                    b.CurInput.xVec = new Vector2(1, 0);
                }
            };
        }

        /// <summary>One level step as the played level takes it, with the held abilities applied (the embed's Step does
        /// that in play; the rehearsal steps the level itself).</summary>
        static void RehearsalStep(Level level)
        {
            foreach (Bob bob in level.Bobs) ApplyAbilities(bob.MyPhsx);
            level.PhsxStep(true);
        }

        static bool AtRest(Bob b, ref int rest)
        {
            bool still = b.MyPhsx.OnGround && !b.Dying && !b.Dead;
            if (still)
            {
                // At rest on its support: no x motion, and (carried) moving with the platform.
                MovingPlatform mp = b.MyPhsx.ObjectLandedOn as MovingPlatform;
                float vx = b.Core.Data.Velocity.X - (mp != null ? mp.BlockCore.Data.Velocity.X : 0);
                still = Math.Abs(vx) < LadderRestSpeed;
            }
            rest = still ? rest + 1 : 0;
            return rest >= LadderRestTicks;
        }

        /// <summary>The level reset at piece k's checkpoint and its recording replayed to its end (the ride's own start):
        /// returns the steps taken, or -1 when the replay died.</summary>
        static int ReplayPieceSilently(Level level, int k)
        {
            ReleaseBobs(level);
            level.SetCurrentPiece(k);
            level.ResetAll(false);
            foreach (Bob bob in level.Bobs)
            {
                bob.CompControl = true;
                bob.MyRecord = level.CurPiece.Recording[bob.MyPieceIndex];
                bob.IndexOffset = 0;
            }
            Level.ReplayProbe = true;
            Level.ReplayDeaths = 0;
            Level.ReplaySnapDisabled = Riding == null || !Riding.Snap;
            int steps = 0;
            while (RecTick(level) < level.CurPiece.PieceLength && steps < level.CurPiece.PieceLength + 10)
            {
                RehearsalStep(level);
                steps++;
                if (Level.ReplayDeaths > 0) return -1;
            }
            return steps;
        }

        /// <summary>The settle controller after piece k's recording: while the bob is in the air (a recording can end
        /// mid-jump), what the engine feeds a computer bob past its recording's end (Bob.GetRecordedInput: right + jump);
        /// once it has landed, no input.</summary>
        static void SettleControl(Level level, Bob b, int k)
        {
            bool landed = false;
            b.CompControl = false;
            b.MyRecord = null;
            b.CodeControl = true;
            b.ControlFunc = count =>
            {
                landed |= b.MyPhsx.OnGround;
                b.CurInput.A_Button = !landed;
                b.CurInput.xVec = landed ? Vector2.Zero : new Vector2(1, 0);
            };
        }

        /// <summary>Settles the bob at the end of the replay; returns steps, or -1 (died / never at rest).</summary>
        static int Settle(Level level, Bob b, int k)
        {
            SettleControl(level, b, k);
            Level.ReplayCodeProbe = true;
            int rest = 0;
            for (int s = 1; s <= LadderSettleMax; s++)
            {
                RehearsalStep(level);
                if (Level.ReplayDeaths > 0) return -1;
                if (AtRest(b, ref rest)) return s;
            }
            return -1;
        }

        static void Teleport(Bob b, Vector2 pos, Vector2 vel)
        {
            b.Move(pos - b.Core.Data.Position);
            b.Core.Data.Velocity = vel;
        }

        /// <summary>The ladder emitter that made the platform the bob stands on (null: not a ladder platform).</summary>
        static BlockEmitter EmitterOf(Bob b)
        {
            if (!b.MyPhsx.OnGround) return null;
            MovingPlatform mp = b.MyPhsx.ObjectLandedOn as MovingPlatform;
            return mp != null && mp.Active && !mp.Core.MarkedForDeletion ? mp.Parent : null;
        }

        /// <summary>The ladder's two emitter columns: the BlockEmitters within LadderColumnsReach left of the target (the
        /// ladder is 850 px wide, its columns at 250 and 600; the target is a little right of piece k+1's start).</summary>
        const float LadderColumnsReach = 1100;
        static void FindColumns(Level level, LadderPlan plan)
        {
            var xs = new List<float>();
            foreach (ObjectBase obj in level.Objects)
            {
                BlockEmitter e = obj as BlockEmitter;
                if (e == null || e.Core.MarkedForDeletion) continue;
                float x = e.EmitData.Position.X;
                if (x > plan.Target.X - LadderColumnsReach && x < plan.Target.X && !xs.Any(v => Math.Abs(v - x) < 1)) xs.Add(x);
            }
            xs.Sort();
            if (xs.Count >= 2) { plan.LeftX = xs[0]; plan.RightX = xs[xs.Count - 1]; }
        }

        static bool SameEmitter(BlockEmitter e, LadderPlan plan)
        {
            return e != null && plan.Emitter != null && (e.EmitData.Position - plan.EmitterPos).Length() < 1;
        }

        /// <summary>A carried bob's key: steps its platform has risen since the rehearsal's rest (one platform of the
        /// emitter reached the rest height every LadderPeriod steps, so any platform of the emitter is measured alike).</summary>
        static int CarriedKey(LadderPlan plan, MovingPlatform mp)
        {
            return (int)Math.Round((mp.Core.Data.Position.Y - plan.PlatformY0) / plan.Rise);
        }

        /// <summary>A rehearsal try's start: the bob at rest at the pose (static), or riding the emitter's platform that is
        /// now between 0 and LadderPeriod steps past the rest height, at the rest's offset (carried).</summary>
        static bool PlaceForTry(Level level, Bob b, LadderPlan plan)
        {
            // The camera as it was at the rest (it follows the bob: left where the last try ended, it would leave the bob
            // behind, off screen); the live bob waits in view of it.
            level.MainCamera.Data.Position = level.MainCamera.Target = plan.RestCamera;
            level.MainCamera.Data.Velocity = Vector2.Zero;
            level.MainCamera.Update();
            if (plan.Emitter == null) { Teleport(b, plan.Pose, plan.RestVelocity); return true; }
            MovingPlatform best = null;
            foreach (BlockBase block in level.Blocks)
            {
                MovingPlatform mp = block as MovingPlatform;
                if (mp == null || !SameEmitter(mp.Parent, plan) || !mp.Active || mp.Core.MarkedForDeletion) continue;
                int key = CarriedKey(plan, mp);
                if (key < 0 || key >= LadderPeriod) continue;
                best = mp;
            }
            if (best == null) return false;
            Teleport(b, best.Core.Data.Position + plan.Offset, plan.RestVelocity);
            return true;
        }

        /// <summary>The bob's launch key now (static: the phase; carried: its platform's rise), or int.MinValue when it is
        /// not on the plan's kind of support.</summary>
        static int LaunchKey(Level level, Bob b, LadderPlan plan)
        {
            if (plan.Emitter == null) return Phase(level);
            if (!SameEmitter(EmitterOf(b), plan)) return int.MinValue;
            return CarriedKey(plan, (MovingPlatform)b.MyPhsx.ObjectLandedOn);
        }

        /// <summary>
        /// The rehearsal of ladder k on the played level; the level is left reset at checkpoint k with piece k's recording
        /// replayed to its end, ready for the live walk.
        /// </summary>
        static void SetLadderOption<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            LadderPlans.Clear();
        }

        static string PlanKey(int k) { return k + ":" + AbilityKey(); }

        /// <summary>
        /// Plans every ladder of the played level at configure (the page calls it after the items), so no plan is made
        /// mid-play: for the held abilities, and on a Jetpack / Double Jump level also for the other state of that ability
        /// (an item arriving later finds its plan made). The level is left as Play left it (reset at the start).
        /// Returns {ok, ladders, plans, crossable, ms}.
        /// </summary>
        public static string PlanLadders()
        {
            try
            {
                Level level = PlayLevel;
                if (level == null || Tools.CurLevel != level) throw new InvalidOperationException("no level is being played");
                var sw = Stopwatch.StartNew();
                int ladders = Math.Max(0, level.LevelPieces.Count - 1), made = 0, crossable = 0;
                if (LadderMode == "cross" && ladders > 0)
                {
                    bool holdJ = JetpackHeld, holdD = DoubleJumpHeld;
                    var states = new List<bool[]> { new[] { holdJ, holdD } };
                    if (LevelHeroJetpack) states.Add(new[] { !holdJ, holdD });
                    if (LevelHeroJumps > 1) states.Add(new[] { holdJ, !holdD });
                    try
                    {
                        foreach (var st in states)
                        {
                            JetpackHeld = st[0];
                            DoubleJumpHeld = st[1];
                            for (int k = 0; k < ladders; k++)
                            {
                                if (LadderPlans.ContainsKey(PlanKey(k))) continue;
                                LadderPlan plan = Rehearse(level, k, false);
                                LadderPlans[PlanKey(k)] = plan;
                                LadderLog.Add(PlanJson(plan, "configure"));
                                made++;
                                if (plan.Validated != null) crossable++;
                            }
                        }
                    }
                    finally
                    {
                        JetpackHeld = holdJ;
                        DoubleJumpHeld = holdD;
                        // Back to the level's start, as Play left it.
                        ReleaseBobs(level);
                        level.SetCurrentPiece(0);
                        level.ResetAll(false, true);
                        ApplyHeld();
                        Events.Clear();
                    }
                }
                LadderPlanMs = sw.Elapsed.TotalMilliseconds;
                return "{\"ok\":true,\"ladders\":" + ladders + ",\"plans\":" + made + ",\"crossable\":" + crossable
                       + ",\"ms\":" + LadderPlanMs.ToString("0.0", CultureInfo.InvariantCulture) + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.GetType().Name + ": " + e.Message) + "}";
            }
        }

        static LadderPlan Rehearse(Level level, int k, bool restore)
        {
            var sw = Stopwatch.StartNew();
            var plan = new LadderPlan { Piece = k, Target = LadderTarget(level, k), TargetTick = LadderTargetTick(level, k), Abilities = AbilityKey() };
            bool holdSuppress = level.SuppressCheckpoints, holdSounds = CoreSoundWad.SuppressSounds;
            int holdTimeLimit = level.TimeLimit;
            CoreSoundWad.SuppressSounds = true;
            Rehearsing = true;
            StatRehearsals++;
            try
            {
                level.SuppressCheckpoints = true;
                // The tries run the level's step count far past a piece's length: no time-limit death in the rehearsal.
                level.TimeLimit = 0;
                FindColumns(level, plan);
                if (float.IsNaN(plan.LeftX)) { plan.Refusal = "no ladder columns between piece " + k + " and piece " + (k + 1); return plan; }
                int rs = ReplayPieceSilently(level, k);
                if (rs < 0) { plan.Refusal = "piece " + k + "'s recording died in the rehearsal"; return plan; }
                plan.ReplaySteps += rs;
                plan.Steps = rs;
                Bob b = level.Bobs[0];
                int settle = Settle(level, b, k);
                plan.Steps += Math.Max(0, settle);
                Debug("replayed " + rs + ", settle " + settle + ": pos " + b.Core.Data.Position + " on " + Describe(b.MyPhsx.ObjectLandedOn) + " columns " + plan.LeftX + "/" + plan.RightX + " target " + plan.Target);
                if (settle >= 0)
                {
                    plan.SettleSteps = settle;
                    plan.Pose = b.Core.Data.Position;
                    plan.RestVelocity = b.Core.Data.Velocity;
                    plan.RestCamera = level.MainCamera.Data.Position;
                    plan.Emitter = EmitterOf(b);
                    if (plan.Emitter != null)
                    {
                        MovingPlatform mp = (MovingPlatform)b.MyPhsx.ObjectLandedOn;
                        plan.Offset = plan.Pose - mp.Core.Data.Position;
                        plan.PlatformY0 = mp.Core.Data.Position.Y;
                        plan.Rise = plan.Emitter.EmitData.Velocity.Y;
                        plan.EmitterPos = plan.Emitter.EmitData.Position;
                        if (Math.Abs(plan.Rise) < .01f) plan.Emitter = null;
                    }
                    int stand = LadderStandTicks;
                    if (WalkOutcome(b, plan, Floor(plan), ref stand) == "crossed")
                    {
                        // The recording already ends across the ladder: nothing to hop, only the walk to the target.
                        plan.Across = true;
                        var t = new LadderTry { Policy = -1, Result = "crossed" };
                        plan.Tries.Add(t);
                        plan.Crossings.Add(t);
                    }
                    else RestTries(level, b, plan);
                }
                if (plan.Crossings.Count == 0)
                {
                    // No rest at the end of piece k (the recording ends mid-jump or on a moving block), or no rested try
                    // crossed: exact tries, each from the replayed recording's end, launched Key steps after it.
                    plan.Exact = true;
                    plan.Emitter = null;
                    ExactTries(level, k, plan);
                }
                if (plan.Crossings.Count == 0) { plan.Refusal = "no policy crossed in " + plan.Tries.Count + " tries"; return plan; }
                plan.Steps += ValidateCrossings(level, k, plan);
                if (plan.Validated == null) plan.Refusal = "no rehearsed crossing held when walked as the live bob will (" + string.Join("; ", plan.ValidationLog) + ")";
            }
            finally
            {
                // Mid-play: put the level back exactly, reset at checkpoint k and piece k replayed (the live bob continues
                // from there). At configure, PlanLadders resets the level to its start afterwards.
                level.SuppressCheckpoints = holdSuppress;
                level.TimeLimit = holdTimeLimit;
                if (restore)
                {
                    int rs = ReplayPieceSilently(level, k);
                    if (rs >= 0) { plan.ReplaySteps += rs; plan.Steps += rs; }
                }
                Level.ReplayCodeProbe = false;
                Rehearsing = false;
                CoreSoundWad.SuppressSounds = holdSounds;
                plan.Ms = sw.Elapsed.TotalMilliseconds;
            }
            return plan;
        }

        static void Debug(string s)
        {
#if !BROWSER
            if (Environment.GetEnvironmentVariable("CK_LADDER_DEBUG") == "1") Console.WriteLine("[ladder] " + s);
#endif
        }

        static string Describe(ObjectBase o) { return o == null ? "null" : o.GetType().Name + " " + o.Core.Data.Position; }

        /// <summary>Tries from the rest pose, teleported back each time (cheap: no replay); see LadderPlan.</summary>
        static void RestTries(Level level, Bob b, LadderPlan plan)
        {
            int start = plan.Steps;
            for (int i = 0; i < LadderPolicies.Length && i < LadderBudget && plan.Crossings.Count < LadderKeep && plan.Steps - start < LadderStepBudget * LadderStepScale; i++)
            {
                ZeroInput(b);
                // Wait (no input) for a start: a carried bob needs its emitter's next platform in range.
                int guard = 0;
                while (!PlaceForTry(level, b, plan) && guard++ < LadderPeriod) { RehearsalStep(level); plan.Steps++; }
                Level.ReplayDeaths = 0;
                int rest = 0, pre = 0;
                while (pre < LadderPreTicks || !AtRest(b, ref rest))
                {
                    RehearsalStep(level); plan.Steps++; pre++;
                    if (pre > LadderPreTicks) AtRest(b, ref rest);
                    if (pre > 20) break;
                }
                var t = new LadderTry { Policy = i, Key = LaunchKey(level, b, plan) };
                if (Level.ReplayDeaths > 0 || t.Key == int.MinValue || pre > 20) { t.Result = "noStart"; plan.Tries.Add(t); continue; }
                b.ControlFunc = PolicyControl(level, b, plan, LadderPolicies[i]);
                RunTry(level, b, plan, t);
            }
        }

        /// <summary>Exact tries: for each policy and launch delay, piece k replayed from its checkpoint, the settle
        /// controller for Key steps, then the policy.</summary>
        static void ExactTries(Level level, int k, LadderPlan plan)
        {
            int start = plan.Steps;
            for (int i = 0; i < LadderPolicies.Length && plan.Crossings.Count < LadderKeep && plan.Steps - start < LadderExactBudget * LadderStepScale; i++)
            {
                int key = LadderExactDelays[i % LadderExactDelays.Length];
                int rs = ReplayPieceSilently(level, k);
                plan.Steps += Math.Max(0, rs);
                if (rs < 0) return;
                Bob b = level.Bobs[0];
                SettleControl(level, b, k);
                Level.ReplayCodeProbe = true;
                for (int s = 0; s < key && Level.ReplayDeaths == 0; s++) { RehearsalStep(level); plan.Steps++; }
                var t = new LadderTry { Policy = i, Key = key };
                if (Level.ReplayDeaths > 0) { t.Result = "noStart"; plan.Tries.Add(t); continue; }
                b.ControlFunc = PolicyControl(level, b, plan, LadderPolicies[i]);
                RunTry(level, b, plan, t);
            }
        }

        static void RunTry(Level level, Bob b, LadderPlan plan, LadderTry t)
        {
            int stand = 0;
            float floor = Floor(plan);
            for (t.Steps = 1; t.Steps <= LadderTryMax; t.Steps++)
            {
                RehearsalStep(level);
                plan.Steps++;
                string r = WalkOutcome(b, plan, floor, ref stand);
                if (r != null) { t.Result = r; break; }
            }
            if (t.Result == null) t.Result = "timeout";
            if (t.Result == "crossed" && !ResyncReachable(level, b, plan)) t.Result = "far";
            if (t.Result != "crossed")
                Debug("try " + t.Policy + " " + LadderPolicies[t.Policy] + " key " + t.Key + " -> " + t.Result + " at " + t.Steps + " pos " + b.Core.Data.Position + " on " + Describe(b.MyPhsx.ObjectLandedOn) + " death " + Level.ReplayFirstDeathKind);
            plan.Tries.Add(t);
            if (t.Result == "crossed") plan.Crossings.Add(t);
        }

        static int Phase(Level level) { return CoreMath.Modulo(level.CurPhsxStep, LadderPeriod); }

        /// <summary>null while the walk goes on; "crossed", "died", "fell" or "overshot" (riding a column far above the
        /// target). Crossed = standing on a block (not a ladder platform) past the left column, level with the target.</summary>
        static string WalkOutcome(Bob b, LadderPlan plan, float floor, ref int stand)
        {
            if (Level.ReplayDeaths > 0 || b.Dying || b.Dead) return "died";
            Vector2 p = b.Core.Data.Position;
            if (p.Y < floor) return "fell";
            if (p.Y > Math.Max(plan.Pose.Y, plan.Target.Y) + LadderOvershoot && b.MyPhsx.OnGround && b.MyPhsx.ObjectLandedOn is MovingPlatform && ((MovingPlatform)b.MyPhsx.ObjectLandedOn).BlockCore.Data.Velocity.Y > 0) return "overshot";
            bool there = b.MyPhsx.OnGround && !(b.MyPhsx.ObjectLandedOn is MovingPlatform)
                         && p.X >= plan.LeftX + LadderPastLeft && Math.Abs(p.Y - plan.Target.Y) < LadderReachY;
            stand = there ? stand + 1 : 0;
            return stand >= LadderStandTicks ? "crossed" : null;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // The live walk

        /// <summary>The ride's ladder (the end of piece k's recording, the goal in a later piece): walk it, or respawn.</summary>
        static void Ladder(Level level, Ride r)
        {
            int from = r.Piece, to = r.Piece + 1;
            if (LadderMode != "cross" || Rehearsing)
            {
                LadderRespawn(level, r, null);
                return;
            }
            LadderPlan plan;
            if (!LadderPlans.TryGetValue(PlanKey(from), out plan))
            {
                // Not planned at configure (the held abilities changed since): plan now (one frame's hitch).
                plan = Rehearse(level, from, true);
                LadderPlans[PlanKey(from)] = plan;
                LadderLog.Add(PlanJson(plan, "late"));
            }
            if (plan.Refusal != null) { LadderRespawn(level, r, plan.Refusal); return; }
            // The rehearsal left the bob at the end of piece k's recording (or the level was not touched): settle, then
            // wait for a planned phase at the pose.
            ReleaseBobs(level);
            Bob b = level.Bobs[0];
            SettleControl(level, b, from);
            Level.ReplayProbe = true;
            Level.ReplayCodeProbe = true;
            Level.ReplayDeaths = 0;
            Cross = new Crossing { Stage = CrossStage.Settle, Plan = plan };
            r.State = RideState.Crossing;
            Events.Add("{\"kind\":\"botNote\",\"note\":\"ladderWalk\",\"piece\":" + from + ",\"fromPiece\":" + from + ",\"toPiece\":" + to
                       + ",\"goal\":" + GoalJson() + ",\"text\":" + GeneratorApi.JsonStr("ladder: walking across to " + CheckpointName(to)) + "}");
        }

        static string CheckpointName(int piece) { return piece <= 0 ? "the start" : "checkpoint " + piece; }

        static string GoalJson()
        {
            BotWalk walk = Bot;
            if (walk == null) return "null";
            return "{\"kind\":" + GeneratorApi.JsonStr(walk.Kind) + ",\"id\":" + GeneratorApi.JsonStr(walk.Id) + "}";
        }

        /// <summary>The P7b ladder: the respawn at checkpoint k+1 and its note (P7c: also the fallback, with why).</summary>
        static void LadderRespawn(Level level, Ride r, string why)
        {
            int from = r.Piece, to = r.Piece + 1;
            Cross = null;
            Level.ReplayCodeProbe = false;
            string fields = ",\"fromPiece\":" + from + ",\"toPiece\":" + to;
            if (LadderMode == "cross")
            {
                StatFellBack++;
                fields += ",\"crossing\":\"fellBack\",\"why\":" + GeneratorApi.JsonStr(why ?? "");
            }
            RespawnRide(level, to, "ladder", fields);
#if !BROWSER
            Debug("fell back: " + why);
#endif
        }

        /// <summary>Every step while the ride is Crossing (after the level's step, as the embed's Step runs).</summary>
        static void StepCrossing(Level level, Ride r)
        {
            Crossing c = Cross;
            if (c == null) { LadderRespawn(level, r, "no crossing"); return; }
            if (level.SetToReset || level.Bobs.Count == 0) { LadderRespawn(level, r, "the level reset"); return; }
            string res = CrossTick(level, level.Bobs[0], c);
            if (res == null) return;
            if (res != "arrived") { LadderRespawn(level, r, res); return; }
            // Standing at piece k+1's recording start: the re-sync. The game's reset at checkpoint k+1 (every recording plays
            // at the step it was recorded at, and each piece's starts at 0), then piece k+1's recording fast-forwarded to the
            // tick at which it first stands (TargetTick): the bob is back where it stood, and plays on from there.
            Bob b = level.Bobs[0];
            Vector2 stood = b.Core.Data.Position;
            int from = r.Piece, to = r.Piece + 1;
            StatCrossed++;
            Cross = null;
            Level.ReplayCodeProbe = false;
            ReleaseBobs(level);
            level.SetCurrentPiece(to);
            level.ResetAll(false);
            var ride = Riding = new Ride { State = RideState.AwaitRespawn, Piece = to, Snap = r.Snap };
            StartRecording(level, ride);
            bool holdSounds = CoreSoundWad.SuppressSounds;
            CoreSoundWad.SuppressSounds = true;
            try { for (int s = 0; s < c.ResyncTick && Level.ReplayDeaths == 0; s++) RehearsalStep(level); }
            finally { CoreSoundWad.SuppressSounds = holdSounds; }
            LastPhsxStep = level.CurPhsxStep;
            Respawned("ladder:crossed");
            float resync = level.Bobs.Count > 0 ? (level.Bobs[0].Core.Data.Position - stood).Length() : -1;
            Events.Add("{\"kind\":\"botNote\",\"note\":\"ladder\",\"piece\":" + to + ",\"fromPiece\":" + from + ",\"toPiece\":" + to
                       + ",\"crossing\":\"crossed\",\"steps\":" + c.Steps + ",\"resync\":" + resync.ToString("0", CultureInfo.InvariantCulture)
                       + ",\"goal\":" + GoalJson() + ",\"text\":" + GeneratorApi.JsonStr("ladder: walked across to " + CheckpointName(to)) + "}");
        }

        /// <summary>
        /// One step of a crossing, run after each level step: the live ride (StepCrossing) and the rehearsal's validation
        /// (ValidateCrossing) both use it, so a validated plan is what the live bob does. Returns null while it goes on,
        /// "arrived" (standing at piece k+1's recording start), or why it failed.
        /// </summary>
        static string CrossTick(Level level, Bob b, Crossing c)
        {
            c.Steps++;
            c.StageSteps++;
            LadderPlan plan = c.Plan;
            if (Level.ReplayDeaths > 0 || b.Dying || b.Dead) return "died while " + StageName(c.Stage);
            switch (c.Stage)
            {
                case CrossStage.Settle:
                {
                    if (plan.Exact)
                    {
                        // Exact: the planned try launches Key steps after the recording's end (the settle controller till then).
                        if (c.Chosen == null) c.Chosen = plan.Crossings.Where(t => !t.Invalid).OrderBy(t => t.Key + t.Steps).FirstOrDefault();
                        if (c.Chosen == null) return "no planned crossing left";
                        if (c.StageSteps < c.Chosen.Key) return null;
                        b.ControlFunc = PolicyControl(level, b, plan, LadderPolicies[c.Chosen.Policy]);
                        c.Stage = CrossStage.Walk;
                        c.StageSteps = 0;
                        return null;
                    }
                    if (!AtRest(b, ref c.Rest)) return c.StageSteps > LadderSettleMax ? "the bob did not come to rest" : null;
                    if (plan.Across)
                    {
                        c.Chosen = plan.Crossings[0];
                        StartArrive(level, b, c);
                        return null;
                    }
                    int key = LaunchKey(level, b, plan);
                    if (key == int.MinValue) return "the bob came to rest on another support than in the rehearsal";
                    Vector2 want = plan.Emitter == null ? plan.Pose : b.MyPhsx.ObjectLandedOn.Core.Data.Position + plan.Offset;
                    float off = (b.Core.Data.Position - want).Length();
                    if (off > LadderPoseTolerance) return "the bob came to rest " + off.ToString("0", CultureInfo.InvariantCulture) + " px from the rehearsed pose";
                    Teleport(b, want, b.Core.Data.Velocity);
                    ZeroInput(b);
                    // The planned try that starts soonest: static, at its phase; carried, at its platform's rise (only ahead).
                    LadderTry best = null;
                    int bestCost = int.MaxValue;
                    foreach (LadderTry t in plan.Crossings)
                    {
                        if (t.Invalid) continue;
                        int wait = plan.Emitter == null ? CoreMath.Modulo(t.Key - key - LadderPreTicks, LadderPeriod) + LadderPreTicks : t.Key - key;
                        if (wait < LadderPreTicks) continue;
                        if (wait + t.Steps < bestCost) { bestCost = wait + t.Steps; best = t; }
                    }
                    if (best == null) return "the bob rode past every planned launch";
                    c.Chosen = best;
                    c.Stage = CrossStage.Wait;
                    c.StageSteps = 0;
                    return null;
                }

                case CrossStage.Wait:
                {
                    // At least LadderPreTicks of no input after the teleport, then the policy at the planned key.
                    if (c.StageSteps < LadderPreTicks) return null;
                    int key = LaunchKey(level, b, plan);
                    if (key == int.MinValue) return "the bob left its support while waiting";
                    if (key != c.Chosen.Key)
                    {
                        if (plan.Emitter != null && key > c.Chosen.Key) return "the planned launch was missed";
                        return c.StageSteps > LadderPeriod + LadderPreTicks + 2 ? "the planned launch never came" : null;
                    }
                    b.ControlFunc = PolicyControl(level, b, plan, LadderPolicies[c.Chosen.Policy]);
                    c.Stage = CrossStage.Walk;
                    c.StageSteps = 0;
                    return null;
                }

                case CrossStage.Walk:
                {
                    string res = WalkOutcome(b, plan, Floor(plan), ref c.Stand);
                    if (res == "crossed") { StartArrive(level, b, c); return null; }
                    if (res != null) return res + " on the ladder";
                    return c.StageSteps > LadderTryMax ? "the walk took too long" : null;
                }

                case CrossStage.Arrive:
                {
                    bool arrived = Math.Abs(c.Goal.X - b.Core.Data.Position.X) <= LadderArrive + 1 && b.MyPhsx.OnGround;
                    // Arrived, or the walk left the ground (a ledge): the re-sync at once, before the bob falls. A re-sync
                    // that would move the bob more than LadderResyncCap px is no walk-through: fall back (Robert's cap).
                    if (arrived || !b.MyPhsx.OnGround || c.StageSteps >= LadderArriveMax) return ResyncCheck(level, b, c);
                    return null;
                }
            }
            return null;
        }

        /// <summary>Standing on piece k+1: walk to the nearest position at which its recording stands (within the first
        /// LadderResyncTicks of it); the re-sync fast-forwards the recording to that tick.</summary>
        static void StartArrive(Level level, Bob b, Crossing c)
        {
            LadderPlan plan = c.Plan;
            c.Stage = CrossStage.Arrive;
            c.StageSteps = 0;
            LevelPiece next = level.LevelPieces[plan.Piece + 1];
            ComputerRecording rec = next.Recording[0];
            int n = Math.Min(Math.Min(next.PieceLength, rec.AutoOnGround.Length), plan.TargetTick + LadderResyncTicks);
            c.ResyncTick = plan.TargetTick;
            float best = float.MaxValue;
            for (int i = plan.TargetTick; i < n; i++)
            {
                if (!rec.AutoOnGround[i]) continue;
                float d = (rec.AutoLocs[i] - b.Core.Data.Position).Length();
                if (d < best) { best = d; c.ResyncTick = i; }
            }
            Vector2 goal = rec.AutoLocs[c.ResyncTick];
            c.Goal = goal;
            b.ControlFunc = count =>
            {
                float dx = goal.X - b.Core.Data.Position.X;
                b.CurInput.A_Button = false;
                b.CurInput.xVec = new Vector2(Math.Abs(dx) <= LadderArrive ? 0 : Math.Sign(dx), 0);
            };
        }

        /// <summary>A try that stands on piece k+1 counts only if the re-sync can stay within the cap: a standing tick of
        /// the recording (in its window) level with the bob (walkable), or one within the cap.</summary>
        static bool ResyncReachable(Level level, Bob b, LadderPlan plan)
        {
            LevelPiece next = level.LevelPieces[plan.Piece + 1];
            ComputerRecording rec = next.Recording[0];
            int n = Math.Min(Math.Min(next.PieceLength, rec.AutoOnGround.Length), plan.TargetTick + LadderResyncTicks);
            Vector2 p = b.Core.Data.Position;
            for (int i = plan.TargetTick; i < n; i++)
            {
                if (!rec.AutoOnGround[i]) continue;
                Vector2 d = rec.AutoLocs[i] - p;
                if (Math.Abs(d.Y) < 30 || d.Length() <= LadderResyncCap) return true;
            }
            return false;
        }

        static string ResyncCheck(Level level, Bob b, Crossing c)
        {
            Vector2 at = level.LevelPieces[c.Plan.Piece + 1].Recording[0].AutoLocs[c.ResyncTick];
            float d = (at - b.Core.Data.Position).Length();
            c.ResyncDistance = d;
            return d <= LadderResyncCap ? "arrived" : "the re-sync would move the bob " + d.ToString("0", CultureInfo.InvariantCulture) + " px (over " + LadderResyncCap + ")";
        }

        static float Floor(LadderPlan plan) { return Math.Min(plan.Pose.Y, plan.Target.Y) - LadderFallDepth; }

        /// <summary>
        /// The rehearsal's last check: from the level reset at checkpoint k, piece k's recording replayed, the crossing as
        /// the live ride will make it (CrossTick). A crossing that fails here is marked invalid and the next one is checked.
        /// Returns the steps taken; plan.Validated says whether one passed.
        /// </summary>
        static int ValidateCrossings(Level level, int k, LadderPlan plan)
        {
            int steps = 0;
            for (int attempt = 0; attempt < LadderValidations && plan.Crossings.Any(t => !t.Invalid); attempt++)
            {
                int rs = ReplayPieceSilently(level, k);
                if (rs < 0) break;
                steps += rs;
                Bob b = level.Bobs[0];
                SettleControl(level, b, k);
                Level.ReplayCodeProbe = true;
                Level.ReplayDeaths = 0;
                var c = new Crossing { Stage = CrossStage.Settle, Plan = plan };
                string res = null;
                for (int s = 0; s < LadderSettleMax + 2 * LadderPeriod + LadderTryMax + LadderArriveMax + 20 && res == null; s++)
                {
                    RehearsalStep(level);
                    steps++;
                    res = CrossTick(level, b, c);
                }
                plan.ValidationLog.Add((c.Chosen == null ? "-" : plan.Tries.IndexOf(c.Chosen).ToString(CultureInfo.InvariantCulture)) + ": " + (res ?? "no result"));
                if (res == "arrived") { plan.Validated = c.Chosen; plan.ValidatedSteps = c.Steps; return steps; }
                if (c.Chosen == null) break;   // nothing chosen: no crossing can start from the live rest
                c.Chosen.Invalid = true;
            }
            return steps;
        }

        static string StageName(CrossStage s)
        {
            switch (s)
            {
                case CrossStage.Settle: return "settling at the piece's end";
                case CrossStage.Wait: return "waiting for the ladder";
                default: return "on the ladder";
            }
        }

        static string PlanJson(LadderPlan p, string when)
        {
            var sb = new StringBuilder();
            sb.Append("{\"piece\":").Append(p.Piece).Append(",\"when\":").Append(GeneratorApi.JsonStr(when)).Append(",\"abilities\":").Append(GeneratorApi.JsonStr(p.Abilities))
              .Append(",\"refusal\":").Append(p.Refusal == null ? "null" : GeneratorApi.JsonStr(p.Refusal))
              .Append(",\"pose\":").Append(Vec(p.Pose)).Append(",\"target\":").Append(Vec(p.Target))
              .Append(",\"support\":").Append(p.Exact ? "\"exact\"" : p.Across ? "\"across\"" : p.Emitter == null ? "\"static\"" : "\"carried\"")
              .Append(",\"columns\":").Append(float.IsNaN(p.LeftX) ? "null" : "[" + p.LeftX.ToString("0", CultureInfo.InvariantCulture) + "," + p.RightX.ToString("0", CultureInfo.InvariantCulture) + "]")
              .Append(",\"tries\":").Append(p.Tries.Count).Append(",\"crossings\":").Append(p.Crossings.Count)
              .Append(",\"steps\":").Append(p.Steps).Append(",\"replaySteps\":").Append(p.ReplaySteps).Append(",\"settleSteps\":").Append(p.SettleSteps)
              .Append(",\"ms\":").Append(p.Ms.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"validated\":").Append(p.Validated == null ? "null" : p.Tries.IndexOf(p.Validated).ToString(CultureInfo.InvariantCulture))
              .Append(",\"validatedSteps\":").Append(p.ValidatedSteps)
              .Append(",\"validations\":[").Append(string.Join(",", p.ValidationLog.Select(GeneratorApi.JsonStr))).Append(']')
              .Append(",\"results\":{");
            bool first = true;
            foreach (var g in p.Tries.GroupBy(t => t.Result))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(GeneratorApi.JsonStr(g.Key)).Append(':').Append(g.Count());
            }
            sb.Append("},\"crossed\":[");
            for (int i = 0; i < p.Crossings.Count; i++)
            {
                var t = p.Crossings[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"try\":").Append(p.Tries.IndexOf(t)).Append(",\"policy\":").Append(GeneratorApi.JsonStr(t.Policy < 0 ? "across" : LadderPolicies[t.Policy].ToString()))
                  .Append(",\"key\":").Append(t.Key).Append(",\"steps\":").Append(t.Steps).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string LadderStatusJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"mode\":").Append(GeneratorApi.JsonStr(LadderMode)).Append(",\"crossed\":").Append(StatCrossed)
              .Append(",\"fellBack\":").Append(StatFellBack).Append(",\"rehearsals\":").Append(StatRehearsals)
              .Append(",\"planMs\":").Append(LadderPlanMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"stage\":").Append(Cross == null ? "null" : GeneratorApi.JsonStr(Cross.Stage.ToString()))
              .Append(",\"plans\":[").Append(string.Join(",", LadderLog)).Append("]}");
            return sb.ToString();
        }

        static void ResetLadders()
        {
            LadderPlans.Clear();
            LadderLog.Clear();
            LadderPlanMs = 0;
            Cross = null;
        }
    }
}
