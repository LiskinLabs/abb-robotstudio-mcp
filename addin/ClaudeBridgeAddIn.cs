using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Environment;
using ABB.Robotics.RobotStudio.Stations;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.RapidDomain;
using ABB.Robotics.Controllers.Discovery;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge
{
    public class ClaudeBridgeAddIn
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private const int Port = 58080;
        private static readonly Queue<Action> _uiQueue = new Queue<Action>();
        private Thread _serverThread;
        private bool _shuttingDown;

        // ── Add-In Entry Point ───────────────────────────────────

        public static void AddinMain()
        {
            var addIn = new ClaudeBridgeAddIn();
            addIn.Start();
            Logger.AddMessage(new LogMessage("ClaudeBridge HTTP server started on port " + Port + " (TcpListener)"));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();

            // Process queued UI-thread actions on the Idle event
            UIEnvironment.Idle += OnIdle;

            // Start server on background thread
            _serverThread = new Thread(() => ListenLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "ClaudeBridge-Server"
            };
            _serverThread.Start();
        }

        public void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            UIEnvironment.Idle -= OnIdle;

            Logger.AddMessage(new LogMessage("ClaudeBridge HTTP server stopped"));
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            lock (_uiQueue)
            {
                while (_uiQueue.Count > 0)
                {
                    var action = _uiQueue.Dequeue();
                    try { action(); }
                    catch { /* handled in RunOnUIThread */ }
                }
            }
        }

        // Marshal to UI thread via the Idle event queue
        private T RunOnUIThread<T>(Func<T> func)
        {
            T result = default;
            Exception error = null;
            var done = new ManualResetEventSlim(false);

            lock (_uiQueue)
            {
                _uiQueue.Enqueue(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });
            }

            if (!done.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("UI thread did not respond within 30 seconds");

            if (error != null)
                throw new Exception(error.Message, error);
            return result;
        }

        // ── TcpListener Server Loop ─────────────────────────────

        private void ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Logger.AddMessage(new LogMessage("ClaudeBridge error: " + ex.Message));
                }
            }
        }

        // ── Manual HTTP Parser ──────────────────────────────────

        private void HandleClient(TcpClient client)
        {
            string method = "GET";
            string path = "/";
            string body = null;
            var query = new NameValueCollection();
            int statusCode = 200;

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;

                    // Read until \r\n\r\n (header terminator)
                    var buffer = new byte[8192];
                    var headerBuffer = new StringBuilder();
                    int totalRead = 0;
                    bool headersComplete = false;
                    int contentLength = 0;

                    int lastBytesRead = 0;
                    while (totalRead < 65536 && !headersComplete)
                    {
                        lastBytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (lastBytesRead == 0) break;
                        headerBuffer.Append(Encoding.ASCII.GetString(buffer, 0, lastBytesRead));
                        totalRead += lastBytesRead;

                        var text = headerBuffer.ToString();
                        int headerEnd = text.IndexOf("\r\n\r\n");
                        if (headerEnd >= 0)
                        {
                            headersComplete = true;
                            // Parse headers
                            var headerSection = text.Substring(0, headerEnd);
                            var lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);

                            // Request line: METHOD /path?query HTTP/1.1
                            if (lines.Length > 0)
                            {
                                var parts = lines[0].Split(' ');
                                if (parts.Length >= 2)
                                {
                                    method = parts[0].ToUpper();
                                    var fullPath = parts[1];
                                    int qIdx = fullPath.IndexOf('?');
                                    if (qIdx >= 0)
                                    {
                                        path = fullPath.Substring(0, qIdx);
                                        ParseQueryString(fullPath.Substring(qIdx + 1), query);
                                    }
                                    else
                                    {
                                        path = fullPath;
                                    }
                                }
                            }

                            // Parse header lines
                            for (int i = 1; i < lines.Length; i++)
                            {
                                var colonIdx = lines[i].IndexOf(':');
                                if (colonIdx > 0)
                                {
                                    var key = lines[i].Substring(0, colonIdx).Trim();
                                    var val = lines[i].Substring(colonIdx + 1).Trim();
                                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                        int.TryParse(val, out contentLength);
                                }
                            }

                            // Read body: headerEnd+4 is where body starts in accumulated text
                            // Body bytes in the last buffer read start at offset: lastBytesRead - (totalRead - headerEnd - 4)
                            if (contentLength > 0)
                            {
                                var headerLen = headerEnd + 4; // includes \r\n\r\n
                                var bodyInLastRead = totalRead - headerLen;
                                var bodyBytes = new byte[contentLength];
                                int bodyPos = 0;

                                if (bodyInLastRead > 0)
                                {
                                    int bufferBodyStart = lastBytesRead - bodyInLastRead;
                                    int copyCount = Math.Min(bodyInLastRead, contentLength);
                                    Array.Copy(buffer, bufferBodyStart, bodyBytes, 0, copyCount);
                                    bodyPos = copyCount;
                                }

                                while (bodyPos < contentLength)
                                {
                                    int n = stream.Read(bodyBytes, bodyPos, contentLength - bodyPos);
                                    if (n == 0) break;
                                    bodyPos += n;
                                }
                                body = Encoding.UTF8.GetString(bodyBytes, 0, bodyPos);
                            }
                        }
                    }

                    path = path.TrimEnd('/').ToLower();
                    if (string.IsNullOrEmpty(path)) path = "/";

                    // Route to handler on UI thread
                    object result;
                    try
                    {
                        result = RunOnUIThread(() =>
                            Route(path, method, body, query));
                    }
                    catch (Exception ex)
                    {
                        result = new { error = ex.Message };
                        statusCode = 500;
                    }

                    SendResponse(client, statusCode, result);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    SendResponse(client, 500, new { error = "Internal server error: " + ex.Message });
                }
                catch { /* best effort */ }
            }
        }

        private static void ParseQueryString(string queryString, NameValueCollection query)
        {
            if (string.IsNullOrEmpty(queryString)) return;
            foreach (var pair in queryString.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx > 0)
                    query[Uri.UnescapeDataString(pair.Substring(0, eqIdx))] =
                        Uri.UnescapeDataString(pair.Substring(eqIdx + 1));
                else
                    query[Uri.UnescapeDataString(pair)] = "";
            }
        }

        private static void SendResponse(TcpClient client, int statusCode, object data)
        {
            try
            {
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var statusText = statusCode == 200 ? "OK" :
                                 statusCode == 400 ? "Bad Request" :
                                 statusCode == 500 ? "Internal Server Error" : "OK";

                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 " + statusCode + " " + statusText + "\r\n");
                sb.Append("Content-Type: application/json; charset=utf-8\r\n");
                sb.Append("Content-Length: " + jsonBytes.Length + "\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");

                var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                var stream = client.GetStream();
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(jsonBytes, 0, jsonBytes.Length);
                stream.Flush();
            }
            catch { /* client may have disconnected */ }
        }

        // ── Route Dispatcher ─────────────────────────────────────

        private object Route(string path, string method, string body,
            NameValueCollection query)
        {
            switch (path)
            {
                case "/ping":
                    var station = Station.ActiveStation;
                    return new
                    {
                        status = "ok",
                        server = "ClaudeBridge",
                        port = Port,
                        transport = "TcpListener",
                        stationOpen = station != null,
                        stationName = station?.Name ?? "(none)"
                    };

                // ── Station ───────────────────────────────────
                case "/station":              return GetStation();
                case "/station/objects":      return method == "GET" && query["bbox"] == "true"
                                                    ? GetStationObjectsDetailed()
                                                    : GetStationObjects();
                case "/station/save":         return SaveStation();

                // ── RAPID ─────────────────────────────────────
                case "/rapid/tasks":          return GetRapidTasks();
                case "/rapid/modules":        return GetRapidModules(query["task"]);
                case "/rapid/module/text":
                    return method == "GET"
                        ? GetModuleText(query["task"], query["module"])
                        : SetModuleText(query["task"], query["module"], body);
                case "/rapid/variable":
                    return method == "GET"
                        ? GetRapidVariable(query["task"], query["name"])
                        : SetRapidVariable(query["task"], query["name"], body);
                case "/rapid/variables":
                    return ListRapidVariables(query);
                case "/rapid/errors":
                    return GetExecutionErrors(query["count"]);

                // ── Controller ────────────────────────────────
                case "/controller/status":    return GetControllerStatus();

                // ── Simulation ────────────────────────────────
                case "/simulation/start":     return StartSimulation();
                case "/simulation/stop":      return StopSimulation();
                case "/simulation/pause":     return PauseSimulation();
                case "/simulation/reset":     return ResetSimulation();
                case "/simulation/status":    return GetSimulationStatus();
                case "/simulation/speed":
                    return method == "GET" ? GetSimulationSpeed() : SetSimulationSpeed(body);

                // ── Position ──────────────────────────────────
                case "/robot/position":       return GetRobotPosition();

                // ── I/O ───────────────────────────────────────
                case "/io/signals":           return GetIOSignals();
                case "/io/signal":
                    return method == "GET"
                        ? GetIOSignal(query["name"])
                        : SetIOSignal(query["name"], body);

                // ── Paths & Targets ───────────────────────────
                case "/paths":                return GetPaths();
                case "/paths/targets":        return GetPathTargets(query["path"]);
                case "/paths/create":         return CreatePath(body);
                case "/targets/create":       return CreateTarget(body);

                // ── Config Files ──────────────────────────────
                case "/config/read":          return ReadConfigFile(query["name"]);
                case "/config/write":         return WriteConfigFile(query["name"], body);

                // ── Collision Detection ───────────────────────
                case "/collision/check":      return CheckCollisions();

                // ── Screenshot ────────────────────────────────
                case "/screenshot":           return TakeScreenshot(body);

                default:
                    return new { error = "Unknown endpoint: " + path };
            }
        }

        // ── Helpers ──────────────────────────────────────────

        private Station GetActiveStation()
        {
            var s = Station.ActiveStation;
            if (s == null) throw new Exception("No active station in RobotStudio");
            return s;
        }

        private RsIrc5Controller GetController()
        {
            var station = GetActiveStation();
            if (station.Irc5Controllers.Count == 0)
                throw new Exception("No controller in station");
            return station.Irc5Controllers[0];
        }

        private Controller ConnectToController()
        {
            var rsCtrl = GetController();
            var scanner = new NetworkScanner();
            scanner.Scan();

            ControllerInfo ctrlInfo = null;
            try
            {
                var sysId = new Guid(rsCtrl.SystemId);
                ctrlInfo = scanner.Find(sysId);
            }
            catch { }

            if (ctrlInfo == null)
            {
                var allControllers = scanner.GetControllers();
                ctrlInfo = allControllers.FirstOrDefault(c =>
                    c.Name.Equals(rsCtrl.Name, StringComparison.OrdinalIgnoreCase));
                if (ctrlInfo == null)
                {
                    var names = string.Join(", ", allControllers.Select(c => c.Name + " (" + c.SystemId + ")"));
                    throw new Exception("Controller '" + rsCtrl.Name + "' not found. Available: [" + names + "]");
                }
            }

            var controller = Controller.Connect(ctrlInfo, ConnectionType.Standalone, false);
            controller.Logon(UserInfo.DefaultUser);
            return controller;
        }

        private RsTask FindTask(RsIrc5Controller ctrl, string taskName)
        {
            var allTasks = ctrl.Tasks.Cast<RsTask>()
                .Concat(ctrl.NonMotionTasks.Cast<RsTask>())
                .ToList();

            if (string.IsNullOrEmpty(taskName))
                return allTasks.FirstOrDefault()
                    ?? throw new Exception("No tasks found");

            return allTasks.FirstOrDefault(t =>
                    t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Task '" + taskName + "' not found. Available: " +
                    string.Join(", ", allTasks.Select(t => t.Name)));
        }

        private RsPathProcedure FindPath(ProjectObject parent, string pathName)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child is RsPathProcedure pp &&
                    pp.Name.Equals(pathName, StringComparison.OrdinalIgnoreCase))
                    return pp;
                var found = FindPath(child, pathName);
                if (found != null) return found;
            }
            return null;
        }

        // ── Station Endpoints ────────────────────────────────

        private object GetStation()
        {
            var station = GetActiveStation();
            var ctrls = new List<object>();
            foreach (RsIrc5Controller c in station.Irc5Controllers)
            {
                ctrls.Add(new
                {
                    name = c.Name,
                    systemId = c.SystemId,
                    systemState = c.SystemState.ToString(),
                    systemPath = c.SystemPath
                });
            }
            return new { name = station.Name, controllers = ctrls };
        }

        private object GetStationObjects()
        {
            var station = GetActiveStation();
            var objects = new List<object>();
            CollectObjects(station, objects, 0);
            return objects;
        }

        private void CollectObjects(ProjectObject parent, List<object> list, int depth)
        {
            foreach (ProjectObject child in parent.Children)
            {
                list.Add(new
                {
                    name = child.Name,
                    type = child.GetType().Name,
                    depth
                });
                if (depth < 4)
                    CollectObjects(child, list, depth + 1);
            }
        }

        private object GetStationObjectsDetailed()
        {
            var station = GetActiveStation();
            var objects = new List<object>();
            CollectObjectsDetailed(station, objects, 0);
            return new { stationName = station.Name, objectCount = objects.Count, objects };
        }

        private void CollectObjectsDetailed(ProjectObject parent, List<object> list, int depth)
        {
            foreach (ProjectObject child in parent.Children)
            {
                var obj = new Dictionary<string, object>
                {
                    ["name"] = child.Name,
                    ["type"] = child.GetType().Name,
                    ["depth"] = depth
                };

                try
                {
                    var gc = child as GraphicComponent;
                    if (gc != null)
                    {
                        var transform = gc.Transform;
                        if (transform != null)
                        {
                            try
                            {
                                var gmat = TryGetProperty(transform, "GlobalMatrix");
                                if (gmat != null)
                                {
                                    var trans = TryGetProperty(gmat, "Translation");
                                    if (trans != null)
                                    {
                                        obj["position"] = new
                                        {
                                            x = Math.Round(GetDoubleProp(trans, "X"), 3),
                                            y = Math.Round(GetDoubleProp(trans, "Y"), 3),
                                            z = Math.Round(GetDoubleProp(trans, "Z"), 3)
                                        };
                                    }
                                }
                            }
                            catch { }
                        }

                        var bb = gc.GetBoundingBox(true);
                        if (bb != null)
                        {
                            try
                            {
                                var bbMin = TryGetProperty(bb, "Min");
                                var bbMax = TryGetProperty(bb, "Max");
                                if (bbMin != null && bbMax != null)
                                {
                                    double minX = GetDoubleProp(bbMin, "X"), minY = GetDoubleProp(bbMin, "Y"), minZ = GetDoubleProp(bbMin, "Z");
                                    double maxX = GetDoubleProp(bbMax, "X"), maxY = GetDoubleProp(bbMax, "Y"), maxZ = GetDoubleProp(bbMax, "Z");
                                    obj["boundingBox"] = new
                                    {
                                        minX_mm = Math.Round(minX * 1000, 1),
                                        minY_mm = Math.Round(minY * 1000, 1),
                                        minZ_mm = Math.Round(minZ * 1000, 1),
                                        maxX_mm = Math.Round(maxX * 1000, 1),
                                        maxY_mm = Math.Round(maxY * 1000, 1),
                                        maxZ_mm = Math.Round(maxZ * 1000, 1),
                                        sizeX_mm = Math.Round((maxX - minX) * 1000, 1),
                                        sizeY_mm = Math.Round((maxY - minY) * 1000, 1),
                                        sizeZ_mm = Math.Round((maxZ - minZ) * 1000, 1)
                                    };
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { /* bounding box not always available */ }

                list.Add(obj);

                if (depth < 4)
                    CollectObjectsDetailed(child, list, depth + 1);
            }
        }

        private object SaveStation()
        {
            var station = GetActiveStation();
            station.Save();
            return new { success = true, message = "Station '" + station.Name + "' saved" };
        }

        // ── RAPID Endpoints ──────────────────────────────────

        private object GetRapidTasks()
        {
            var ctrl = GetController();
            var tasks = new List<object>();

            void AddTasks(IEnumerable<RsTask> taskList, string category)
            {
                foreach (var task in taskList)
                {
                    var children = new List<string>();
                    foreach (ProjectObject child in task.Children)
                    {
                        children.Add(child.Name + " (" + child.GetType().Name + ")");
                        foreach (ProjectObject gc in child.Children)
                            children.Add("  " + gc.Name + " (" + gc.GetType().Name + ")");
                    }
                    tasks.Add(new
                    {
                        name = task.Name,
                        category,
                        isMotionTask = task.IsMotionTask,
                        pathProcedures = task.PathProcedures.Count,
                        dataDeclarations = task.DataDeclarations.Count,
                        children
                    });
                }
            }

            AddTasks(ctrl.Tasks.Cast<RsTask>(), "Motion");
            AddTasks(ctrl.NonMotionTasks.Cast<RsTask>(), "NonMotion");
            return tasks;
        }

        private object GetRapidModules(string taskName)
        {
            using (var controller = ConnectToController())
            {
                var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");
                var modules = new List<object>();
                foreach (Module mod in task.GetModules())
                {
                    modules.Add(new
                    {
                        name = mod.Name,
                        type = mod.Type.ToString(),
                        isSystem = mod.IsSystem
                    });
                }
                return modules;
            }
        }

        private object GetModuleText(string taskName, string moduleName)
        {
            string step = "start";
            try
            {
                step = "ConnectToController";
                using (var controller = ConnectToController())
                {
                    step = "GetTask(" + taskName + ")";
                    var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                    step = "GetModule(" + moduleName + ")";
                    var module = task.GetModule(moduleName);
                    if (module == null)
                        throw new Exception("Module '" + moduleName + "' not found in task '" + taskName + "'");

                    var localDir = Path.Combine(Path.GetTempPath(), "claude_rapid");
                    Directory.CreateDirectory(localDir);
                    var localFile = Path.Combine(localDir, moduleName + ".mod");
                    bool isSystem = module.IsSystem;

                    if (!isSystem)
                    {
                        step = "SaveProgramToFile";
                        var remoteDir = "$HOME/_claude_tmp_prog";
                        task.SaveProgramToFile(remoteDir);

                        step = "FindInSavedProgram";
                        var files = controller.FileSystem.GetFilesAndDirectories(remoteDir + "/*");
                        string foundRemote = null;
                        foreach (var f in files)
                        {
                            var fname = Path.GetFileNameWithoutExtension(f.Name);
                            if (fname.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                            {
                                foundRemote = remoteDir + "/" + f.Name;
                                break;
                            }
                        }

                        if (foundRemote != null)
                        {
                            step = "GetFile(" + foundRemote + ")";
                            controller.FileSystem.GetFile(foundRemote, localFile, true);
                            string text = File.ReadAllText(localFile);
                            try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                            try { File.Delete(localFile); } catch { }
                            return new { task = taskName, module = moduleName, type = "program", text };
                        }
                        try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                    }

                    step = "SearchSystemModule";
                    string[] searchDirs = { "$HOME", "$SYSTEM", "$SYSTEM/RAPID" };

                    foreach (var dir in searchDirs)
                    {
                        try
                        {
                            var files = controller.FileSystem.GetFilesAndDirectories(dir + "/*");
                            foreach (var f in files)
                            {
                                var fname = Path.GetFileNameWithoutExtension(f.Name);
                                if (fname.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                                {
                                    step = "GetFile(" + dir + "/" + f.Name + ")";
                                    controller.FileSystem.GetFile(dir + "/" + f.Name, localFile, true);
                                    string text = File.ReadAllText(localFile);
                                    try { File.Delete(localFile); } catch { }
                                    return new { task = taskName, module = moduleName, type = "system", text };
                                }
                            }
                        }
                        catch { }
                    }

                    step = "SaveToFile to $HOME";
                    module.SaveToFile("$HOME");

                    step = "ReadFromDisk";
                    var rsCtrl = GetController();
                    var savedFile = Path.Combine(rsCtrl.SystemPath, "HOME", moduleName + ".sysx");
                    if (!File.Exists(savedFile))
                        throw new Exception("SaveToFile succeeded but file not found at: " + savedFile);
                    string sysText = File.ReadAllText(savedFile);
                    try { File.Delete(savedFile); } catch { }
                    return new { task = taskName, module = moduleName, type = "system", text = sysText };
                }
            }
            catch (Exception ex)
            {
                throw new Exception("GetModuleText failed at step '" + step + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private object SetModuleText(string taskName, string moduleName, string body)
        {
            var data = JObject.Parse(body);
            var code = data["code"]?.ToString();
            if (string.IsNullOrEmpty(code))
                throw new Exception("Missing 'code' in request body");

            // BOM-free UTF-8 + CRLF normalization
            var normalizedCode = code.Replace("\r\n", "\n").Replace("\n", "\r\n");

            using (var controller = ConnectToController())
            {
                var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                // Clean up old modules before loading new one
                bool replace = data["replace"] == null || data["replace"].Value<bool>();
                if (replace)
                {
                    foreach (Module existingMod in task.GetModules())
                    {
                        var mName = existingMod.Name;
                        if (mName.Equals("BASE", StringComparison.OrdinalIgnoreCase) ||
                            mName.Equals("user", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!existingMod.IsSystem && !mName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { task.DeleteModule(mName); } catch { }
                        }
                    }
                }

                var rsCtrl = GetController();
                var vcHome = Path.Combine(rsCtrl.SystemPath, "HOME");
                var tmpFile = Path.Combine(vcHome, "_claude_tmp_" + moduleName + ".mod");
                try
                {
                    File.WriteAllText(tmpFile, normalizedCode, new UTF8Encoding(false));
                    using (var mastership = Mastership.Request(controller.Rapid))
                    {
                        try { task.DeleteModule(moduleName); } catch { }
                        task.LoadModuleFromFile(tmpFile, RapidLoadMode.Add);
                    }
                    return new { success = true, message = "Module " + moduleName + " updated" };
                }
                finally
                {
                    try { File.Delete(tmpFile); } catch { }
                }
            }
        }

        private object GetRapidVariable(string taskName, string varName)
        {
            var ctrl = GetController();
            var task = FindTask(ctrl, taskName);

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (decl.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                {
                    var result = new Dictionary<string, object>
                    {
                        ["name"] = decl.Name,
                        ["dataType"] = decl.DataType,
                        ["moduleName"] = decl.ModuleName,
                        ["storageType"] = decl.StorageType.ToString(),
                        ["local"] = decl.Local
                    };

                    var initExpr = TryGetStringProperty(decl, "InitialExpression");
                    if (initExpr != null) result["value"] = initExpr;

                    return result;
                }
            }
            throw new Exception("Variable '" + varName + "' not found in task '" + taskName + "'");
        }

        private object SetRapidVariable(string taskName, string varName, string body)
        {
            var data = JObject.Parse(body);
            var value = data["value"]?.ToString();
            if (value == null) throw new Exception("Missing 'value' in request body");

            var ctrl = GetController();
            var task = FindTask(ctrl, taskName);

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (decl.Name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                {
                    if (TrySetStringProperty(decl, "InitialExpression", value))
                        return new { success = true, message = "Variable " + varName + " set to " + value };
                    throw new Exception("Cannot set InitialExpression on this declaration type");
                }
            }
            throw new Exception("Variable '" + varName + "' not found");
        }

        private object ListRapidVariables(NameValueCollection query)
        {
            var ctrl = GetController();
            var taskName = query["task"] ?? "T_ROB1";
            var typeFilter = query["typeFilter"]?.ToLower();
            var moduleFilter = query["module"];

            var task = FindTask(ctrl, taskName);
            var variables = new List<object>();

            foreach (RsDataDeclaration decl in task.DataDeclarations)
            {
                if (!string.IsNullOrEmpty(moduleFilter) &&
                    !decl.ModuleName.Equals(moduleFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(typeFilter) &&
                    !decl.DataType.ToLower().Contains(typeFilter))
                    continue;

                var varInfo = new Dictionary<string, object>
                {
                    ["name"] = decl.Name,
                    ["dataType"] = decl.DataType,
                    ["moduleName"] = decl.ModuleName,
                    ["storageType"] = decl.StorageType.ToString(),
                    ["local"] = decl.Local
                };

                var initExpr = TryGetStringProperty(decl, "InitialExpression");
                if (initExpr != null) varInfo["initialValue"] = initExpr;

                variables.Add(varInfo);
            }

            return new { task = taskName, variableCount = variables.Count, variables };
        }

        private object GetExecutionErrors(string countStr)
        {
            int maxCount = 10;
            if (!string.IsNullOrEmpty(countStr)) int.TryParse(countStr, out maxCount);
            if (maxCount > 100) maxCount = 100;

            var errors = new List<object>();
            try
            {
                using (var controller = ConnectToController())
                {
                    var categories = controller.EventLog.GetCategories();
                    int collected = 0;
                    foreach (var cat in categories)
                    {
                        var catName = TryGetStringProperty(cat, "Name") ?? "?";
                        var messages = TryGetProperty(cat, "Messages") as System.Collections.IEnumerable;
                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                if (collected >= maxCount) break;
                                errors.Add(new
                                {
                                    category = catName,
                                    title = TryGetStringProperty(msg, "Title") ?? "",
                                    body = TryGetStringProperty(msg, "Body") ?? "",
                                    timestamp = (TryGetProperty(msg, "Timestamp") as DateTime?)?.ToString("o") ?? "",
                                    type = TryGetStringProperty(msg, "Type") ?? ""
                                });
                                collected++;
                            }
                        }
                        if (collected >= maxCount) break;
                    }
                }
            }
            catch (Exception ex)
            {
                return new { error = "Event log not available: " + ex.Message };
            }

            return new { errors, count = errors.Count };
        }

        // ── Controller Status ────────────────────────────────

        private object GetControllerStatus()
        {
            var ctrl = GetController();
            return new
            {
                name = ctrl.Name,
                systemId = ctrl.SystemId,
                systemState = ctrl.SystemState.ToString(),
                systemPath = ctrl.SystemPath,
                runMode = TryGetStringProperty(ctrl, "RunMode") ?? "unknown",
                simulationState = Simulator.State.ToString(),
                simulationSpeed = Simulator.SimulationSpeed
            };
        }

        // ── Simulation Endpoints ─────────────────────────────

        private object StartSimulation()
        {
            Simulator.StartAsync();
            return new { success = true, message = "Simulation started", state = Simulator.State.ToString() };
        }

        private object StopSimulation()
        {
            Simulator.StopAsync();
            return new { success = true, message = "Simulation stopped", state = Simulator.State.ToString() };
        }

        private object PauseSimulation()
        {
            Simulator.PauseAsync();
            return new { success = true, message = "Simulation paused", state = Simulator.State.ToString() };
        }

        private object ResetSimulation()
        {
            Simulator.StopAsync();

            // Try to reset Program Pointer via RWS-style action on controller
            try
            {
                using (var controller = ConnectToController())
                {
                    using (Mastership.Request(controller.Rapid))
                    {
                        // Attempt PP reset via reflection (API varies by SDK version)
                        var rapid = controller.Rapid;
                        var resetMethod = rapid.GetType().GetMethod("ResetProgramPointer")
                                       ?? rapid.GetType().GetMethod("ResetPP");
                        if (resetMethod != null)
                        {
                            resetMethod.Invoke(rapid, null);
                        }
                        else
                        {
                            // Fallback: try execution reset via known pattern
                            Logger.AddMessage(new LogMessage("ClaudeBridge: ResetProgramPointer not available in this SDK version"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.AddMessage(new LogMessage("ClaudeBridge PP reset note: " + ex.Message));
            }

            return new { success = true, message = "Simulation stopped and PP reset attempted", state = Simulator.State.ToString() };
        }

        private object GetSimulationStatus()
        {
            return new
            {
                state = Simulator.State.ToString(),
                currentTime = Simulator.CurrentTime,
                simulationSpeed = Simulator.SimulationSpeed
            };
        }

        private object GetSimulationSpeed()
        {
            return new { simulationSpeed = Simulator.SimulationSpeed };
        }

        private object SetSimulationSpeed(string body)
        {
            var data = JObject.Parse(body);
            var speed = data["speed"]?.Value<double>() ?? 1.0;
            Simulator.SimulationSpeed = speed;
            return new { success = true, message = "Simulation speed set to " + speed };
        }

        // ── Position Endpoint ────────────────────────────────

        private object GetRobotPosition()
        {
            var station = GetActiveStation();
            var mechanisms = new List<object>();

            foreach (ProjectObject child in station.Children)
            {
                if (child is Mechanism mech)
                {
                    var jointValues = new List<double>();
                    try
                    {
                        var jvProp = mech.GetType().GetProperty("JointValues");
                        if (jvProp != null)
                        {
                            var jv = jvProp.GetValue(mech) as double[];
                            if (jv != null) jointValues.AddRange(jv);
                        }
                    }
                    catch { /* ignore */ }

                    double fx = 0, fy = 0, fz = 0;
                    try
                    {
                        var flange = mech.GetFlange(0);
                        var tProp = flange.GetType().GetProperty("Transform")
                                 ?? flange.GetType().GetProperty("Matrix");
                        if (tProp != null)
                        {
                            var mat = tProp.GetValue(flange);
                            fx = (double)(mat.GetType().GetProperty("X")?.GetValue(mat) ?? 0.0);
                            fy = (double)(mat.GetType().GetProperty("Y")?.GetValue(mat) ?? 0.0);
                            fz = (double)(mat.GetType().GetProperty("Z")?.GetValue(mat) ?? 0.0);
                        }
                    }
                    catch { /* ignore */ }

                    mechanisms.Add(new
                    {
                        name = mech.Name,
                        type = mech.GetType().Name,
                        flangeX = fx, flangeY = fy, flangeZ = fz,
                        joints = jointValues
                    });
                }
            }

            if (mechanisms.Count == 0)
            {
                var ctrl = GetController();
                foreach (RsMechanicalUnit mu in ctrl.MechanicalUnits)
                {
                    mechanisms.Add(new { name = mu.Name, type = mu.GetType().Name });
                }
            }

            return mechanisms;
        }

        // ── I/O Signal Endpoints ─────────────────────────────

        private object GetIOSignals()
        {
            var signals = new List<object>();

            try
            {
                using (var controller = ConnectToController())
                {
                    System.Collections.IEnumerable signalList = null;
                    try
                    {
                        // Try GetSignals with one int arg first
                        var method = controller.IOSystem.GetType().GetMethod("GetSignals", new[] { typeof(int) });
                        if (method != null)
                            signalList = method.Invoke(controller.IOSystem, new object[] { 0 }) as System.Collections.IEnumerable;
                    }
                    catch { }
                    if (signalList == null)
                    {
                        try
                        {
                            // Try GetSignals via reflection without args
                            var method = controller.IOSystem.GetType().GetMethod("GetSignals", new Type[0]);
                            if (method != null)
                                signalList = method.Invoke(controller.IOSystem, null) as System.Collections.IEnumerable;
                        }
                        catch { }
                    }
                    if (signalList == null)
                    {
                        try
                        {
                            // Last resort: try GetAllSignals / ListSignals
                            var method = controller.IOSystem.GetType().GetMethod("GetAllSignals")
                                      ?? controller.IOSystem.GetType().GetMethod("ListSignals");
                            if (method != null)
                                signalList = method.Invoke(controller.IOSystem, null) as System.Collections.IEnumerable;
                        }
                        catch { }
                    }

                    if (signalList != null)
                    {
                        int count = 0;
                        foreach (var signal in signalList)
                        {
                            if (count >= 500) break;
                            signals.Add(new
                            {
                                name = TryGetStringProperty(signal, "Name"),
                                type = TryGetStringProperty(signal, "Type"),
                                value = TryGetStringProperty(signal, "Value")
                            });
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new { message = "IO signals not accessible via controller: " + ex.Message + ". Try starting the virtual controller first." };
            }

            if (signals.Count == 0)
                return new { message = "No IO signals found. Ensure the virtual controller is started." };

            return signals;
        }

        private object GetIOSignal(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new Exception("Signal name required. Use /io/signals to list available signals.");

            try
            {
                using (var controller = ConnectToController())
                {
                    var signal = TryGetProperty(controller.IOSystem, name)
                              ?? TryGetPropertyViaMethod(controller.IOSystem, "GetSignal", name);
                    if (signal == null)
                        throw new Exception("Signal '" + name + "' not found");
                    return new
                    {
                        name = TryGetStringProperty(signal, "Name"),
                        type = TryGetStringProperty(signal, "Type"),
                        value = TryGetStringProperty(signal, "Value")
                    };
                }
            }
            catch (Exception ex) when (!ex.Message.StartsWith("Signal '"))
            {
                throw new Exception("Cannot read IO signal: " + ex.Message + ". Ensure virtual controller is running.");
            }
        }

        private object SetIOSignal(string name, string body)
        {
            if (string.IsNullOrEmpty(name))
                throw new Exception("Signal name required. Use /io/signals to list available signals.");

            var data = JObject.Parse(body);
            var valueObj = data["value"];
            if (valueObj == null) throw new Exception("Missing 'value' in request body");

            try
            {
                using (var controller = ConnectToController())
                {
                    using (Mastership.Request(controller.Rapid))
                    {
                        var signal = TryGetProperty(controller.IOSystem, name)
                                  ?? TryGetPropertyViaMethod(controller.IOSystem, "GetSignal", name);
                        if (signal == null)
                            throw new Exception("Signal '" + name + "' not found");

                        var prevValue = TryGetStringProperty(signal, "Value");
                        var setProp = signal.GetType().GetProperty("Value");
                        if (setProp != null && setProp.CanWrite)
                        {
                            var targetType = setProp.PropertyType;
                            if (targetType == typeof(float) || targetType == typeof(double))
                                setProp.SetValue(signal, Convert.ChangeType(valueObj.Value<double>(), targetType));
                            else
                                setProp.SetValue(signal, valueObj.Value<double>());
                        }

                        return new
                        {
                            success = true,
                            message = "Signal " + name + " set to " + valueObj.Value<double>(),
                            previousValue = prevValue,
                            newValue = TryGetStringProperty(signal, "Value")
                        };
                    }
                }
            }
            catch (Exception ex) when (!ex.Message.StartsWith("Signal '"))
            {
                throw new Exception("Cannot set IO signal: " + ex.Message + ". Ensure virtual controller is running.");
            }
        }

        // ── Path & Target Endpoints ──────────────────────────

        private object GetPaths()
        {
            var station = GetActiveStation();
            var paths = new List<object>();
            CollectPaths(station, paths);
            return paths;
        }

        private void CollectPaths(ProjectObject parent, List<object> paths)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child is RsPathProcedure pp)
                {
                    paths.Add(new
                    {
                        name = pp.Name,
                        instructionCount = pp.Instructions.Count
                    });
                }
                CollectPaths(child, paths);
            }
        }

        private object GetPathTargets(string pathName)
        {
            var station = GetActiveStation();
            var path = FindPath(station, pathName);
            if (path == null) throw new Exception("Path '" + pathName + "' not found");

            var targets = new List<object>();
            foreach (RsInstruction instr in path.Instructions)
            {
                if (instr is RsMoveInstruction move)
                {
                    var targetObj = TryGetProperty(move, "Target")
                                ?? TryGetProperty(move, "RobTarget")
                                ?? TryGetProperty(move, "ToPoint");

                    var instrName = TryGetStringProperty(move, "InstructionName")
                                 ?? TryGetStringProperty(move, "Name")
                                 ?? move.GetType().Name;

                    if (targetObj != null)
                    {
                        targets.Add(new
                        {
                            name = TryGetStringProperty(targetObj, "Name") ?? "?",
                            instruction = instrName,
                            type = targetObj.GetType().Name
                        });
                    }
                    else
                    {
                        targets.Add(new { name = move.Name, instruction = instrName });
                    }
                }
            }
            return targets;
        }

        private object CreatePath(string body)
        {
            var data = JObject.Parse(body);
            var name = data["name"]?.ToString() ?? "NewPath";
            var ctrl = GetController();
            var task = ctrl.Tasks.Count > 0 ? ctrl.Tasks[0] : throw new Exception("No tasks");

            var path = new RsPathProcedure(name);
            task.PathProcedures.Add(path);
            return new { success = true, message = "Path '" + name + "' created" };
        }

        private object CreateTarget(string body)
        {
            var data = JObject.Parse(body);
            var name = data["name"]?.ToString() ?? "Target";
            var x = data["x"]?.Value<double>() ?? 0;
            var y = data["y"]?.Value<double>() ?? 0;
            var z = data["z"]?.Value<double>() ?? 0;

            var ctrl = GetController();
            var task = ctrl.Tasks.Count > 0 ? ctrl.Tasks[0] : throw new Exception("No tasks");

            var target = new RsRobTarget();
            target.Name = name;

            var transProp = target.GetType().GetProperty("Transform")
                         ?? target.GetType().GetProperty("Frame");
            if (transProp != null && transProp.CanWrite)
            {
                var frame = new ABB.Robotics.Math.Matrix4(
                    ABB.Robotics.Math.Vector3.XVector,
                    ABB.Robotics.Math.Vector3.YVector,
                    ABB.Robotics.Math.Vector3.ZVector,
                    new ABB.Robotics.Math.Vector3(x, y, z)
                );
                transProp.SetValue(target, frame);
            }
            task.DataDeclarations.Add(target);
            return new { success = true, message = "Target '" + name + "' at (" + x + ", " + y + ", " + z + ")" };
        }

        // ── Config File Endpoints ────────────────────────────

        private object ReadConfigFile(string cfgName)
        {
            var ctrl = GetController();
            var sysPath = ctrl.SystemPath;
            if (string.IsNullOrEmpty(sysPath))
                throw new Exception("Cannot determine system directory");

            var candidates = new[]
            {
                Path.Combine(sysPath, "SYSPAR", cfgName ?? "SYS.cfg"),
                Path.Combine(sysPath, cfgName ?? "SYS.cfg"),
            };

            foreach (var cfgPath in candidates)
            {
                if (File.Exists(cfgPath))
                    return new { path = cfgPath, content = File.ReadAllText(cfgPath) };
            }
            throw new Exception("Config file '" + cfgName + "' not found in " + sysPath);
        }

        private object WriteConfigFile(string cfgName, string body)
        {
            var data = JObject.Parse(body);
            var content = data["content"]?.ToString();
            if (string.IsNullOrEmpty(content))
                throw new Exception("Missing 'content' in request body");

            var ctrl = GetController();
            var sysPath = ctrl.SystemPath;

            // Try SYSPAR first, fallback to root
            var cfgPath = Path.Combine(sysPath, "SYSPAR", cfgName ?? "SYS.cfg");
            var dir = Path.GetDirectoryName(cfgPath);
            if (!Directory.Exists(dir))
            {
                cfgPath = Path.Combine(sysPath, cfgName ?? "SYS.cfg");
            }

            File.WriteAllText(cfgPath, content);
            return new { success = true, message = "Config file " + cfgName + " written to " + cfgPath };
        }

        // ── Collision Detection ──────────────────────────────

        private object CheckCollisions()
        {
            var station = GetActiveStation();
            try
            {
                CollisionDetector.CheckCollisions(station);
                return new { success = true, message = "Collision check executed — no collisions detected" };
            }
            catch (Exception ex)
            {
                return new { success = false, message = "Collision detected: " + ex.Message };
            }
        }

        // ── Screenshot ───────────────────────────────────────

        private object TakeScreenshot(string body)
        {
            GetActiveStation(); // ensure station exists
            int width = 1280, height = 720;
            string savePath = null;

            if (!string.IsNullOrEmpty(body))
            {
                var data = JObject.Parse(body);
                if (data["width"] != null) width = Math.Min(data["width"].Value<int>(), 3840);
                if (data["height"] != null) height = Math.Min(data["height"].Value<int>(), 2160);
                if (data["savePath"] != null) savePath = data["savePath"].ToString();
            }

            string imageBase64 = null;
            Exception captureError = null;

            // Route runs on UI thread via _uiQueue/OnIdle, so we can call
            // RobotStudio APIs directly without queuing another action.
            // (Queuing to _uiQueue from inside Route would deadlock.)
            try
            {
                TakeScreenshotInternal(width, height, out imageBase64);
            }
            catch (Exception ex)
            {
                throw new Exception("Screenshot failed: " + ex.Message, ex);
            }

            if (savePath != null)
            {
                // Validate savePath is within allowed directories
                var fullPath = Path.GetFullPath(savePath);
                var allowedBase = Path.GetFullPath(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RobotStudio"));
                Directory.CreateDirectory(allowedBase); // ensure base exists
                // Prevent path traversal: ensure base+separator match or exact base match
                var allowedWithSep = allowedBase.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? allowedBase
                    : allowedBase + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(allowedWithSep, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fullPath, allowedBase, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("savePath must be within " + allowedBase);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(fullPath, Convert.FromBase64String(imageBase64));
                return new
                {
                    imageBase64, width, height,
                    mimeType = "image/png",
                    savedPath = fullPath,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
            }

            return new
            {
                imageBase64, width, height,
                mimeType = "image/png",
                savedPath = (string)null,
                timestamp = DateTime.UtcNow.ToString("o")
            };
        }

        private void TakeScreenshotInternal(int width, int height, out string imageBase64)
        {
            imageBase64 = null;

            // Search all loaded assemblies for GraphicControl type
            Type gcType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    gcType = asm.GetType("ABB.Robotics.RobotStudio.GraphicControl");
                    if (gcType == null)
                        gcType = asm.GetTypes().FirstOrDefault(t => t.Name == "GraphicControl");
                }
                catch { }
                if (gcType != null) break;
            }
            if (gcType == null)
                throw new Exception("GraphicControl type not found in any loaded assembly. Available types with 'Graphic': " +
                    string.Join(", ", AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                        .Where(t => t.Name.Contains("Graphic") || t.Name.Contains("Screen"))
                        .Take(10).Select(t => t.FullName)));

            var acProp = gcType.GetProperty("ActiveGraphicControl");
            if (acProp == null) throw new Exception("ActiveGraphicControl property not found");
            object gc = acProp.GetValue(null);
            if (gc == null) throw new Exception("No active graphic control — open a station with 3D view");

            var ssMethod = gc.GetType().GetMethod("ScreenShot", new Type[] { typeof(int), typeof(int) });
            if (ssMethod == null) throw new Exception("ScreenShot method not found on " + gc.GetType().FullName);

            using (var bitmap = ssMethod.Invoke(gc, new object[] { width, height }) as Bitmap)
            {
                if (bitmap == null) throw new Exception("ScreenShot returned null");
                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    imageBase64 = Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        // ── Reflection Helpers ───────────────────────────────

        private ProjectObject FindObjectByName(ProjectObject parent, string name)
        {
            foreach (ProjectObject child in parent.Children)
            {
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var found = FindObjectByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private string TryGetStringProperty(object obj, string propName)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj)?.ToString(); }
                catch { return null; }
            }
            return null;
        }

        private bool TrySetStringProperty(object obj, string propName, string value)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(obj, value); return true; }
                catch { return false; }
            }
            return false;
        }

        private object TryGetProperty(object obj, string propName)
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj); }
                catch { return null; }
            }
            return null;
        }

        private double GetDoubleProp(object obj, string propName)
        {
            var val = TryGetProperty(obj, propName);
            if (val == null) return 0.0;
            try { return Convert.ToDouble(val); }
            catch { return 0.0; }
        }

        private object TryGetPropertyViaMethod(object obj, string methodName, params object[] args)
        {
            var method = obj.GetType().GetMethod(methodName);
            if (method != null)
            {
                try { return method.Invoke(obj, args); }
                catch { return null; }
            }
            return null;
        }
    }
}
