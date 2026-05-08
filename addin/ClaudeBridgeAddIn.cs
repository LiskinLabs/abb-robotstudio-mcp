using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private const int Port = 58080;
        private static readonly Queue<Action> _uiQueue = new Queue<Action>();

        // ── Add-In Entry Point ───────────────────────────────────

        public static void AddinMain()
        {
            var addIn = new ClaudeBridgeAddIn();
            addIn.Start();
            Logger.AddMessage(new LogMessage("ClaudeBridge HTTP server started on port " + Port));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();

            // Process queued UI-thread actions on the Idle event
            UIEnvironment.Idle += OnIdle;

            System.Threading.Tasks.Task.Run(() => ListenLoop(_cts.Token));
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

        private async System.Threading.Tasks.Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = System.Threading.Tasks.Task.Run(() => HandleRequest(context));
                }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Logger.AddMessage(new LogMessage("ClaudeBridge error: " + ex.Message));
                }
            }
        }

        // ── Request Handler ──────────────────────────────────────

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLower();
            var method = ctx.Request.HttpMethod;
            string body = null;

            if (method == "POST" || method == "PUT")
            {
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    body = reader.ReadToEnd();
            }

            object result;
            int statusCode = 200;
            try
            {
                result = RunOnUIThread(() =>
                    Route(path, method, body, ctx.Request.QueryString));
            }
            catch (Exception ex)
            {
                result = new { error = ex.Message };
                statusCode = 500;
            }

            var json = JsonConvert.SerializeObject(result, Formatting.Indented);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private object Route(string path, string method, string body,
            System.Collections.Specialized.NameValueCollection query)
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
                        stationOpen = station != null,
                        stationName = station?.Name ?? "(none)"
                    };

                // ── Station ───────────────────────────────────
                case "/station":              return GetStation();
                case "/station/objects":      return GetStationObjects();
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

                default:
                    return new { error = $"Unknown endpoint: {path}" };
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

            // Try by SystemId first, then fall back to matching by name
            ControllerInfo ctrlInfo = null;
            try
            {
                var sysId = new Guid(rsCtrl.SystemId);
                ctrlInfo = scanner.Find(sysId);
            }
            catch { }

            if (ctrlInfo == null)
            {
                // Find by controller name
                var allControllers = scanner.GetControllers();
                ctrlInfo = allControllers.FirstOrDefault(c =>
                    c.Name.Equals(rsCtrl.Name, StringComparison.OrdinalIgnoreCase));
                if (ctrlInfo == null)
                {
                    var names = string.Join(", ", allControllers.Select(c => $"{c.Name} ({c.SystemId})"));
                    throw new Exception($"Controller '{rsCtrl.Name}' not found. Available: [{names}]");
                }
            }

            var controller = Controller.Connect(ctrlInfo, ConnectionType.Standalone, false);
            controller.Logon(UserInfo.DefaultUser);
            return controller;
        }

        private RsTask FindTask(RsIrc5Controller ctrl, string taskName)
        {
            // Check motion tasks and non-motion tasks
            var allTasks = ctrl.Tasks.Cast<RsTask>()
                .Concat(ctrl.NonMotionTasks.Cast<RsTask>())
                .ToList();

            if (string.IsNullOrEmpty(taskName))
                return allTasks.FirstOrDefault()
                    ?? throw new Exception("No tasks found");

            return allTasks.FirstOrDefault(t =>
                    t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"Task '{taskName}' not found. Available: " +
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

        private object SaveStation()
        {
            var station = GetActiveStation();
            station.Save();
            return new { success = true, message = $"Station '{station.Name}' saved" };
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
                        children.Add($"{child.Name} ({child.GetType().Name})");
                        foreach (ProjectObject gc in child.Children)
                            children.Add($"  {gc.Name} ({gc.GetType().Name})");
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
                        type = mod.Type.ToString()
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
                    step = $"GetTask({taskName})";
                    var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                    step = $"GetModule({moduleName})";
                    var module = task.GetModule(moduleName);
                    if (module == null)
                        throw new Exception($"Module '{moduleName}' not found in task '{taskName}'");

                    var localDir = @"C:\temp\claude_rapid";
                    Directory.CreateDirectory(localDir);
                    var localFile = Path.Combine(localDir, $"{moduleName}.mod");
                    bool isSystem = module.IsSystem;

                    if (!isSystem)
                    {
                        // Program module: save program, then download the file
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
                            step = $"GetFile({foundRemote})";
                            controller.FileSystem.GetFile(foundRemote, localFile, true);
                            string text = File.ReadAllText(localFile);
                            try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                            try { File.Delete(localFile); } catch { }
                            return new { task = taskName, module = moduleName, type = "program", text };
                        }
                        try { controller.FileSystem.RemoveDirectory(remoteDir, true); } catch { }
                    }

                    // System module or not found in program: search controller filesystem
                    step = "SearchSystemModule";
                    string[] searchDirs = { "$HOME", "$SYSTEM", "$SYSTEM/RAPID" };
                    string[] extensions = { ".sys", ".sysx", ".mod", ".modx" };

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
                                    step = $"GetFile({dir}/{f.Name})";
                                    controller.FileSystem.GetFile(dir + "/" + f.Name, localFile, true);
                                    string text = File.ReadAllText(localFile);
                                    try { File.Delete(localFile); } catch { }
                                    return new { task = taskName, module = moduleName, type = "system", text };
                                }
                            }
                        }
                        catch { }
                    }

                    // SaveToFile expects a DIRECTORY path — it appends "/<ModuleName>.sysx"
                    step = "SaveToFile to $HOME";
                    module.SaveToFile("$HOME");

                    // Read directly from VC disk path
                    step = "ReadFromDisk";
                    var rsCtrl = GetController();
                    var savedFile = Path.Combine(rsCtrl.SystemPath, "HOME", moduleName + ".sysx");
                    if (!File.Exists(savedFile))
                        throw new Exception($"SaveToFile succeeded but file not found at: {savedFile}");
                    string sysText = File.ReadAllText(savedFile);
                    try { File.Delete(savedFile); } catch { }
                    return new { task = taskName, module = moduleName, type = "system", text = sysText };
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"GetModuleText failed at step '{step}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private object SetModuleText(string taskName, string moduleName, string body)
        {
            var data = JObject.Parse(body);
            var code = data["code"]?.ToString();
            if (string.IsNullOrEmpty(code))
                throw new Exception("Missing 'code' in request body");

            using (var controller = ConnectToController())
            {
                var task = controller.Rapid.GetTask(taskName ?? "T_ROB1");

                // Write code to temp file in VC HOME, then load into controller
                var rsCtrl = GetController();
                var vcHome = Path.Combine(rsCtrl.SystemPath, "HOME");
                var tmpFile = Path.Combine(vcHome, $"_claude_tmp_{moduleName}.mod");
                try
                {
                    File.WriteAllText(tmpFile, code);
                    using (var mastership = Mastership.Request(controller))
                    {
                        // Delete old module first, then load the new one
                        try { task.DeleteModule(moduleName); } catch { }
                        task.LoadModuleFromFile(tmpFile, RapidLoadMode.Add);
                    }
                    return new { success = true, message = $"Module {moduleName} updated" };
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

                    // Try to get InitialExpression (on RsGenericDataDeclaration)
                    var initExpr = TryGetStringProperty(decl, "InitialExpression");
                    if (initExpr != null) result["value"] = initExpr;

                    return result;
                }
            }
            throw new Exception($"Variable '{varName}' not found in task '{taskName}'");
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
                        return new { success = true, message = $"Variable {varName} set to {value}" };
                    throw new Exception("Cannot set InitialExpression on this declaration type");
                }
            }
            throw new Exception($"Variable '{varName}' not found");
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
            return new { success = true, message = "Simulation started" };
        }

        private object StopSimulation()
        {
            Simulator.StopAsync();
            return new { success = true, message = "Simulation stopped" };
        }

        private object PauseSimulation()
        {
            Simulator.PauseAsync();
            return new { success = true, message = "Simulation paused" };
        }

        private object ResetSimulation()
        {
            // Stop first, then the user can start fresh
            Simulator.StopAsync();
            return new { success = true, message = "Simulation stopped/reset" };
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
            return new { success = true, message = $"Simulation speed set to {speed}" };
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
                    // Get joint values
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

                    // Get flange transform via reflection
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

            // Fallback: MechanicalUnits from controller
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
            // IOSignal doesn't extend ProjectObject; access via controller's signal collection
            var ctrl = GetController();
            var signals = new List<object>();

            // Try reflection to find signal collection
            var sigProp = ctrl.GetType().GetProperty("IOSignals")
                       ?? ctrl.GetType().GetProperty("Signals")
                       ?? ctrl.GetType().GetProperty("IOSystem");
            if (sigProp != null)
            {
                var collection = sigProp.GetValue(ctrl) as System.Collections.IEnumerable;
                if (collection != null)
                {
                    foreach (var sig in collection)
                    {
                        signals.Add(new
                        {
                            name = TryGetStringProperty(sig, "Name"),
                            signalType = TryGetStringProperty(sig, "SignalType"),
                            value = TryGetStringProperty(sig, "Value")
                        });
                        if (signals.Count >= 200) break;
                    }
                }
            }

            if (signals.Count == 0)
                return new { message = "No IO signals found via controller. Signals may be accessible after starting the virtual controller." };

            return signals;
        }

        private object GetIOSignal(string name)
        {
            return new { message = $"Signal lookup for '{name}' - use GetIOSignals to list available signals first" };
        }

        private object SetIOSignal(string name, string body)
        {
            return new { error = "Direct I/O signal write not yet implemented for this SDK version" };
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
            if (path == null) throw new Exception($"Path '{pathName}' not found");

            var targets = new List<object>();
            foreach (RsInstruction instr in path.Instructions)
            {
                if (instr is RsMoveInstruction move)
                {
                    // Use reflection for API compatibility
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
            return new { success = true, message = $"Path '{name}' created" };
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

            // Set position via reflection for API compat
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
            return new { success = true, message = $"Target '{name}' at ({x}, {y}, {z})" };
        }

        // ── Config File Endpoints ────────────────────────────

        private object ReadConfigFile(string cfgName)
        {
            var ctrl = GetController();
            var sysPath = ctrl.SystemPath;
            if (string.IsNullOrEmpty(sysPath))
                throw new Exception("Cannot determine system directory");

            // Try common locations
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
            throw new Exception($"Config file '{cfgName}' not found in {sysPath}");
        }

        private object WriteConfigFile(string cfgName, string body)
        {
            var data = JObject.Parse(body);
            var content = data["content"]?.ToString();
            if (string.IsNullOrEmpty(content))
                throw new Exception("Missing 'content' in request body");

            var ctrl = GetController();
            var cfgPath = Path.Combine(ctrl.SystemPath, "SYSPAR", cfgName ?? "SYS.cfg");
            File.WriteAllText(cfgPath, content);
            return new { success = true, message = $"Config file {cfgName} written to {cfgPath}" };
        }

        // ── Collision Detection ──────────────────────────────

        private object CheckCollisions()
        {
            var station = GetActiveStation();
            // Use static CollisionDetector methods
            try
            {
                CollisionDetector.CheckCollisions(station);
                return new { success = true, message = "Collision check executed" };
            }
            catch (Exception ex)
            {
                return new { success = false, message = ex.Message };
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
    }
}
