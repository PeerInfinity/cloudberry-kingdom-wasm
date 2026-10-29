using System;

namespace CloudberryKingdom
{
    static class MainClass
    {
#if WINDOWS
        [STAThread]
#endif
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		static void Main(string[] args)
        {
            Tools.Write("Main Constructor");

            args = GeneratorApi.ProcessArgs(args);
            CloudberryKingdomGame.ProcessArgs(args);

            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

#if BROWSER
            // BROWSER: report each throw at the throw site (before Mono's wasm unwinder runs),
            // without touching StackTrace. The first BrowserThrowReportLimit throws are printed.
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (BrowserThrowCount++ < BrowserThrowReportLimit)
                    Console.WriteLine("[first-chance #" + BrowserThrowCount + "] " + e.Exception.GetType().FullName + ": " + e.Exception.Message);
            };
#endif

#if DEBUG && WINDOWS && !SDL2
            AppDomain.CurrentDomain.FirstChanceException += new EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs>(CurrentDomain_FirstChanceException);
#endif

#if BROWSER && GAME
            if (BrowserMainLoop.Requested())
            {
                BrowserMainLoop.Start(new XnaGameClass());
                return;
            }
#endif

#if GAME
            using (XnaGameClass game = new XnaGameClass())
#else
            using (Game_Editor game = new Game_Editor())            
#endif
            {
                game.Run();
            }
        }

#if DEBUG && WINDOWS
        static void CurrentDomain_FirstChanceException(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
			Tools.Log("Exception on MainClass.CurrentDomain_FirstChanceException\n" + Tools.ExceptionStr(e.Exception));
        }
#endif

#if BROWSER
        static int BrowserThrowCount = 0;
        const int BrowserThrowReportLimit = 40;
#endif
        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
			Tools.Log(string.Format("{0}\nExceptionObject:\n{1}\nsender:\n{2}", "Exception on MainClass.CurrentDomain_UnhandledException",
				e.ExceptionObject == null ? "" : e.ExceptionObject.ToString(),
				sender == null ? "" : sender.ToString()));
        }
    }
}
