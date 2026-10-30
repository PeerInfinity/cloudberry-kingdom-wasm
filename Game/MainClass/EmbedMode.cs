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
    ///   - Up at the end door queues a "door" event (once per arrival at the door) — where the exit menu will hang;
    ///   - held items: platform items through PlatformSuppression, the Jetpack ability on the level's hero (zeroed
    ///     when the level's hero has a jetpack and the Jetpack item is not held);
    ///   - the bot (BotWalkTo): respawn at the goal's piece checkpoint and play that piece's recording (the player's own
    ///     bob under the recording's input, CompControl as the computer's replay, the snap off by default) until the coin
    ///     is collected or the door is in reach; a death, or running past the goal's tick, ends it as failed.
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
        }

        class BotWalk
        {
            public string Kind, Id;
            public int Piece, Tick, Limit, Steps;
            public CheckCoin Check;
            public bool Snap, Armed, Reached, Steering;
        }

        static readonly List<CheckCoin> Checks = new List<CheckCoin>();
        static readonly List<string> Events = new List<string>();
        static Level PlayLevel;
        static GameData PlayGame;
        static bool LevelHeroJetpack, JetpackHeld;
        static List<string> HeldPlatforms = new List<string>();
        static bool DoorArmed = true;
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
                    foreach (var c in checks) c.Sent = checkedNames.Contains(c.Name);

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
                    DoorArmed = true;
                    Configures++;

                    BobPhsxNormal hero = level.DefaultHeroType as BobPhsxNormal;
                    LevelHeroJetpack = hero != null && hero.JetPack;
                    ApplyHeld();

                    var sb = new StringBuilder();
                    sb.Append("{\"ok\":true,\"configures\":").Append(Configures)
                      .Append(",\"pieces\":").Append(level.LevelPieces.Count)
                      .Append(",\"coins\":").Append(coins)
                      .Append(",\"checks\":").Append(Checks.Count)
                      .Append(",\"heroJetpack\":").Append(LevelHeroJetpack ? "true" : "false")
                      .Append(",\"camStartFix\":").Append(CamStartFix ? "true" : "false");
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

        /// <summary>json: {platforms: [item names], jetpack: bool}. Returns the state.</summary>
        public static string SetItems(string json)
        {
            try
            {
                var platforms = new List<string>();
                bool jetpack = false;
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement e;
                    if (doc.RootElement.TryGetProperty("platforms", out e)) foreach (var p in e.EnumerateArray()) platforms.Add(p.GetString());
                    if (doc.RootElement.TryGetProperty("jetpack", out e)) jetpack = e.GetBoolean();
                }
                foreach (string p in platforms)
                    if (PlatformSuppression.ItemIndex(p) < 0) throw new ArgumentException("unknown platform item '" + p + "'");
                HeldPlatforms = platforms;
                JetpackHeld = jetpack;
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
                + ",\"heroJetpack\":" + (LevelHeroJetpack ? "true" : "false") + ",\"jetpackOn\":" + (JetpackOn ? "true" : "false") + "}";
        }

        static bool JetpackOn { get { return LevelHeroJetpack && JetpackHeld; } }

        static void ApplyHeld()
        {
            PlatformSuppression.SetHeld(HeldPlatforms);
            if (PlayLevel != null && PlayLevel.Bobs != null)
                foreach (Bob bob in PlayLevel.Bobs) ApplyAbilities(bob.MyPhsx);
        }

        /// <summary>The Jetpack item on a bob of the played level (Bob.SetHeroPhsx calls it for every new phsx; not during a generation).</summary>
        public static void ApplyAbilities(BobPhsx phsx)
        {
            if (!Active || PlayLevel == null || GeneratorApi.Generating) return;
            BobPhsxNormal normal = phsx as BobPhsxNormal;
            if (normal != null && LevelHeroJetpack) normal.JetPack = JetpackHeld;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Coins and the door

        public static void OnCoin(Coin coin, Bob bob)
        {
            if (!Active || coin.Core.MyLevel != PlayLevel) return;
            foreach (var c in Checks)
            {
                if (c.StartPos != coin.Core.StartData.Position) continue;
                if (Bot != null && Bot.Check == c) Bot.Reached = true;
                if (c.Sent) return;
                c.Sent = true;
                Events.Add("{\"kind\":\"location\",\"name\":" + GeneratorApi.JsonStr(c.Name) + ",\"bot\":" + (Bot != null ? "true" : "false")
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
            Events.Add("{\"kind\":\"door\",\"bot\":" + (bot ? "true" : "false") + "}");
        }

        // -------------------------------------------------------------------------------------------------------------
        // The bot

        /// <summary>json: {goal: {kind: 'pickup'|'portal', id}, options: {snap}}. Returns {ok, piece, tick} or {ok:false, error}.</summary>
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
                    }
                }
                if (walk.Kind == "pickup")
                {
                    walk.Check = Checks.FirstOrDefault(c => c.Name == walk.Id);
                    if (walk.Check == null) throw new ArgumentException("no check coin '" + walk.Id + "'");
                    walk.Piece = walk.Check.Piece;
                    walk.Tick = walk.Check.Tick;
                    walk.Limit = walk.Tick + BotSlackTicks;
                }
                else if (walk.Kind == "portal")
                {
                    walk.Piece = level.LevelPieces.Count - 1;
                    walk.Tick = -1;
                    walk.Limit = level.LevelPieces[walk.Piece].PieceLength + BotSlackTicks;
                }
                else throw new ArgumentException("unknown goal kind '" + walk.Kind + "'");

                StopBot(null);
                // Respawn at the goal's piece checkpoint (the piece's start, where its recording starts).
                level.SetCurrentPiece(walk.Piece);
                level.SetToReset = true;
                Bot = walk;
                return "{\"ok\":true,\"piece\":" + walk.Piece + ",\"tick\":" + walk.Tick + "}";
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":" + GeneratorApi.JsonStr(e.GetType().Name + ": " + e.Message) + "}";
            }
        }

        public static void BotStop() { StopBot("stopped"); }

        /// <summary>
        /// Leaves the played level for the API's empty level (a new configure starts with this, so a region that is being
        /// replaced, or a refused one, cannot report anything more).
        /// </summary>
        public static void Stop()
        {
            StopBot(null);
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

        static void StopBot(string why)
        {
            if (Bot == null) return;
            BotWalk walk = Bot;
            Bot = null;
            Level.ReplayProbe = false;
            Level.ReplaySnapDisabled = false;
            if (PlayLevel != null)
                foreach (Bob bob in PlayLevel.Bobs) { bob.CompControl = false; bob.MyRecord = null; bob.CodeControl = false; bob.ControlFunc = null; }
            if (why != null)
                Events.Add("{\"kind\":\"bot\",\"goal\":{\"kind\":" + GeneratorApi.JsonStr(walk.Kind) + ",\"id\":" + GeneratorApi.JsonStr(walk.Id)
                    + "},\"result\":" + GeneratorApi.JsonStr(why) + ",\"piece\":" + walk.Piece + ",\"steps\":" + walk.Steps + ",\"goalTick\":" + walk.Tick + "}");
        }

        /// <summary>Every physics step of the played level (EmbedDriver).</summary>
        static void Step()
        {
            Level level = PlayLevel;
            if (level == null || Tools.CurLevel != level) return;
            foreach (Bob bob in level.Bobs) ApplyAbilities(bob.MyPhsx);

            // The door event re-arms once no bob is in reach of the door.
            Door door = level.FinalDoor;
            if (!DoorArmed && door != null && !level.Bobs.Any(b => door.BobInReach(b))) DoorArmed = true;

            BotWalk walk = Bot;
            if (walk == null) return;
            if (!walk.Armed)
            {
                if (level.SetToReset || level.Bobs.Count == 0) return;
                // The respawn happened: the player's bob plays the piece's recording, as the computer's replay does.
                foreach (Bob bob in level.Bobs)
                {
                    bob.CompControl = true;
                    bob.MyRecord = level.CurPiece.Recording[bob.MyPieceIndex];
                    bob.IndexOffset = 0;
                }
                Level.ReplayProbe = true;           // a death is counted (and ends the walk), not acted on
                Level.ReplayDeaths = 0;
                Level.ReplaySnapDisabled = !walk.Snap;
                walk.Armed = true;
                return;
            }
            walk.Steps++;
            if (Level.ReplayDeaths > 0)
            {
                StopBot("died");
                foreach (Bob bob in level.Bobs) bob.Die(Bob.BobDeathType.Other, true, true);
                return;
            }
            if (walk.Kind == "pickup" && walk.Reached) { StopBot("reached"); return; }
            if (walk.Kind == "portal" && !walk.Steering && door != null && level.Bobs.Count > 0
                && level.CurPhsxStep - level.Bobs[0].IndexOffset >= level.CurPiece.PieceLength)
            {
                // The final piece's recording is over and the door is not in reach yet. After its recording the engine
                // feeds the computer "right + jump", which can carry the bob over a low door; walk to the door instead.
                walk.Steering = true;
                foreach (Bob bob in level.Bobs)
                {
                    Bob b = bob;
                    b.CompControl = false;
                    b.MyRecord = null;
                    b.CodeControl = true;
                    b.ControlFunc = count =>
                    {
                        float dx = door.Pos.X - b.Core.Data.Position.X;
                        b.CurInput.xVec = new Vector2(Math.Abs(dx) < DoorSteerDeadZone ? 0 : Math.Sign(dx), 0);
                    };
                }
            }
            if (walk.Steering && level.Bobs.Any(b => b.Dying || b.Dead)) { StopBot("died"); return; }
            if (walk.Kind == "portal" && door != null && level.Bobs.Any(b => door.BobInReach(b)))
            {
                StopBot("reached");
                DoorArmed = true;
                OnDoor(door, true);
                return;
            }
            if (walk.Steps > walk.Limit) StopBot("timeout");
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
                if (level.FinalDoor != null)
                    sb.Append(",\"door\":[").Append(level.FinalDoor.Pos.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(level.FinalDoor.Pos.Y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                sb.Append(",\"checks\":[");
                for (int i = 0; i < Checks.Count; i++)
                {
                    var c = Checks[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(GeneratorApi.JsonStr(c.Name)).Append(",\"sent\":").Append(c.Sent ? "true" : "false")
                      .Append(",\"piece\":").Append(c.Piece).Append(",\"tick\":").Append(c.Tick).Append('}');
                }
                sb.Append(']');
                sb.Append(",\"bot\":");
                if (Bot == null) sb.Append("null");
                else sb.Append("{\"kind\":").Append(GeneratorApi.JsonStr(Bot.Kind)).Append(",\"id\":").Append(GeneratorApi.JsonStr(Bot.Id))
                       .Append(",\"armed\":").Append(Bot.Armed ? "true" : "false").Append(",\"steps\":").Append(Bot.Steps).Append('}');
                sb.Append(",\"items\":").Append(ItemsJson());
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
