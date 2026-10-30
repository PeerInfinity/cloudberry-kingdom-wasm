#if !BROWSER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudberryKingdom
{
    /// <summary>
    /// Native only (build/tools/native-dev.sh): runs an embedded-mode script without a page, stepping the game's physics
    /// directly (no drawing). --embed-test script.json --out log.json. The script is a JSON array of steps:
    ///   {"do":"configure", "request":{…ck-level/2…}, "checks":[coin id…]}   generate the document, then Play it
    ///   {"do":"items", "platforms":[…], "jetpack":bool}
    ///   {"do":"bot", "goal":{"kind":"pickup"|"portal","id":…}, "options":{…}}
    ///   {"do":"run", "steps":n} | {"do":"until", "event":"location"|"door"|"bot", "max":n}
    ///   {"do":"status"} | {"do":"stop"} | {"do":"checked", "names":[…]} | {"do":"rearm"}
    /// Every step's result and every event go into the log (a development instrument; the page is what ships).
    /// </summary>
    public static class EmbedTest
    {
        public static string Script, Out;

        public static void Run()
        {
            var log = new JsonArray();
            using (var script = JsonDocument.Parse(File.ReadAllText(Script)))
            {
                foreach (JsonElement step in script.RootElement.EnumerateArray())
                {
                    string what = step.GetProperty("do").GetString();
                    var entry = new JsonObject { ["do"] = what };
                    try
                    {
                        switch (what)
                        {
                            case "configure": entry["result"] = JsonNode.Parse(Configure(step)); break;
                            case "items": entry["result"] = JsonNode.Parse(EmbedMode.SetItems(step.GetRawText())); break;
                            case "bot": entry["result"] = JsonNode.Parse(EmbedMode.BotWalkTo(step.GetRawText())); break;
                            case "status": entry["result"] = JsonNode.Parse(EmbedMode.StatusJson()); break;
                            case "checked": entry["result"] = JsonNode.Parse(EmbedMode.SetChecked(step.GetProperty("names").GetRawText())); break;
                            case "rearm": EmbedMode.RearmDoors(); break;
                            case "stop": EmbedMode.Stop(); break;
                            case "run":
                            {
                                int n = step.GetProperty("steps").GetInt32();
                                entry["events"] = Steps(n, null, out int ran);
                                entry["ran"] = ran;
                                break;
                            }
                            case "until":
                            {
                                int n = step.GetProperty("max").GetInt32();
                                entry["events"] = Steps(n, step.GetProperty("event").GetString(), out int ran);
                                entry["ran"] = ran;
                                break;
                            }
                            default: throw new ArgumentException("unknown step " + what);
                        }
                    }
                    catch (Exception e) { entry["error"] = e.ToString(); }
                    entry["status"] = JsonNode.Parse(EmbedMode.StatusJson());
                    log.Add(entry);
                }
            }
            File.WriteAllText(Out, log.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("[embed-test] wrote " + Out);
        }

        static JsonArray Steps(int n, string until, out int ran)
        {
            var events = new JsonArray();
            for (ran = 0; ran < n; ran++)
            {
                Tools.CurGameData.PhsxStep();
                string ev = EmbedMode.TakeEvents();
                if (ev.Length == 0) continue;
                bool stop = false;
                foreach (JsonNode e in JsonNode.Parse(ev).AsArray())
                {
                    JsonNode c = e.DeepClone();
                    c["atStep"] = ran;
                    events.Add(c);
                    if (until != null && (string)e["kind"] == until) stop = true;
                }
                if (stop) { ran++; break; }
            }
            return events;
        }

        /// <summary>What the page's configure does: the document, then Play with the check coins taken from it.</summary>
        static string Configure(JsonElement step)
        {
            string request = step.GetProperty("request").GetRawText();
            string docText = GeneratorApi.Generate(request);
            JsonNode doc = JsonNode.Parse(docText);
            if (doc["error"] != null) return docText;
            var checkIds = step.TryGetProperty("checks", out JsonElement ce) ? ce.EnumerateArray().Select(x => x.GetInt32()).ToList() : new List<int>();
            var checks = new JsonArray();
            foreach (JsonNode coin in doc["coins"]["list"].AsArray())
            {
                int id = (int)coin["id"];
                if (!checkIds.Contains(id)) continue;
                checks.Add(new JsonObject
                {
                    ["name"] = "coin" + id, ["id"] = id, ["pos"] = doc["objects"].AsArray().First(o => (int)o["id"] == id)["pos"].DeepClone(), ["piece"] = coin["witness"] == null ? (int)coin["piece"] : (int)coin["witness"]["replayOfPiece"],
                    ["tick"] = coin["witness"] == null ? -1 : (int)coin["witness"]["tick"],
                });
            }
            var play = new JsonObject
            {
                ["generate"] = JsonNode.Parse(request), ["checks"] = checks, ["coinCount"] = (int)doc["coins"]["count"],
            };
            if (step.TryGetProperty("camStartFix", out JsonElement cf)) play["camStartFix"] = cf.GetBoolean();
            return EmbedMode.Play(play.ToJsonString());
        }
    }
}
#endif
