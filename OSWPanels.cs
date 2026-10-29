// =============================================================================
// OSWPanels.cs  —  part of OSWTools.dll
// -----------------------------------------------------------------------------
// The C# side of the OSW panel library. Everything goes through one
// Streamer.bot action ("OSW Panels") so users only import one thing.
//
// [FILES] Everything big lives in OSWData as files. Streamer.bot's globals
// only hold two short, temporary values (the library's version and folder).
//
//   op = "init"         Unpack the library + style picker to
//                       OSWData/osw-panels/ and publish where it is
//                       (OSW_Lib_Path) and which version (OSW_Lib_Version).
//                       Safe to run any number of times.
//   op = "save"         Save looks from the picker to
//                       OSWData/<widget folder>/look.json, then push them live.
//   op = "getLook"      An overlay (or the picker) asking for a widget's look.
//                       The answer comes back as an OSW_Style broadcast.
//   op = "register"     An overlay saying "I'm the InfoPanel and I live at this
//                       URL". Kept in OSWData/osw-panels/widgets.json.
//   op = "listWidgets"  The picker asking which widgets exist. The answer
//                       comes back as an OSW_Widgets broadcast.
//   op = "getFile"      An overlay asking for one of its widget's shareable
//                       data files (e.g. the Info Panel's snapshot.json). The
//                       answer comes back as an OSW_File broadcast.
//   op = "openPicker"   Write the picker's config and open it in the browser
//                       (or call OpenPicker() straight from a settings button).
//
// WHY BROADCASTS? Running an action over the WebSocket doesn't return data,
// so the action answers with WebsocketBroadcastJson. Overlays already listen
// for those (General.Custom) and ignore answers meant for other widgets.
//
// The contract (names, formats) is in INTEGRATION.md.
//
// SETUP IN THE PROJECT
//   Put osw-panels.css, osw-panels.js and osw-style-picker.html in a folder
//   named "Panels" and set each file's Build Action to "Embedded Resource".
//   This code finds them by the ".Panels." part of their resource name, so it
//   doesn't depend on your project's namespace.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Streamer.bot.Plugin.Interface;

namespace OSWTools
{
    public static class OSWPanels
    {
        // ---- Names shared with the overlays and the picker (INTEGRATION.md) ----
        public const string GlobalVersion = "OSW_Lib_Version";   // temporary: 10-character fingerprint
        public const string GlobalPath = "OSW_Lib_Path";         // [FILES] temporary: "D:/Streamer.bot/OSWData/osw-panels/"
        public const string StyleEvent = "OSW_Style";            // broadcast: a widget's look (saved, or asked for)
        public const string WidgetsEvent = "OSW_Widgets";        // [FILES] broadcast: answer to listWidgets
        public const string FileEvent = "OSW_File";              // broadcast: answer to getFile
        public const string PickerFile = "osw-style-picker.html";
        public const string LookFile = "look.json";              // OSWData/<widget folder>/look.json
        public const string WidgetsFile = "widgets.json";        // [FILES] OSWData/osw-panels/widgets.json

        // Which OSWData folder each widget already uses for its settings.json.
        // The look is saved next to it. A widget missing from this list uses
        // its own id as the folder name (OSWData/DonorTicker/).
        // To add a widget: one line, e.g.  { "DonorTicker", "DONOR" },
        static readonly Dictionary<string, string> WidgetFolders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "InfoPanel", "INFO" },
            };

        // Data files an overlay may ask for with op "getFile". Only these: the
        // answer is broadcast to every WebSocket client, and a widget folder
        // also holds files that must stay private (settings.json has API keys).
        // To share another file, add its name (without ".json") here.
        static readonly HashSet<string> ShareableFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "snapshot",
        };

        // OSWData sits in the Streamer.bot folder. BaseDirectory is the folder
        // Streamer.bot.exe runs from, wherever the user installed it.
        public static string OSWDataPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OSWData");
        public static string PanelsPath => Path.Combine(OSWDataPath, "osw-panels");
        static string WidgetsPath => Path.Combine(PanelsPath, WidgetsFile);

        // Widget ids end up inside file paths, so only allow a safe set of
        // characters: letters, digits and underscore.
        static readonly Regex SafeId = new Regex("^[A-Za-z0-9_]{1,40}$");

        // Several overlays can call at once; the lock makes the second caller
        // wait instead of writing the same files at the same time.
        static readonly object Gate = new object();
        static string _version;                                   // null until Initialize has run

        public static string LibraryVersion => _version;

        // =====================================================================
        // ENTRY POINT for the "OSW Panels" action:
        //     return OSWTools.OSWPanels.Handle(CPH, args);
        // =====================================================================
        public static bool Handle(IInlineInvokeProxy cph, IDictionary<string, object> args)
        {
            string op = (Arg(args, "op") ?? "init").Trim().ToLowerInvariant();
            try
            {
                switch (op)
                {
                    case "init": return Initialize(cph);
                    case "save": return SaveStyles(cph, Arg(args, "styles"));
                    case "getlook": return SendLook(cph, Arg(args, "widget"), Arg(args, "requestId"));
                    case "register":
                        return RegisterWidget(cph, Arg(args, "widget"), Arg(args, "url"), Arg(args, "name"),
                            Arg(args, "host"), Arg(args, "port"), Arg(args, "password"));
                    case "listwidgets": return SendWidgetList(cph, Arg(args, "requestId"));
                    case "getfile": return SendFile(cph, Arg(args, "widget"), Arg(args, "name"), Arg(args, "requestId"));
                    case "openpicker": return OpenPicker(cph, Arg(args, "widget"));
                    default:
                        cph.LogWarn($"[OSW Panels] Unknown op \"{op}\". Use init, save, getLook, register, listWidgets, getFile or openPicker.");
                        return false;
                }
            }
            catch (Exception ex)
            {
                // Never let one bad request take the action down silently.
                cph.LogError($"[OSW Panels] {op} failed: {ex.Message}");
                return false;
            }
        }

        // =====================================================================
        // INIT — unpack the library, tell overlays where it is
        // =====================================================================
        public static bool Initialize(IInlineInvokeProxy cph)
        {
            lock (Gate)
            {
                var files = ReadEmbeddedPanels();
                if (!files.TryGetValue("osw-panels.css", out var css) || !files.TryGetValue("osw-panels.js", out var js))
                {
                    cph.LogError("[OSW Panels] The panel library isn't inside the DLL. In the project, put osw-panels.css and " +
                                 "osw-panels.js in the Panels folder with Build Action = Embedded Resource, then rebuild.");
                    return false;
                }

                // The version is a fingerprint of the files themselves, not a
                // number someone has to remember to bump. Overlays add it to the
                // file links (osw-panels.css?v=...), so a DLL update makes them
                // load the new files instead of an old cached copy.
                _version = Fingerprint(css, js);

                // [FILES] Overlays load the library straight from these files.
                Unpack(cph, files, _version);

                // Tell overlays where the files are. Forward slashes, ending in
                // "/", so overlays can put "file:///" or "http://absolute/" in
                // front and the file name after.
                string folder = PanelsPath.Replace('\\', '/').TrimEnd('/') + "/";

                // Temporary globals (persisted: false), rebuilt every start so
                // they can never be out of date. Version goes LAST: an overlay
                // that sees the version knows the path is there too.
                cph.SetGlobalVar(GlobalPath, folder, false);
                cph.SetGlobalVar(GlobalVersion, _version, false);

                CleanUpOldGlobals(cph);
                cph.LogInfo($"[OSW Panels] Library {_version} ready in {folder}");
                return true;
            }
        }

        // Every embedded resource whose name contains ".Panels.", keyed by its
        // file name: "OSWTools.Panels.osw-panels.css" -> "osw-panels.css".
        static Dictionary<string, byte[]> ReadEmbeddedPanels()
        {
            var asm = typeof(OSWPanels).Assembly;
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in asm.GetManifestResourceNames())
            {
                int i = name.IndexOf(".Panels.", StringComparison.Ordinal);
                if (i < 0) continue;
                using (var src = asm.GetManifestResourceStream(name))
                using (var mem = new MemoryStream())
                {
                    src.CopyTo(mem);
                    result[name.Substring(i + ".Panels.".Length)] = StripBom(mem.ToArray());
                }
            }
            return result;
        }

        // Files saved by Visual Studio often start with an invisible "byte
        // order mark". Strip it so the unpacked files are clean.
        static byte[] StripBom(byte[] b)
        {
            return b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.Skip(3).ToArray() : b;
        }

        static string Text(byte[] bytes)
        {
            string s = Encoding.UTF8.GetString(bytes);
            return s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;
        }

        static string Fingerprint(params byte[][] parts)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var p in parts) sha.TransformBlock(p, 0, p.Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash, 0, 5).Replace("-", "").ToLowerInvariant();   // 10 hex chars
            }
        }

        // Write the files to OSWData/osw-panels/, but only when the version on
        // disk differs, so a normal start touches nothing.
        static void Unpack(IInlineInvokeProxy cph, Dictionary<string, byte[]> files, string version)
        {
            Directory.CreateDirectory(PanelsPath);
            string versionFile = Path.Combine(PanelsPath, "version.txt");
            bool current = File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == version
                           && files.Keys.All(f => File.Exists(Path.Combine(PanelsPath, f)));
            if (current) return;

            foreach (var kv in files)
            {
                try { WriteAtomic(Path.Combine(PanelsPath, kv.Key), kv.Value); }
                catch (Exception ex) { cph.LogWarn($"[OSW Panels] Couldn't update {kv.Key}: {ex.Message}"); }
            }
            File.WriteAllText(versionFile, version);           // last: only marks "current" once every file is in
            cph.LogInfo($"[OSW Panels] Unpacked {files.Count} file(s) to {PanelsPath}");
        }

        // Write to a temp file, then swap it in. The real file is always either
        // the whole old version or the whole new one, never half-written.
        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // =====================================================================
        // SAVE — write looks from the picker and push them to the overlays
        // =====================================================================
        // stylesJson: { "InfoPanel": { ...style... }, "DonorTicker": null, ... }
        // A null style means "no saved look": that widget uses its own
        // Streamer.bot settings.
        public static bool SaveStyles(IInlineInvokeProxy cph, string stylesJson)
        {
            if (string.IsNullOrWhiteSpace(stylesJson))
            {
                cph.LogWarn("[OSW Panels] save: no 'styles' argument.");
                return false;
            }
            JObject styles;
            try { styles = ParseJson(stylesJson); }
            catch (JsonException ex) { cph.LogError($"[OSW Panels] save: 'styles' isn't valid JSON: {ex.Message}"); return false; }

            bool allOk = true;
            foreach (var prop in styles.Properties())
            {
                string widget = prop.Name;
                if (!SafeId.IsMatch(widget)) { cph.LogWarn($"[OSW Panels] save: skipped bad widget id \"{widget}\"."); allOk = false; continue; }

                // The look is stored and forwarded as-is: C# never reads inside
                // it, so new skins never need a DLL change.
                JToken style = prop.Value.Type == JTokenType.Null ? JValue.CreateNull() : prop.Value;
                var record = new JObject
                {
                    ["schema"] = 1,
                    ["widget"] = widget,
                    ["style"] = style,
                    ["savedAt"] = DateTime.UtcNow.ToString("o")
                };

                // If the file can't be written, the save failed: skip the
                // broadcast, so the picker (which waits for it) never says
                // "Saved" when the look isn't really kept.
                string path = LookPath(widget);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteAtomic(path, Encoding.UTF8.GetBytes(record.ToString(Formatting.Indented)));
                }
                catch (Exception ex)
                {
                    cph.LogError($"[OSW Panels] {widget}: couldn't write {path}: {ex.Message}");
                    allOk = false;
                    continue;
                }

                Broadcast(cph, StyleEvent, widget, style, "saved", null);
                cph.LogInfo(style.Type == JTokenType.Null
                    ? $"[OSW Panels] {widget}: saved look cleared (using its Streamer.bot settings)."
                    : $"[OSW Panels] {widget}: look saved to {path}");
            }
            return allOk;
        }

        // OSWData/INFO for InfoPanel, OSWData/<id> otherwise.
        public static string WidgetFolderPath(string widget)
        {
            string folder;
            if (!WidgetFolders.TryGetValue(widget, out folder)) folder = widget;
            return Path.Combine(OSWDataPath, folder);
        }

        // OSWData/INFO/look.json for InfoPanel, OSWData/<id>/look.json otherwise.
        public static string LookPath(string widget) => Path.Combine(WidgetFolderPath(widget), LookFile);

        // =====================================================================
        // WIDGET DATA FILES — e.g. the Info Panel's snapshot.json
        // =====================================================================
        // For a widget's own C# to keep data an overlay needs on load as a FILE
        // instead of a big persisted global:
        //     OSWPanels.SaveWidgetFile(CPH, "InfoPanel", "snapshot", json);
        // writes OSWData/INFO/snapshot.json (atomically). Overlays then ask for
        // it with op "getFile". Returns false (and logs why) if it couldn't.
        public static bool SaveWidgetFile(IInlineInvokeProxy cph, string widget, string name, string json)
        {
            if (!SafeId.IsMatch(widget ?? "") || !SafeId.IsMatch(name ?? ""))
            {
                cph.LogWarn($"[OSW Panels] SaveWidgetFile: bad widget or file name \"{widget}/{name}\".");
                return false;
            }
            string path = Path.Combine(WidgetFolderPath(widget), name + ".json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteAtomic(path, Encoding.UTF8.GetBytes(json ?? "null"));
                return true;
            }
            catch (Exception ex)
            {
                cph.LogWarn($"[OSW Panels] Couldn't write {path}: {ex.Message}");
                return false;
            }
        }

        // Answer: { event: "OSW_File", widget, name, data, requestId }.
        // data is the file's JSON, or null if there's no such file yet.
        public static bool SendFile(IInlineInvokeProxy cph, string widget, string name, string requestId)
        {
            if (!SafeId.IsMatch(widget ?? "") || !SafeId.IsMatch(name ?? "")) { cph.LogWarn($"[OSW Panels] getFile: bad widget or file name \"{widget}/{name}\"."); return false; }
            if (!ShareableFiles.Contains(name)) { cph.LogWarn($"[OSW Panels] getFile: \"{name}\" isn't a shareable file."); return false; }

            JToken data = JValue.CreateNull();
            string path = Path.Combine(WidgetFolderPath(widget), name + ".json");
            if (File.Exists(path))
            {
                try { data = JsonConvert.DeserializeObject<JToken>(Text(File.ReadAllBytes(path)), RawDates) ?? JValue.CreateNull(); }
                catch (Exception ex) { cph.LogWarn($"[OSW Panels] {path} couldn't be read ({ex.Message})."); }
            }
            var o = new JObject { ["event"] = FileEvent, ["widget"] = widget, ["name"] = name, ["data"] = data };
            if (!string.IsNullOrEmpty(requestId)) o["requestId"] = requestId;
            cph.WebsocketBroadcastJson(o.ToString(Formatting.None));
            return true;
        }

        // =====================================================================
        // GET LOOK — [FILES] read look.json and send it back
        // =====================================================================
        // Answer: { event: "OSW_Style", widget, style, from: "file" | "none",
        //           requestId }.   style is null when there's no saved look,
        // so the widget uses its Streamer.bot settings.
        // Read fresh every time: hand-editing look.json and refreshing the
        // overlay shows the change, no restart needed.
        public static bool SendLook(IInlineInvokeProxy cph, string widget, string requestId)
        {
            if (string.IsNullOrEmpty(widget) || !SafeId.IsMatch(widget)) { cph.LogWarn($"[OSW Panels] getLook: bad widget id \"{widget}\"."); return false; }

            JToken style = JValue.CreateNull();
            string from = "none";
            string path = LookPath(widget);
            if (File.Exists(path))
            {
                try
                {
                    var record = ParseJson(Text(File.ReadAllBytes(path)));
                    style = record["style"] ?? JValue.CreateNull();
                    from = style.Type == JTokenType.Null ? "none" : "file";
                }
                catch (Exception ex)
                {
                    // A hand edit with a typo: say where, and fall back to the
                    // widget's Streamer.bot settings instead of showing nothing.
                    cph.LogWarn($"[OSW Panels] {widget}: {path} couldn't be read ({ex.Message}). Using its Streamer.bot settings until it's fixed.");
                    from = "broken";
                }
            }
            Broadcast(cph, StyleEvent, widget, style, from, requestId);
            return true;
        }

        static void Broadcast(IInlineInvokeProxy cph, string evt, string widget, JToken style, string from, string requestId)
        {
            var o = new JObject { ["event"] = evt, ["widget"] = widget, ["style"] = style, ["from"] = from };
            if (!string.IsNullOrEmpty(requestId)) o["requestId"] = requestId;
            cph.WebsocketBroadcastJson(o.ToString(Formatting.None));
        }

        // =====================================================================
        // REGISTER — an overlay reporting where it lives  (widgets.json)
        // =====================================================================
        // Overlays can live anywhere, so they tell us their own URL (and how
        // they reach Streamer.bot) when they connect. The picker uses this to
        // preview the real overlay, with nothing for the user to type in.
        public static bool RegisterWidget(IInlineInvokeProxy cph, string widget, string url, string name,
                                          string host, string port, string password)
        {
            if (string.IsNullOrEmpty(widget) || !SafeId.IsMatch(widget)) { cph.LogWarn($"[OSW Panels] register: bad widget id \"{widget}\"."); return false; }
            if (string.IsNullOrWhiteSpace(url)) { cph.LogWarn($"[OSW Panels] register: {widget} sent no url."); return false; }

            var info = new JObject
            {
                ["id"] = widget,
                ["name"] = string.IsNullOrWhiteSpace(name) ? widget : name,
                ["url"] = url,
                ["fileUrl"] = ToFileUrl(url),
                ["host"] = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host,
                ["port"] = int.TryParse(port, out int p) ? p : 8080,
                ["password"] = password ?? ""
            };

            lock (Gate)   // read-change-write of one shared file
            {
                var all = ReadWidgets(cph);
                bool isNew = all[widget] == null;
                // Skip the write if nothing changed: overlays register on every
                // reconnect, and there's no point rewriting the same file.
                if (!isNew && JToken.DeepEquals(all[widget], info)) return true;
                all[widget] = info;
                Directory.CreateDirectory(PanelsPath);
                WriteAtomic(WidgetsPath, Encoding.UTF8.GetBytes(all.ToString(Formatting.Indented)));
                cph.LogInfo(isNew ? $"[OSW Panels] Registered widget {widget}." : $"[OSW Panels] Updated where {widget} lives.");
            }
            return true;
        }

        // Answer: { event: "OSW_Widgets", widgets: { InfoPanel: {...}, ... }, requestId }
        public static bool SendWidgetList(IInlineInvokeProxy cph, string requestId)
        {
            JObject all;
            lock (Gate) all = ReadWidgets(cph);
            var o = new JObject { ["event"] = WidgetsEvent, ["widgets"] = all };
            if (!string.IsNullOrEmpty(requestId)) o["requestId"] = requestId;
            cph.WebsocketBroadcastJson(o.ToString(Formatting.None));
            return true;
        }

        // widgets.json: { "InfoPanel": { id, name, url, fileUrl, host, port, password }, ... }
        static JObject ReadWidgets(IInlineInvokeProxy cph)
        {
            if (!File.Exists(WidgetsPath)) return new JObject();
            try { return ParseJson(Text(File.ReadAllBytes(WidgetsPath))); }
            catch (Exception ex)
            {
                // Rebuilds itself: each overlay registers again when it connects.
                cph.LogWarn($"[OSW Panels] {WidgetsPath} couldn't be read ({ex.Message}); widgets will re-register as they connect.");
                return new JObject();
            }
        }

        // In OBS, a "Local file" browser source is served as
        //   http://absolute/C:/Path/To/Overlay.html
        // A normal browser (the picker) can't open that address, so keep a
        // file:/// version alongside it for previews.
        static string ToFileUrl(string url)
        {
            const string obsLocal = "http://absolute/";
            if (url.StartsWith(obsLocal, StringComparison.OrdinalIgnoreCase))
                return "file:///" + url.Substring(obsLocal.Length);
            return url;
        }

        // =====================================================================
        // [FILES] One-time cleanup of the earlier test version, which kept the
        // library and looks in globals. Anything still useful is moved into
        // its file first, then the global is removed. Does nothing once clean.
        // =====================================================================
        static void CleanUpOldGlobals(IInlineInvokeProxy cph)
        {
            try
            {
                cph.UnsetGlobalVar("OSW_Lib_CSS", false);
                cph.UnsetGlobalVar("OSW_Lib_JS", false);

                string listRaw = cph.GetGlobalVar<string>("OSW_Widgets", true);
                if (string.IsNullOrWhiteSpace(listRaw)) return;          // already clean

                List<string> ids;
                try { ids = JsonConvert.DeserializeObject<List<string>>(listRaw) ?? new List<string>(); }
                catch (JsonException) { ids = new List<string>(); }

                var all = ReadWidgets(cph);
                foreach (var id in ids.Where(i => SafeId.IsMatch(i)))
                {
                    string w = cph.GetGlobalVar<string>("OSW_Widget_" + id, true);
                    if (all[id] == null && !string.IsNullOrWhiteSpace(w))
                        try { all[id] = ParseJson(w); } catch (JsonException) { }

                    string s = cph.GetGlobalVar<string>("OSW_Style_" + id, true);
                    string look = LookPath(id);
                    if (!File.Exists(look) && !string.IsNullOrWhiteSpace(s))
                        try
                        {
                            var rec = ParseJson(s);
                            Directory.CreateDirectory(Path.GetDirectoryName(look));
                            WriteAtomic(look, Encoding.UTF8.GetBytes(rec.ToString(Formatting.Indented)));
                        }
                        catch (Exception) { }

                    cph.UnsetGlobalVar("OSW_Widget_" + id, true);
                    cph.UnsetGlobalVar("OSW_Style_" + id, true);
                }
                if (all.Count > 0)
                    WriteAtomic(WidgetsPath, Encoding.UTF8.GetBytes(all.ToString(Formatting.Indented)));
                cph.UnsetGlobalVar("OSW_Widgets", true);
                cph.LogInfo("[OSW Panels] Moved the old OSW_* globals into OSWData files and removed them.");
            }
            catch (Exception ex)
            {
                cph.LogWarn($"[OSW Panels] Cleanup of old globals skipped: {ex.Message}");
            }
        }

        // Parse JSON but leave date-looking text alone. By default Newtonsoft
        // turns "2026-10-22T16:22:56.9986180Z" into a DateTime and writes it
        // back differently.
        static readonly JsonSerializerSettings RawDates = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
        static JObject ParseJson(string text)
        {
            var o = JsonConvert.DeserializeObject<JObject>(text, RawDates);
            if (o == null) throw new JsonException("empty");
            return o;
        }

        // =====================================================================
        // OPEN PICKER — for the "Edit look" button
        // =====================================================================
        // From a settings form:   OSWTools.OSWPanels.OpenPicker(cph, "InfoPanel");
        //
        // Writes osw-panels/picker-config.js, then opens the picker in the
        // default browser. A config FILE is used, not ?query=... in the URL,
        // because Windows drops the query part when it opens a local file.
        public static bool OpenPicker(IInlineInvokeProxy cph, string widgetId)
        {
            if (_version == null && !Initialize(cph)) return false;   // makes sure the picker is unpacked

            string picker = Path.Combine(PanelsPath, PickerFile);
            if (!File.Exists(picker))
            {
                cph.LogError($"[OSW Panels] The style picker isn't in this DLL yet ({PickerFile}).");
                return false;
            }

            // Which widget to start on, and how to reach Streamer.bot (taken
            // from that widget's own registration, since it connected
            // successfully with it). The picker asks for the widget list
            // itself once connected.
            JObject widgets;
            lock (Gate) widgets = ReadWidgets(cph);
            JObject start = widgetId != null && widgets[widgetId] is JObject w ? w : widgets.Properties().Select(pr => pr.Value as JObject).FirstOrDefault();

            var cfg = new JObject
            {
                ["widget"] = widgetId ?? (string)start?["id"],
                ["host"] = start?["host"] ?? "127.0.0.1",
                ["port"] = start?["port"] ?? 8080,
                ["password"] = start?["password"] ?? "",
                ["libVersion"] = _version,
                ["widgets"] = widgets,
                ["writtenAt"] = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(Path.Combine(PanelsPath, "picker-config.js"),
                "// Written by OSWTools.dll each time the picker opens. Don't edit.\n" +
                "window.OSW_PICKER = " + cfg.ToString(Formatting.Indented) + ";\n");

            // UseShellExecute = true: "open this file the way Windows would",
            // so an .html file opens in the user's default browser.
            Process.Start(new ProcessStartInfo(picker) { UseShellExecute = true });
            cph.LogInfo($"[OSW Panels] Opened the style picker for {cfg["widget"]}.");
            return true;
        }

        // ---------------------------------------------------------------------
        static string Arg(IDictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out var v) && v != null ? v.ToString() : null;
        }
    }
}
