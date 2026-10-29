#if BROWSER
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;

using CloudberryKingdom.Bobs;

namespace CloudberryKingdom
{
    /// <summary>
    /// A read-only view of what the player is looking at, for the page and its test drivers:
    /// the current game type, the frame and physics counters, every bob in the current level
    /// (position, velocity, dying/dead) and the items of each visible menu with the selected one.
    /// The page calls it as window.cloudberry.state(); it changes nothing in the game.
    /// </summary>
    public static partial class BrowserPlayState
    {
        [JSExport]
        public static string StateJson()
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream))
                {
                    w.WriteStartObject();
                    var game = Tools.TheGame;
                    w.WriteNumber("drawCount", game != null ? game.DrawCount : -1);
                    w.WriteNumber("phsxCount", game != null ? game.PhsxCount : -1);
                    var data = Tools.CurGameData;
                    w.WriteString("gameData", data != null ? data.GetType().Name : null);

                    var level = Tools.CurLevel;
                    w.WriteStartArray("bobs");
                    if (level != null && level.Bobs != null)
                    {
                        foreach (Bob bob in level.Bobs)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("player", (int)bob.MyPlayerIndex);
                            WriteVector(w, "pos", bob.Core.Data.Position);
                            WriteVector(w, "vel", bob.Core.Data.Velocity);
                            w.WriteBoolean("dying", bob.Dying);
                            w.WriteBoolean("dead", bob.Dead);
                            w.WriteBoolean("computer", bob.CompControl);
                            w.WriteEndObject();
                        }
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("menus");
                    if (data != null)
                    {
                        foreach (GameObject obj in data.MyGameObjects)
                        {
                            var panel = obj as GUI_Panel;
                            if (panel == null || !panel.Active || panel.Hid || panel.MyMenu == null || panel.MyMenu.Items == null) continue;
                            w.WriteStartObject();
                            w.WriteString("panel", panel.GetType().Name);
                            w.WriteNumber("selected", panel.MyMenu.CurIndex);
                            w.WriteStartArray("items");
                            foreach (MenuItem item in panel.MyMenu.Items)
                                w.WriteStringValue(item.MyText != null ? item.MyText.FirstString() : item.Name);
                            w.WriteEndArray();
                            w.WriteEndObject();
                        }
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        static void WriteVector(Utf8JsonWriter w, string name, Microsoft.Xna.Framework.Vector2 v)
        {
            w.WriteStartArray(name);
            w.WriteNumberValue(float.IsFinite(v.X) ? v.X : 0);
            w.WriteNumberValue(float.IsFinite(v.Y) ? v.Y : 0);
            w.WriteEndArray();
        }
    }
}
#endif
