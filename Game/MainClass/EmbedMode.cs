using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.Xna.Framework;

using CloudberryKingdom.Bobs;
using CloudberryKingdom.Levels;
using CloudberryKingdom.InGameObjects;

#if BROWSER
using System.Runtime.InteropServices.JavaScript;
#endif

namespace CloudberryKingdom
{
    /// <summary>
    /// Embedded mode (page ?embed=1, game argument --embed): the page as an Archipelago substrate. No attract mode and no
    /// menus: the game idles on the API's empty level until the page (its __swfBridge, main.js) asks it to Play a level
    /// made from a ck-level/2 request. The page checks the request's document hash before it asks; EmbedMode makes the
    /// level from the same arguments by the same path (GeneratorApi.BuildSeedData), checks the played level against the
    /// document's coins, and then:
    ///   - a CHECK coin's collection queues a "location" event (once per coin per configure);
    ///   - Up at the end door queues a "door" event {door:'end'} (once per arrival at the door; the page's exit menu), and
    ///     Up at the START door one with door:'start' (the way back; armed only once the bob has left the start door's reach,
    ///     so the first jump of a level does not open it); RearmDoors lets the page re-arm both after its menu closes;
    ///   - check coins are drawn by DrawCheckCoin: the page's texture (the Archipelago logo from the art library, or its own
    ///     fallback), an outline once the host says the check is checked (SetChecked / Play's checked);
    ///   - held items: platform items through PlatformSuppression, the Jetpack ability on the level's hero (zeroed
    ///     when the level's hero has a jetpack and the Jetpack item is not held), and the DoubleJump ability (a hero
    ///     with more than one jump has ONE when the DoubleJump item is not held; P5a);
    ///   - the bot (BotWalkTo, P7b): the player's own bob plays the piece recordings ON (CompControl as the computer's
    ///     replay, the snap off by default) from where it is to the coin or the door, and keeps playing after it; it respawns
    ///     only at a ladder (botNote), after a death or a miss (the game's respawn at the last checkpoint passed, bounded),
    ///     for a goal behind it, or for the way back (see "The bot" below).
    /// The page takes the events every frame (TakeEvents) and makes the bridge calls.
    /// </summary>
    public static partial class EmbedMode
    {
        /// <summary>The region payload version the page accepts (the contract the cloudberry registry entry emits).</summary>
        public const string PayloadVersion = "cloudberry-region/1";
        /// <summary>The bot gives up this many ticks past the goal's recorded tick (or the final recording's end).</summary>
        public const int BotSlackTicks = 120;
        /// <summary>Walking to the door after the final recording: no input within this many px of its x.</summary>
        public const float DoorSteerDeadZone = 20;
        /// <summary>
        /// A played coin matches the document's coin (its objects[] position, written right after the level's reset) when it
        /// is the nearest coin and this close: coins hover, so a position read at another tick is a few px off; coins sit on an
        /// 80 px grid, so this cannot pick a neighbour.
        /// </summary>
        public const float CoinMatchDistance = 30;
        public const string JetpackItem = "Jetpack";
        public const string DoubleJumpItem = "DoubleJump";
        /// <summary>Up held, as the door's prompt reads it (Door.Interact: xVec.Y > .85).</summary>
        public const float DoorUpThreshold = .85f;

        public static bool Active;

        class CheckCoin
        {
            public string Name;
            public int Id, Piece, Tick;
            public Vector2 Pos;
            /// <summary>The matched coin's generated position (Core.StartData): the coin's identity. The engine reuses coin
            /// objects across level resets, so a reference held from Play can name another coin later.</summary>
            public Vector2 StartPos;
            public Coin Coin;
            public bool Sent;
            /// <summary>Collected in this configure (a later walk to it is reached at once: the bob does not go back for it).</summary>
            public bool Collected;
            /// <summary>The host says this check is checked (Play's checked, SetChecked): drawn as an outline.</summary>
            public bool Checked;
        }

        static readonly List<CheckCoin> Checks = new List<CheckCoin>();
        static readonly List<string> Events = new List<string>();
        static Level PlayLevel;
        static GameData PlayGame;
        static bool LevelHeroJetpack, JetpackHeld;
        /// <summary>The played level's hero's NumJumps (1 = no double jump to gate); DoubleJumpHeld restores it (P5a).</summary>
        static int LevelHeroJumps = 1;
        static bool DoubleJumpHeld;
        static List<string> HeldPlatforms = new List<string>();
        static bool DoorArmed = true, StartDoorArmed;
        /// <summary>RearmDoors: both doors re-arm at the next step on which no bob holds Up.</summary>
        static bool RearmPending;
        static int LastPhsxStep;
        static BotWalk Bot;
        static int Configures;
        /// <summary>Pieces after the first keep the engine's CamStartPos (0,0); when set, Play gives each its start's camera.</summary>
        static bool CamStartFix;

        // -------------------------------------------------------------------------------------------------------------
        // Play

        /// <summary>
        /// Makes and starts the level. json: {generate: ck-level/2 request, checks: [{name, id, pos:[x,y], piece, tick}],
        /// checked: [name], coinCount, camStartFix}. Returns {ok, …} or {ok:false, error}.
        /// </summary>
        public static string Play(string json)
        {
            try
            {
                if (!GeneratorApi.Ready) throw new InvalidOperationException("not ready: content still loading");
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement, e;
                    string generate = root.GetProperty("generate").GetRawText();
                    var checkedNames = new HashSet<string>();
                    if (root.TryGetProperty("checked", out e)) foreach (var c in e.EnumerateArray()) checkedNames.Add(c.GetString());
                    int coinCount = root.TryGetProperty("coinCount", out e) ? e.GetInt32() : -1;
                    CamStartFix = root.TryGetProperty("camStartFix", out e) && e.GetBoolean();

                    var checks = new List<CheckCoin>();
                    if (root.TryGetProperty("checks", out e))
                        foreach (var c in e.EnumerateArray())
                        {
                            JsonElement pos = c.GetProperty("pos");
                            checks.Add(new CheckCoin
                            {
                                Name = c.GetProperty("name").GetString(),
                                Id = c.GetProperty("id").GetInt32(),
                                Pos = new Vector2(pos[0].GetSingle(), pos[1].GetSingle()),
                                Piece = c.GetProperty("piece").GetInt32(),
                                Tick = c.GetProperty("tick").GetInt32(),
                                Sent = false,
                            });
                        }
                    foreach (var c in checks) c.Sent = c.Checked = checkedNames.Contains(c.Name);

                    Level level = GeneratorApi.MakePlayableLevel(generate, out PlayGame);
                    PlayLevel = level;

                    // The played level against the document: its coin count and every check coin, by position.
                    var played = new List<Coin>();
                    foreach (ObjectBase obj in level.Objects)
                    {
                        Coin coin = obj as Coin;
                        if (coin != null && !coin.Core.MarkedForDeletion) played.Add(coin);
                    }
                    int coins = played.Count;
                    foreach (var c in checks)
                    {
                        Coin best = played.OrderBy(coin => (coin.Core.Data.Position - c.Pos).Length()).FirstOrDefault();
                        if (best != null && (best.Core.Data.Position - c.Pos).Length() < CoinMatchDistance && !checks.Any(o => o.Coin == best))
                        {
                            c.Coin = best;
                            c.StartPos = best.Core.StartData.Position;
                        }
                    }
                    var missing = checks.Where(c => c.Coin == null).Select(c => c.Name).ToList();
                    if (coinCount >= 0 && coins != coinCount)
                        throw new InvalidOperationException("the played level has " + coins + " coins, the document " + coinCount);
                    if (missing.Count > 0)
                        throw new InvalidOperationException("check coins not in the played level: " + string.Join(", ", missing));
                    foreach (var c in checks)
                        if (c.Piece < 0 || c.Piece >= level.LevelPieces.Count) throw new InvalidOperationException("check " + c.Name + ": no piece " + c.Piece);

                    Checks.Clear();
                    Checks.AddRange(checks);
                    Events.Clear();
                    Bot = null;
                    Riding = null;
                    ResetLadders();
                    DoorArmed = true;
                    StartDoorArmed = false;
                    RearmPending = false;
                    Configures++;

                    BobPhsxNormal hero = level.DefaultHeroType as BobPhsxNormal;
                    LevelHeroJetpack = hero != null && hero.JetPack;
                    LevelHeroJumps = hero != null ? Math.Max(1, hero.NumJumps) : 1;
                    ApplyHeld();

                    var sb = new StringBuilder();
                    sb.Append("{\"ok\":true,\"configures\":").Append(Configures)
                      .Append(",\"pieces\":").Append(level.LevelPieces.Count)
                      .Append(",\"coins\":").Append(coins)
                      .Append(",\"checks\":").Append(Checks.Count)
                      .Append(",\"heroJetpack\":").Append(LevelHeroJetpack ? "true" : "false")
                      .Append(",\"heroJumps\":").Append(LevelHeroJumps)
                      .Append(",\"camStartFix\":").Append(CamStartFix ? "true" : "false")
                      .Append(",\"startDoor\":").Append(level.StartDoor != null ? "true" : "false");
                    sb.Append('}');
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[embed] play refused: " + e);
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.GetType().Name + ": " + e.Message) + "}";
            }
        }

        /// <summary>Called by MakePlayableLevel once the level is made, before its first reset: the embed's own PostMake.</summary>
        internal static void PostMake(Level level)
        {
            ILevelConnector door = (ILevelConnector)level.FindIObject(LevelConnector.EndOfLevelCode);
            if (door != null) door.OnOpen = d => OnDoor(d, false);
            Door start = level.StartDoor;
            if (start != null) start.OnOpen = d => OnStartDoor(d);
            level.MyGame.AddGameObject(new EmbedDriver());

            if (CamStartFix)
            {
                // Pieces after the first keep CamStartPos (0,0): a respawn at their checkpoint puts the camera at the
                // level's origin for the reset step (the bob is off screen: BobDeathType.LeftBehind, covered by the respawn's
                // invulnerability) and then pans across the level. Each piece starts its camera where its bob starts.
                for (int k = 1; k < level.LevelPieces.Count; k++)
                {
                    LevelPiece piece = level.LevelPieces[k];
                    if (piece.CamStartPos == Vector2.Zero && piece.StartData != null && piece.StartData.Length > 0)
                        piece.CamStartPos = piece.StartData[0].Position;
                }
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Items

        /// <summary>json: {platforms: [item names], jetpack: bool, doubleJump: bool}. Returns the state.</summary>
        public static string SetItems(string json)
        {
            try
            {
                var platforms = new List<string>();
                bool jetpack = false, doubleJump = false;
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement e;
                    if (doc.RootElement.TryGetProperty("platforms", out e)) foreach (var p in e.EnumerateArray()) platforms.Add(p.GetString());
                    if (doc.RootElement.TryGetProperty("jetpack", out e)) jetpack = e.GetBoolean();
                    if (doc.RootElement.TryGetProperty("doubleJump", out e)) doubleJump = e.GetBoolean();
                }
                foreach (string p in platforms)
                    if (PlatformSuppression.ItemIndex(p) < 0) throw new ArgumentException("unknown platform item '" + p + "'");
                HeldPlatforms = platforms;
                JetpackHeld = jetpack;
                DoubleJumpHeld = doubleJump;
                ApplyHeld();
                return ItemsJson();
            }
            catch (Exception e)
            {
                return "{\"error\":" + GeneratorApi.JsonStr(e.Message) + "}";
            }
        }

        static string ItemsJson()
        {
            return "{\"platforms\":" + GeneratorApi.JsonStrings(HeldPlatforms) + ",\"jetpack\":" + (JetpackHeld ? "true" : "false")
                + ",\"heroJetpack\":" + (LevelHeroJetpack ? "true" : "false") + ",\"jetpackOn\":" + (JetpackOn ? "true" : "false")
                + ",\"doubleJump\":" + (DoubleJumpHeld ? "true" : "false") + ",\"heroJumps\":" + LevelHeroJumps
                + ",\"jumpsOn\":" + JumpsOn + "}";
        }

        static bool JetpackOn { get { return LevelHeroJetpack && JetpackHeld; } }
        /// <summary>The jumps a bob of the played level has: the hero's, or one without the DoubleJump item.</summary>
        static int JumpsOn { get { return LevelHeroJumps > 1 && !DoubleJumpHeld ? 1 : LevelHeroJumps; } }

        static void ApplyHeld()
        {
            PlatformSuppression.SetHeld(HeldPlatforms);
            if (PlayLevel != null && PlayLevel.Bobs != null)
                foreach (Bob bob in PlayLevel.Bobs) ApplyAbilities(bob.MyPhsx);
        }

        /// <summary>The Jetpack and DoubleJump items on a bob of the played level (Bob.SetHeroPhsx calls it for every new phsx;
        /// not during a generation).</summary>
        public static void ApplyAbilities(BobPhsx phsx)
        {
            if (!Active || PlayLevel == null || GeneratorApi.Generating) return;
            BobPhsxNormal normal = phsx as BobPhsxNormal;
            if (normal != null && LevelHeroJetpack) normal.JetPack = JetpackHeld;
            if (normal != null && LevelHeroJumps > 1) normal.NumJumps = JumpsOn;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Coins and the door

        public static void OnCoin(Coin coin, Bob bob)
        {
            if (!Active || coin.Core.MyLevel != PlayLevel || Rehearsing) return;
            foreach (var c in Checks)
            {
                if (c.StartPos != coin.Core.StartData.Position) continue;
                c.Collected = true;
                if (Bot != null && Bot.Check == c) Bot.Reached = true;
                if (c.Sent) return;
                c.Sent = true;
                Events.Add("{\"kind\":\"location\",\"name\":" + GeneratorApi.JsonStr(c.Name) + ",\"bot\":" + (Bot != null || Riding != null ? "true" : "false")
                    + ",\"coin\":" + Vec(coin.Core.StartData.Position) + ",\"bob\":" + Vec(bob.Core.Data.Position) + "}");
                return;
            }
        }

        static string Vec(Vector2 v)
        {
            return "[" + v.X.ToString("R", CultureInfo.InvariantCulture) + "," + v.Y.ToString("R", CultureInfo.InvariantCulture) + "]";
        }

        static void OnDoor(Door door, bool bot)
        {
            if (!DoorArmed) return;
            DoorArmed = false;
            Events.Add("{\"kind\":\"door\",\"door\":\"end\",\"bot\":" + (bot ? "true" : "false") + "}");
        }

        static void OnStartDoor(Door door)
        {
            if (!StartDoorArmed || Bot != null) return;
            StartDoorArmed = false;
            Events.Add("{\"kind\":\"door\",\"door\":\"start\",\"bot\":false}");
        }

        /// <summary>The page's exit menu closed: Up at a door opens it again (once Up is no longer held).</summary>
        public static void RearmDoors() { RearmPending = true; }

        /// <summary>json: [check names] the host says are checked (drawn as outlines); every other check is drawn unchecked.</summary>
        public static string SetChecked(string json)
        {
            try
            {
                var names = new HashSet<string>();
                using (var doc = JsonDocument.Parse(json))
                    foreach (var n in doc.RootElement.EnumerateArray()) names.Add(n.GetString());
                foreach (var c in Checks) c.Checked = names.Contains(c.Name);
                return "{\"ok\":true,\"checked\":" + Checks.Count(c => c.Checked) + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.Message) + "}";
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // The check coins' look

        /// <summary>The page's check-coin textures (premultiplied RGBA): unchecked and checked (outline). Null = the default look.</summary>
        static CoreEngine.CoreTexture CheckTex, CheckedTex;
        /// <summary>World size the page's texture is drawn at (an ordinary coin is a 76 wide circle).</summary>
        static float CheckTexSize = 128;
        /// <summary>Check coins drawn so far (one per coin per drawn frame), by look: a test instrument for the status.</summary>
        static long DrawnUnchecked, DrawnChecked;
        /// <summary>The look without a texture from the page: a larger purple coin; checked = a purple ring.</summary>
        public const float FallbackRadius = 52, FallbackRing = 12;
        static readonly Color FallbackColor = new Color(150, 70, 230, 255);

        /// <summary>
        /// json: {w, h, size, fill: base64 RGBA, outline: base64 RGBA} (premultiplied alpha), or {clear:true} for the default
        /// look. The page makes these from the art library's Archipelago logo, or its own fallback when that fetch fails.
        /// </summary>
        public static string SetCheckLook(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement, e;
                    if (root.TryGetProperty("clear", out e) && e.GetBoolean()) { CheckTex = CheckedTex = null; return "{\"ok\":true}"; }
                    int w = root.GetProperty("w").GetInt32(), h = root.GetProperty("h").GetInt32();
                    if (root.TryGetProperty("size", out e)) CheckTexSize = e.GetSingle();
                    CheckTex = MakeTexture(w, h, Convert.FromBase64String(root.GetProperty("fill").GetString()));
                    CheckedTex = MakeTexture(w, h, Convert.FromBase64String(root.GetProperty("outline").GetString()));
                    return "{\"ok\":true,\"w\":" + w + ",\"h\":" + h + "}";
                }
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.GetType().Name + ": " + e.Message) + "}";
            }
        }

        static CoreEngine.CoreTexture MakeTexture(int w, int h, byte[] rgba)
        {
            if (rgba.Length != w * h * 4) throw new ArgumentException("texture: " + rgba.Length + " bytes for " + w + "x" + h);
            var tex = new Microsoft.Xna.Framework.Graphics.Texture2D(Tools.Device, w, h);
            tex.SetData(rgba);
            return new CoreEngine.CoreTexture { Tex = tex, Width = w, Height = h, FromCode = true, Name = "EmbedCheckCoin" };
        }

        /// <summary>Coin.Draw (box drawing): true when the coin is a check coin of the played level and was drawn here.</summary>
        public static bool DrawCheckCoin(Coin coin)
        {
            if (PlayLevel == null || coin.Core.MyLevel != PlayLevel) return false;
            CheckCoin check = null;
            foreach (var c in Checks) if (c.StartPos == coin.Core.StartData.Position) { check = c; break; }
            if (check == null) return false;
            CoreEngine.CoreTexture tex = check.Checked ? CheckedTex : CheckTex;
            if (check.Checked) DrawnChecked++; else DrawnUnchecked++;
            if (tex != null)
                Tools.QDrawer.DrawSquareDot(coin.Pos, Color.White, CheckTexSize, tex, Tools.BasicEffect);
            else if (check.Checked)
            {
                // A ring: the purple disc's edge, drawn as segments of a circle of short lines.
                const int n = 24;
                float r = FallbackRadius - FallbackRing / 2;
                for (int i = 0; i < n; i++)
                {
                    double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                    Tools.QDrawer.DrawLine(coin.Pos + r * new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)),
                        coin.Pos + r * new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)), FallbackColor, FallbackRing);
                }
            }
            else
                Tools.QDrawer.DrawCircle(coin.Pos, FallbackRadius, FallbackColor);
            return true;
        }

        // -------------------------------------------------------------------------------------------------------------
        // The bot (P7b: continuous playback)
        //
        // The bob plays the piece recordings ON. A walk's goal is reached by playing on from where the bob is, and the bob
        // keeps playing after it (the RIDE) until its recording runs out; then it PARKS (control released where the
        // recording left it). A coin on the way is collected in passing. The bob respawns only where no recording carries it:
        //   ladder   the end of piece k's recording while the goal is in a later piece: respawn at checkpoint k+1 (no
        //            recording crosses the elevator ladder between pieces; P7c may walk it). A botNote says so.
        //   restart  reason 'died': a death (a just-received item changed a platform or the hero, so the recording no longer
        //            fits) is the game's own death and respawn at the last checkpoint the bob passed (the current piece's),
        //            then that piece's recording again. Reason 'missed': the goal's tick passed (or its piece's recording ran
        //            out) without it: the same restart, without a death.
        //   rewind   the goal is behind the bob (an earlier piece, or a tick of this piece already played): respawn at the
        //            goal's piece checkpoint, one the bob has passed.
        //   idle     the bob is not on a recording (the first walk, a keyboard player's bob, a parked bob that died):
        //            restart at the current piece's checkpoint (the last one passed).
        //   start    a walk to a start-door side (the way back, P3a): restart the level at the start door (item-free).
        // A walk's restarts are bounded: after BotMaxRetries of them, or the same failure twice (the same piece, tick and items:
        // a replay repeats it), the walk ends 'died' / 'timeout' with exhausted:true and the reason, by name.
        // options.respawn: true = the pre-P7b walk (respawn at the goal's piece checkpoint), for A/B measurements.

        /// <summary>A walk restarts at most this many times (deaths and misses together) before it fails by name.</summary>
        public const int BotMaxRetries = 3;
        /// <summary>After a death the game's own respawn is waited for this long, then forced.</summary>
        public const int BotRespawnWaitTicks = 600;

        class BotWalk
        {
            public string Kind, Id;
            /// <summary>The goal's piece and the recording tick that reaches it (-1: the piece's end, then the door).</summary>
            public int Piece, Tick, Steps, PieceSteps, Retries, MaxRetries = BotMaxRetries;
            public CheckCoin Check;
            public bool Snap, Reached, Respawn, Armed;
            public string LastFailure;
        }

        enum RideState { AwaitRespawn, Playing, Steering, Parked, Crossing }

        /// <summary>The bob's playback, which outlives the walk that started it.</summary>
        class Ride
        {
            public RideState State;
            public int Piece;
            public bool Snap;
            /// <summary>&gt;= 0: a death at this step; the recording waits for the game's respawn (the step count restarts).</summary>
            public int DeathStep = -1, WaitSteps;
        }

        static Ride Riding;
        static int StatWalks, StatContinued, StatDeaths, StatParkedDeaths;
        static readonly SortedDictionary<string, int> StatRespawns = new SortedDictionary<string, int>();

        /// <summary>json: {goal: {kind: 'pickup'|'portal'|'startdoor', id}, options: {snap, respawn, retries}}. Returns {ok, piece, tick, how}.</summary>
        public static string BotWalkTo(string json)
        {
            try
            {
                Level level = PlayLevel;
                if (level == null || Tools.CurLevel != level) throw new InvalidOperationException("no level is being played");
                var walk = new BotWalk();
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement goal = doc.RootElement.GetProperty("goal"), e;
                    walk.Kind = goal.GetProperty("kind").GetString();
                    walk.Id = goal.TryGetProperty("id", out e) ? e.GetString() : null;
                    if (doc.RootElement.TryGetProperty("options", out e) && e.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement s;
                        if (e.TryGetProperty("snap", out s)) walk.Snap = s.GetBoolean();
                        if (e.TryGetProperty("respawn", out s)) walk.Respawn = s.GetBoolean();
                        if (e.TryGetProperty("retries", out s)) walk.MaxRetries = Math.Max(0, s.GetInt32());
                        // P7c: the ladder walk ('cross', the default) or the P7b respawn ('respawn'); the rehearsal's budget
                        // (measurement). A changed value drops the plans made at configure (the next ladder plans anew).
                        if (e.TryGetProperty("ladder", out s)) SetLadderOption(ref LadderMode, s.GetString() == "respawn" ? "respawn" : "cross");
                        if (e.TryGetProperty("ladderBudget", out s)) SetLadderOption(ref LadderBudget, Math.Max(1, s.GetInt32()));
                        if (e.TryGetProperty("ladderKeep", out s)) SetLadderOption(ref LadderKeep, Math.Max(1, s.GetInt32()));
                        if (e.TryGetProperty("ladderSteps", out s)) SetLadderOption(ref LadderStepScale, Math.Max(.1f, s.GetSingle()));
                    }
                }
                if (walk.Kind == "pickup")
                {
                    walk.Check = Checks.FirstOrDefault(c => c.Name == walk.Id);
                    if (walk.Check == null) throw new ArgumentException("no check coin '" + walk.Id + "'");
                    walk.Piece = walk.Check.Piece;
                    walk.Tick = walk.Check.Tick;
                }
                else if (walk.Kind == "portal")
                {
                    walk.Piece = level.LevelPieces.Count - 1;
                    walk.Tick = -1;
                }
                else if (walk.Kind == "startdoor")
                {
                    // The way back (P3a): restart the level (piece 0's checkpoint is the start door) and leave by the start
                    // door, the side in id. The page decides that a side is a start-door side (startDoorSides).
                    if (level.StartDoor == null) throw new InvalidOperationException("the level has no start door");
                    walk.Piece = 0;
                    walk.Tick = -1;
                }
                else throw new ArgumentException("unknown goal kind '" + walk.Kind + "'");

                EndWalk(null, null);   // a walk in progress ends; the ride goes on
                StatWalks++;
                Bot = walk;
                string how;
                if (walk.Kind == "startdoor") { RespawnRide(level, 0, "start", null); Riding = null; how = "start"; }
                else if (walk.Kind == "pickup" && walk.Check.Collected) { walk.Reached = true; how = "collected"; }
                else if (walk.Respawn) { RespawnRide(level, walk.Piece, "goal", null); how = "respawn"; }
                else how = Continue(level, walk);
                return "{\"ok\":true,\"piece\":" + walk.Piece + ",\"tick\":" + walk.Tick + ",\"how\":" + GeneratorApi.JsonStr(how) + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.GetType().Name + ": " + e.Message) + "}";
            }
        }

        /// <summary>Where a walk starts: on from the ride when the goal is ahead of it, else a respawn (rewind / idle).</summary>
        static string Continue(Level level, BotWalk walk)
        {
            Ride r = Riding;
            if (r != null && r.State == RideState.Parked && !ParkHolds(level, r)) r = Riding = null;
            if (r == null)
            {
                int cur = level.LevelPieces.IndexOf(level.CurPiece);
                if (cur < 0 || cur > walk.Piece) { RespawnRide(level, walk.Piece, "rewind", null); return "rewind"; }
                RespawnRide(level, cur, "idle", null);
                return "idle";
            }
            bool ahead = r.Piece < walk.Piece;
            if (r.Piece == walk.Piece)
            {
                if (walk.Tick < 0) ahead = true;   // the door: on through this piece's end, then the steering
                else if (r.State == RideState.AwaitRespawn) ahead = true;
                // Not yet collected and not yet past the miss rule's tick (a coin the bob is about to collect counts as ahead).
                else if (r.State == RideState.Playing) ahead = RecTick(level) <= walk.Tick + BotSlackTicks;
            }
            if (!ahead) { RespawnRide(level, walk.Piece, "rewind", null); return "rewind"; }
            r.Snap = walk.Snap;
            if (r.State == RideState.Playing) Level.ReplaySnapDisabled = !walk.Snap;
            StatContinued++;
            return "continue";
        }

        static int RecTick(Level level)
        {
            return level.Bobs.Count > 0 ? level.CurPhsxStep - level.Bobs[0].IndexOffset : level.CurPhsxStep;
        }

        /// <summary>
        /// A parked bob is still at its piece's end: alive and in that piece (no respawn since). Where exactly does not matter
        /// (the piece-end elevator carries it): a parked bob is only ever sent on (the ladder, or the steering to the door).
        /// </summary>
        static bool ParkHolds(Level level, Ride r)
        {
            if (level.SetToReset || level.Bobs.Count == 0 || level.LevelPieces.IndexOf(level.CurPiece) != r.Piece) return false;
            Bob bob = level.Bobs[0];
            return !bob.Dead && !bob.Dying;
        }

        static void ReleaseBobs(Level level)
        {
            Level.ReplayProbe = false;
            Level.ReplaySnapDisabled = false;
            if (level != null)
                foreach (Bob bob in level.Bobs) { bob.CompControl = false; bob.MyRecord = null; bob.CodeControl = false; bob.ControlFunc = null; }
        }

        /// <summary>A respawn at a piece's checkpoint (the engine's reset at that piece); the recording starts once it is done.</summary>
        static void RespawnRide(Level level, int piece, string kind, string noteFields)
        {
            Cross = null;
            Level.ReplayCodeProbe = false;
            ReleaseBobs(level);
            level.SetCurrentPiece(piece);
            level.SetToReset = true;
            Riding = new Ride { State = RideState.AwaitRespawn, Piece = piece, Snap = Bot != null && Bot.Snap };
            Respawned(kind);
            Note(kind, piece, noteFields);
        }

        static void Respawned(string kind)
        {
            int n;
            StatRespawns.TryGetValue(kind, out n);
            StatRespawns[kind] = n + 1;
        }

        static string NoteText(string kind, int piece, string reason, int attempt, int maxAttempts)
        {
            // For people, piece k starts at checkpoint k, and piece 0 at the start (the start door, no checkpoint). The
            // Archipelago-CC status strip names them the same way from the fields (P7a).
            string cp = piece <= 0 ? "the start" : "checkpoint " + piece;
            switch (kind)
            {
                case "ladder": return "ladder: skipping to " + cp;
                case "restart": return (reason == "died" ? "died" : "missed the goal") + ": restarting from " + cp + " (attempt " + attempt + "/" + maxAttempts + ")";
                case "rewind": return "the goal is behind: back to " + cp;
                case "idle": return piece <= 0 ? "starting at the start" : "starting from " + cp;
                case "start": return "back to the start door";
                case "goal": return "respawn at " + cp;
                case "died": return "died between walks: back at " + cp;
                default: return kind;
            }
        }

        /// <summary>Queues {kind:'botNote', note, piece, …fields, goal, text}.</summary>
        static void Note(string kind, int piece, string fields, string reason = null, int attempt = 0, int maxAttempts = 0)
        {
            BotWalk walk = Bot;
            var sb = new StringBuilder();
            sb.Append("{\"kind\":\"botNote\",\"note\":").Append(GeneratorApi.JsonStr(kind)).Append(",\"piece\":").Append(piece);
            if (fields != null) sb.Append(fields);
            if (reason != null) sb.Append(",\"reason\":").Append(GeneratorApi.JsonStr(reason)).Append(",\"attempt\":").Append(attempt).Append(",\"maxAttempts\":").Append(maxAttempts);
            sb.Append(",\"goal\":");
            if (walk == null) sb.Append("null");
            else sb.Append("{\"kind\":").Append(GeneratorApi.JsonStr(walk.Kind)).Append(",\"id\":").Append(GeneratorApi.JsonStr(walk.Id)).Append('}');
            sb.Append(",\"text\":").Append(GeneratorApi.JsonStr(NoteText(kind, piece, reason, attempt, maxAttempts))).Append('}');
            Events.Add(sb.ToString());
        }

        static void StartRecording(Level level, Ride r)
        {
            r.Piece = level.LevelPieces.IndexOf(level.CurPiece);   // the game's respawn decides (the last checkpoint passed)
            foreach (Bob bob in level.Bobs)
            {
                bob.CompControl = true;
                bob.MyRecord = level.CurPiece.Recording[bob.MyPieceIndex];
                bob.IndexOffset = 0;
            }
            Level.ReplayProbe = true;           // a death is counted (and handled by the bot), not acted on
            Level.ReplayDeaths = 0;
            Level.ReplaySnapDisabled = !r.Snap;
            r.State = RideState.Playing;
            if (Bot != null) Bot.PieceSteps = 0;
        }

        static void Park(Level level, Ride r)
        {
            ReleaseBobs(level);
            r.State = RideState.Parked;
        }

        static void BeginSteering(Level level, Ride r, Door door)
        {
            // The final piece's recording is over and the door is not in reach yet. After its recording the engine feeds the
            // computer "right + jump", which can carry the bob over a low door; walk to the door instead.
            ReleaseBobs(level);
            r.State = RideState.Steering;
            // The way planned at configure (P8): plain walking unless the rehearsal found it dies or sticks.
            DoorPlan plan;
            DoorPolicy pol = DoorPlans.TryGetValue(AbilityKey(), out plan) ? plan.Policy : null;
            foreach (Bob bob in level.Bobs)
            {
                bob.CodeControl = true;
                bob.ControlFunc = DoorControl(bob, door, pol);
            }
        }

        static string ItemsKey()
        {
            return string.Join("+", HeldPlatforms.OrderBy(p => p, StringComparer.Ordinal)) + (JetpackHeld ? "+J" : "") + (DoubleJumpHeld ? "+D" : "");
        }

        /// <summary>A death or a miss on the ride: a restart from the last checkpoint passed, or the walk fails by name.</summary>
        static void OnFailure(Level level, Ride r, BotWalk walk, string what, int rec)
        {
            bool died = what == "died";
            int piece = level.LevelPieces.IndexOf(level.CurPiece);
            bool dying = level.Bobs.Any(b => b.Dead || b.Dying);
            ReleaseBobs(level);
            if (died)
            {
                if (walk != null) StatDeaths++; else StatParkedDeaths++;
                if (!dying) foreach (Bob bob in level.Bobs) bob.Die(Bob.BobDeathType.Other, true, true);
            }
            if (walk == null)
            {
                Riding = null;
                if (died) Note("died", piece, null);
                return;
            }
            string key = piece + ":" + rec + ":" + ItemsKey();
            string reason = null;
            if (walk.LastFailure == key)
                reason = "the same " + (died ? "death" : "miss") + " twice (piece " + piece + ", tick " + rec + ", the same items): a replay would repeat it";
            else if (walk.Retries >= walk.MaxRetries)
                reason = walk.MaxRetries + " restarts used";
            walk.LastFailure = key;
            if (reason != null)
            {
                Riding = null;
                EndWalk(died ? "died" : "timeout", reason);
                return;
            }
            walk.Retries++;
            int attempt = walk.Retries + 1, max = walk.MaxRetries + 1;
            if (died)
            {
                // The game's own death and respawn (at the current piece's checkpoint); the recording starts after it.
                Riding = new Ride { State = RideState.AwaitRespawn, Piece = piece, Snap = walk.Snap, DeathStep = Math.Max(1, level.CurPhsxStep) };
                Respawned("restart:died");
                Note("restart", piece, null, "died", attempt, max);
            }
            else
            {
                ReleaseBobs(level);
                level.SetCurrentPiece(piece);
                level.SetToReset = true;
                Riding = new Ride { State = RideState.AwaitRespawn, Piece = piece, Snap = walk.Snap };
                Respawned("restart:missed");
                Note("restart", piece, null, "missed", attempt, max);
            }
        }

        public static void BotStop()
        {
            EndWalk("stopped", null);
            EndRide();
        }

        /// <summary>
        /// Leaves the played level for the API's empty level (a new configure starts with this, so a region that is being
        /// replaced, or a refused one, cannot report anything more).
        /// </summary>
        public static void Stop()
        {
            EndWalk(null, null);
            EndRide();
            Checks.Clear();
            Events.Clear();
            if (PlayGame != null && Tools.CurGameData == PlayGame)
            {
                GameData old = PlayGame;
                Tools.TheGame.MakeEmptyLevel();
                old.Release();
            }
            PlayGame = null;
            PlayLevel = null;
        }

        static void EndRide()
        {
            Cross = null;
            Level.ReplayCodeProbe = false;
            if (Riding == null) return;
            Riding = null;
            ReleaseBobs(PlayLevel);
        }

        /// <summary>Ends the walk (why null: silently, as a new walk replaces it). The ride is left alone.</summary>
        static void EndWalk(string why, string reason)
        {
            if (Bot == null) return;
            BotWalk walk = Bot;
            Bot = null;
            if (walk.Kind == "startdoor") ReleaseBobs(PlayLevel);
            if (why == null) return;
            Level level = PlayLevel;
            int piece = level != null ? level.LevelPieces.IndexOf(level.CurPiece) : -1;
            var sb = new StringBuilder();
            sb.Append("{\"kind\":\"bot\",\"goal\":{\"kind\":").Append(GeneratorApi.JsonStr(walk.Kind)).Append(",\"id\":").Append(GeneratorApi.JsonStr(walk.Id))
              .Append("},\"result\":").Append(GeneratorApi.JsonStr(why)).Append(",\"piece\":").Append(piece).Append(",\"goalPiece\":").Append(walk.Piece)
              .Append(",\"steps\":").Append(walk.Steps).Append(",\"goalTick\":").Append(walk.Tick).Append(",\"retries\":").Append(walk.Retries);
            if (reason != null) sb.Append(",\"exhausted\":true,\"reason\":").Append(GeneratorApi.JsonStr(reason));
            string goal = walk.Kind == "pickup" ? "coin " + walk.Id : walk.Kind == "startdoor" ? "the start door (" + walk.Id + ")" : "the door (" + walk.Id + ")";
            sb.Append(",\"text\":").Append(GeneratorApi.JsonStr("walk to " + goal + ": " + why + (reason != null ? " — " + reason : ""))).Append('}');
            Events.Add(sb.ToString());
        }

        /// <summary>Every physics step of the played level (EmbedDriver).</summary>
        static void Step()
        {
            Level level = PlayLevel;
            if (level == null || Tools.CurLevel != level) return;
            foreach (Bob bob in level.Bobs) ApplyAbilities(bob.MyPhsx);

            // A door event re-arms once no bob is in reach of the door (the start door: armed for the first time so).
            Door door = level.FinalDoor, start = level.StartDoor;
            if (!DoorArmed && door != null && !level.Bobs.Any(b => door.BobInReach(b))) DoorArmed = true;
            // A respawn (the level's step count starts again) puts the bob back at its start: the start door disarms.
            if (level.CurPhsxStep < LastPhsxStep) StartDoorArmed = false;
            LastPhsxStep = level.CurPhsxStep;
            if (!StartDoorArmed && start != null && level.Bobs.Count > 0 && !level.SetToReset && !level.Bobs.Any(b => start.BobInReach(b))) StartDoorArmed = true;
            if (RearmPending && !level.Bobs.Any(b => b.CurInput.xVec.Y > .85f))
            {
                RearmPending = false;
                DoorArmed = true;
                if (start != null) StartDoorArmed = true;
            }
            // The player's Up in reach of a door (as the door's own prompt reads it, xVec.Y > .85). Door.Interact's OnOpen
            // is also hooked, but it is gated by engine state the embedded level does not always have (the keyboard-last
            // flag, a temporary block after the bob is moved), so the embed reads the input itself.
            foreach (Bob bob in level.Bobs)
            {
                if (bob.CompControl || bob.CodeControl || bob.Dead || bob.Dying || bob.CurInput.xVec.Y <= DoorUpThreshold) continue;
                if (door != null && DoorArmed && door.BobInReach(bob)) OnDoor(door, false);
                else if (start != null && StartDoorArmed && start.BobInReach(bob)) OnStartDoor(start);
            }

            StepBot(level, door, start);
        }

        static void StepBot(Level level, Door door, Door start)
        {
            BotWalk walk = Bot;
            if (walk != null)
            {
                walk.Steps++;
                if (walk.Kind == "pickup" && walk.Reached) { EndWalk("reached", null); walk = null; }
                else if (walk.Kind == "startdoor") { StepStartDoor(level, walk, start); return; }
            }
            Ride r = Riding;
            if (r == null) return;
            switch (r.State)
            {
                case RideState.AwaitRespawn:
                    if (r.DeathStep >= 0)
                    {
                        r.WaitSteps++;
                        if (level.CurPhsxStep >= r.DeathStep)
                        {
                            if (r.WaitSteps > BotRespawnWaitTicks && !level.SetToReset)
                            {
                                level.SetCurrentPiece(r.Piece);
                                level.SetToReset = true;
                            }
                            return;
                        }
                        r.DeathStep = -1;
                    }
                    if (level.SetToReset || level.Bobs.Count == 0 || level.Bobs.Any(b => b.Dead || b.Dying)) return;
                    StartRecording(level, r);
                    return;

                case RideState.Playing:
                {
                    if (walk != null) walk.PieceSteps++;
                    int rec = RecTick(level);
                    if (Level.ReplayDeaths > 0) { OnFailure(level, r, walk, "died", Level.ReplayFirstDeathTick); return; }
                    if (walk != null && walk.Kind == "pickup" && r.Piece == walk.Piece && rec > walk.Tick + BotSlackTicks) { OnFailure(level, r, walk, "missed", rec); return; }
                    if (rec < level.CurPiece.PieceLength) return;
                    // The recording is over.
                    if (walk != null && walk.Piece > r.Piece) Ladder(level, r);
                    else if (walk != null && walk.Kind == "portal" && door != null) BeginSteering(level, r, door);
                    else if (walk != null) OnFailure(level, r, walk, "missed", rec);   // its piece's recording ran out without the coin
                    // No walk: the ride plays to its end. The final piece's end is the door (the steering, which stands there
                    // without a door event: only a walk to a side leaves); any other piece's is the ladder (park there).
                    else if (door != null && r.Piece == level.LevelPieces.Count - 1) BeginSteering(level, r, door);
                    else Park(level, r);
                    return;
                }

                case RideState.Steering:
                    if (walk != null) walk.PieceSteps++;
                    if (level.Bobs.Any(b => b.Dying || b.Dead)) { OnFailure(level, r, walk, "died", RecTick(level)); return; }
                    if (walk != null && walk.Kind != "portal") { Park(level, r); return; }
                    if (walk == null)
                    {
                        if (door == null || level.Bobs.Any(b => door.BobInReach(b))) Park(level, r);
                        return;
                    }
                    if (door != null && level.Bobs.Any(b => door.BobInReach(b)))
                    {
                        EndWalk("reached", null);
                        Park(level, r);
                        DoorArmed = true;
                        OnDoor(door, true);
                        return;
                    }
                    if (walk.PieceSteps > level.CurPiece.PieceLength + BotSlackTicks) OnFailure(level, r, walk, "missed", RecTick(level));
                    return;

                case RideState.Crossing:
                    if (walk != null) walk.PieceSteps++;
                    StepCrossing(level, r);
                    return;

                case RideState.Parked:
                    if (!ParkHolds(level, r))
                    {
                        if (level.Bobs.Any(b => b.Dead || b.Dying)) { StatParkedDeaths++; Note("died", r.Piece, null); }
                        Riding = null;
                        if (walk != null) Continue(level, walk);
                        return;
                    }
                    if (walk == null) return;
                    if (walk.Piece > r.Piece) Ladder(level, r);
                    else if (walk.Kind == "portal" && door != null) BeginSteering(level, r, door);
                    else OnFailure(level, r, walk, "missed", RecTick(level));
                    return;
            }
        }

        /// <summary>The way back (P3a): the respawn at the level's start puts the bob at the start door.</summary>
        static void StepStartDoor(Level level, BotWalk walk, Door start)
        {
            if (!walk.Armed)
            {
                if (level.SetToReset || level.Bobs.Count == 0) return;
                walk.Armed = true;
                return;
            }
            if (level.Bobs.Any(b => b.Dying || b.Dead)) { EndWalk("died", null); return; }
            if (start != null && level.Bobs.Any(b => start.BobInReach(b)))
            {
                EndWalk("reached", null);
                StartDoorArmed = false;
                Events.Add("{\"kind\":\"door\",\"door\":\"start\",\"bot\":true}");
                return;
            }
            if (walk.Steps > BotSlackTicks) EndWalk("timeout", null);
        }

        class EmbedDriver : GameObject
        {
            public EmbedDriver() { PreventRelease = true; PauseOnPause = true; }
            protected override void MyPhsxStep() { Step(); }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Outbound

        /// <summary>The events since the last call, as a JSON array ("" when none).</summary>
        public static string TakeEvents()
        {
            if (Events.Count == 0) return "";
            string s = "[" + string.Join(",", Events) + "]";
            Events.Clear();
            return s;
        }

        public static string StatusJson()
        {
            Level level = PlayLevel;
            var sb = new StringBuilder();
            sb.Append("{\"active\":").Append(Active ? "true" : "false")
              .Append(",\"configures\":").Append(Configures)
              .Append(",\"playing\":").Append(level != null && Tools.CurLevel == level ? "true" : "false");
            if (level != null)
            {
                sb.Append(",\"piece\":").Append(level.LevelPieces.IndexOf(level.CurPiece))
                  .Append(",\"tick\":").Append(level.CurPhsxStep);
                if (level.MainCamera != null) sb.Append(",\"camera\":").Append(Vec(level.MainCamera.Data.Position));
                Bob bob = level.Bobs.Count > 0 ? level.Bobs[0] : null;
                if (bob != null)
                    sb.Append(",\"bob\":{\"pos\":[").Append(bob.Core.Data.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(bob.Core.Data.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append("],\"dead\":")
                      .Append(bob.Dead || bob.Dying ? "true" : "false").Append(",\"compControl\":").Append(bob.CompControl ? "true" : "false")
                      .Append(",\"jetpack\":").Append(bob.MyPhsx is BobPhsxNormal && ((BobPhsxNormal)bob.MyPhsx).JetPack ? "true" : "false").Append('}');
                if (level.StartDoor != null)
                    sb.Append(",\"startDoor\":[").Append(level.StartDoor.Pos.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(level.StartDoor.Pos.Y.ToString("R", CultureInfo.InvariantCulture)).Append("],\"startDoorArmed\":")
                      .Append(StartDoorArmed ? "true" : "false").Append(",\"startDoorLocked\":").Append(level.StartDoor.Locked ? "true" : "false");
                sb.Append(",\"doorArmed\":").Append(DoorArmed ? "true" : "false");
                if (level.FinalDoor != null) sb.Append(",\"doorLocked\":").Append(level.FinalDoor.Locked ? "true" : "false");
                sb.Append(",\"playMode\":").Append(level.PlayMode);
                if (bob != null)
                    sb.Append(",\"input\":{\"y\":").Append(bob.CurInput.xVec.Y.ToString("R", CultureInfo.InvariantCulture))
                      .Append(",\"keyboardUsedLast\":").Append(bob.GetPlayerData() != null && bob.GetPlayerData().KeyboardUsedLast ? "true" : "false").Append('}');
                if (level.FinalDoor != null)
                    sb.Append(",\"door\":[").Append(level.FinalDoor.Pos.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(level.FinalDoor.Pos.Y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                sb.Append(",\"checks\":[");
                for (int i = 0; i < Checks.Count; i++)
                {
                    var c = Checks[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(GeneratorApi.JsonStr(c.Name)).Append(",\"sent\":").Append(c.Sent ? "true" : "false")
                      .Append(",\"checked\":").Append(c.Checked ? "true" : "false")
                      .Append(",\"piece\":").Append(c.Piece).Append(",\"tick\":").Append(c.Tick).Append('}');
                }
                sb.Append(']');
                sb.Append(",\"bot\":");
                if (Bot == null) sb.Append("null");
                else sb.Append("{\"kind\":").Append(GeneratorApi.JsonStr(Bot.Kind)).Append(",\"id\":").Append(GeneratorApi.JsonStr(Bot.Id))
                       .Append(",\"armed\":").Append(Bot.Armed ? "true" : "false").Append(",\"steps\":").Append(Bot.Steps)
                       .Append(",\"retries\":").Append(Bot.Retries).Append('}');
                sb.Append(",\"ride\":");
                if (Riding == null) sb.Append("null");
                else sb.Append("{\"state\":").Append(GeneratorApi.JsonStr(Riding.State.ToString())).Append(",\"piece\":").Append(Riding.Piece)
                       .Append(",\"rec\":").Append(RecTick(level)).Append('}');
                sb.Append(",\"botStats\":{\"walks\":").Append(StatWalks).Append(",\"continued\":").Append(StatContinued)
                  .Append(",\"deaths\":").Append(StatDeaths).Append(",\"parkedDeaths\":").Append(StatParkedDeaths).Append(",\"ladder\":").Append(LadderStatusJson()).Append(",\"respawns\":{");
                bool firstKind = true;
                foreach (var kv in StatRespawns) { if (!firstKind) sb.Append(','); firstKind = false; sb.Append(GeneratorApi.JsonStr(kv.Key)).Append(':').Append(kv.Value); }
                sb.Append("}}");
                sb.Append(",\"items\":").Append(ItemsJson());
                sb.Append(",\"look\":{\"texture\":").Append(CheckTex != null ? "true" : "false").Append(",\"size\":")
                  .Append(CheckTexSize.ToString("R", CultureInfo.InvariantCulture)).Append(",\"drawnUnchecked\":").Append(DrawnUnchecked)
                  .Append(",\"drawnChecked\":").Append(DrawnChecked).Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

#if BROWSER
        [JSExport] public static string PlayJson(string json) { return Play(json); }
        [JSExport] public static string SetItemsJson(string json) { return SetItems(json); }
        [JSExport] public static string BotWalkToJson(string json) { return BotWalkTo(json); }
        [JSExport] public static void BotStopNow() { BotStop(); }
        [JSExport] public static void StopNow() { Stop(); }
        [JSExport] public static string TakeEventsJson() { return TakeEvents(); }
        [JSExport] public static string Status() { return StatusJson(); }
        [JSExport] public static string PlanLaddersJson() { return PlanLadders(); }
        [JSExport] public static void RearmDoorsNow() { RearmDoors(); }
        [JSExport] public static string SetCheckedJson(string json) { return SetChecked(json); }
        [JSExport] public static string SetCheckLookJson(string json) { return SetCheckLook(json); }
#endif
    }

    public static partial class GeneratorApi
    {
        /// <summary>True while Generate runs (its replays make bobs the embed's item hooks must leave alone).</summary>
        public static bool Generating;

        /// <summary>
        /// The embedded page's level: made from the request by the generation's path (BuildSeedData + MakeLevel) and set up
        /// as NormalGameData's own level loading does (MakeBobs, PostMake, the final reset), synchronously, with the request's
        /// statics (and NoDoublePaths pinned) in force while it is made.
        /// </summary>
        internal static Level MakePlayableLevel(string requestJson, out GameData game)
        {
            Args a = ParseArgs(requestJson);
            if (!a.V2) throw new ArgumentException("embedded play needs a ck-level/2 request");
            if (a.RerollSearch) throw new ArgumentException("embedded play needs the resolved request (rerolls), not a reroll search");

            List<KeyValuePair<System.Reflection.FieldInfo, object>> holdStatics = null;
            bool holdNoDouble = LevelSeedData.NoDoublePaths;
            GameData old = Tools.CurGameData;
            try
            {
                LevelSeedData.NoDoublePaths = true;
                if (a.Statics != null) holdStatics = GeneratorSettings.ApplyStatics(a.Statics.Value);
                PlatformSuppression.Disable();

                LevelGeometry geometry;
                LevelSeedData data = BuildSeedData(a, new List<string>(), out geometry);

                if (PlayerManager.GetNumPlayers() == 0) PlayerManager.Players[0].Exists = true;

                var g = new NormalGameData();
                g.Init();
                g.DefaultHeroType = data.DefaultHeroType;
                g.DefaultHeroType2 = data.DefaultHeroType2;
                Tools.CurGameData = g;

                Level level = data.MakeLevel(true, g);
                if (level == null) throw new InvalidOperationException("MakeLevel returned null");
                if (level.ReturnedEarly) throw new InvalidOperationException("MakeLevel returned early");

                g.MyLevel = level;
                level.MyGame = g;
                Tools.CurLevel = level;
                level.CanWatchComputer = level.CanWatchReplay = true;
                g.MakeBobs(level);
                g.SetAdditionalBobParameters(level.Bobs);
                level.Name = data.Name;
                g.SetAdditionalLevelParameters();
                if (data.PostMake != null) data.PostMake(level);
                EmbedMode.PostMake(level);
                level.PlayMode = 0;
                level.ResetAll(false, true);
                g.Loading = false;
                data.MyGame = g;

                if (old != null && old != g) old.Release();
                game = g;
                return level;
            }
            catch
            {
                Tools.CurGameData = old;
                if (old != null) Tools.CurLevel = old.MyLevel;
                throw;
            }
            finally
            {
                if (holdStatics != null) foreach (var kv in holdStatics) kv.Key.SetValue(null, kv.Value);
                LevelSeedData.NoDoublePaths = holdNoDouble;
            }
        }
    }
}
