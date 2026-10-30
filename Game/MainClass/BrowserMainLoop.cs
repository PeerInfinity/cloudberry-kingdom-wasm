#if BROWSER
using System;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

using Microsoft.Xna.Framework;

namespace CloudberryKingdom
{
    /// <summary>
    /// An opt-in main loop driven by the page (environment CK_MAIN_LOOP=js). FNA's own Emscripten loop calls
    /// emscripten_set_main_loop(..., simulate_infinite_loop: 1), which unwinds out of Main by throwing; every managed
    /// throw after that point has been fatal in this single-threaded runtime (an unwinder assertion or "memory access
    /// out of bounds"). Here Main returns normally instead, and the page calls Frame() from requestAnimationFrame;
    /// Frame() is what FNA's loop callback does (Game.RunOneFrame), after the part of Game.Run that precedes the loop.
    /// </summary>
    public static partial class BrowserMainLoop
    {
        public const string EnvironmentVariable = "CK_MAIN_LOOP";
        public const string JsLoopValue = "js";

        static Game TheGame;
        static int FramesRun, ThrowProbesCaught;

        public static bool Requested()
        {
            return Environment.GetEnvironmentVariable(EnvironmentVariable) == JsLoopValue;
        }

        /// <summary>Keeps the game for Frame() and registers it with the platform as Game.Run does before its loop.</summary>
        public static void Start(Game game)
        {
            TheGame = game;
            // Game.Run: DoInitialize (RunOneFrame does it on the first frame), BeginRun, BeforeLoop (FNAPlatform.RegisterGame).
            typeof(Game).GetMethod("BeforeLoop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(game, null);
            Console.WriteLine("[main-loop] js: Main returns; the page steps frames");
        }

        /// <summary>Physics steps per drawn frame (CloudberryKingdomGame.Draw multiplies its own count by this).</summary>
        public static int StepsPerDraw = 1;

        [JSExport]
        public static bool Frame()
        {
            return FrameSteps(1);
        }

        /// <summary>One drawn frame that advances the game by the given number of physics steps (the page's pacing).</summary>
        [JSExport]
        public static bool FrameSteps(int steps)
        {
            if (TheGame == null) return false;
            StepsPerDraw = steps;
            TheGame.RunOneFrame();
            FramesRun++;
            return true;
        }

        /// <summary>The game's physics step count (for the page's FPS display).</summary>
        [JSExport]
        public static int PhysicsCount() { return Tools.TheGame != null ? Tools.TheGame.PhsxCount : 0; }

        [JSExport]
        public static int FrameCount() { return FramesRun; }

        /// <summary>Queues a throw that is caught inside the next game frame (a probe: does a caught throw survive?).</summary>
        [JSExport]
        public static void QueueThrowProbe()
        {
            Tools.BrowserBackgroundQueue.Add(() =>
            {
                try { throw new InvalidOperationException("throw probe"); }
                catch (InvalidOperationException) { ThrowProbesCaught++; }
            });
        }

        [JSExport]
        public static int ThrowProbeCount() { return ThrowProbesCaught; }
    }
}
#endif
