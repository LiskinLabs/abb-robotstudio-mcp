#!/usr/bin/env node

/**
 * ABB RobotStudio MCP Server — Combined SDK + RWS
 * - SDK tools (rs_*): Control RobotStudio app via ClaudeBridge Add-In (TcpListener)
 * - RWS tools (rws_*): Control the robot controller via Robot Web Services
 *
 * v2.1.0 — TcpListener bridge with AbortController timeouts, new tools
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

// ── Configuration ────────────────────────────────────────────

const BRIDGE_URL = process.env.ABB_BRIDGE_URL || "http://localhost:58080";
const RWS_URL = process.env.ABB_RWS_URL || "http://localhost:80";
const RWS_USER = process.env.ABB_RWS_USER || "Default User";
const RWS_PASS = process.env.ABB_RWS_PASS || "robotics";

// ── SDK Bridge Client (Add-In HTTP) ─────────────────────────

async function bridge(
  path: string,
  method: "GET" | "POST" = "GET",
  body?: object,
  timeoutMs: number = 15000
): Promise<any> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const res = await fetch(`${BRIDGE_URL}${path}`, {
      method,
      headers: body ? { "Content-Type": "application/json" } : {},
      body: body ? JSON.stringify(body) : null,
      signal: controller.signal,
    });
    const json = (await res.json()) as Record<string, unknown>;
    if (json.error) throw new Error(String(json.error));
    return json;
  } catch (e: any) {
    if (e.name === "AbortError") {
      throw new Error(
        `Request to ClaudeBridge timed out after ${timeoutMs}ms. The RobotStudio UI thread may be busy.`
      );
    }
    if (e.cause?.code === "ECONNREFUSED") {
      throw new Error(
        `Cannot connect to ClaudeBridge at ${BRIDGE_URL}. Is RobotStudio running with the Add-In loaded?`
      );
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

// ── RWS Client (Robot Web Services REST) ────────────────────

const rwsAuth =
  "Basic " + Buffer.from(`${RWS_USER}:${RWS_PASS}`).toString("base64");
let rwsCookie = "";

async function rws(
  methodOrPath: string,
  pathOrBody?: string,
  bodyOrCt?: string,
  ct?: string
): Promise<{ status: number; body: string }> {
  let method: string, path: string, body: string | undefined, contentType: string | undefined;
  if (methodOrPath === "GET" || methodOrPath === "POST" || methodOrPath === "PUT") {
    method = methodOrPath; path = pathOrBody!; body = bodyOrCt; contentType = ct;
  } else {
    method = "GET"; path = methodOrPath; body = pathOrBody; contentType = bodyOrCt;
  }
  const headers: Record<string, string> = {
    Authorization: rwsAuth,
    Accept: "application/xhtml+xml;v=2.0",
  };
  if (rwsCookie) headers["Cookie"] = rwsCookie;
  if (body && contentType) headers["Content-Type"] = contentType;

  const res = await fetch(`${RWS_URL}${path}`, {
    method,
    headers,
    body: body ?? null,
  });

  const setCookie = res.headers.getSetCookie();
  for (const c of setCookie) {
    if (c.includes("ABBCX") || c.includes("-http-session"))
      rwsCookie = c.split(";")[0] ?? "";
  }

  return { status: res.status, body: await res.text() };
}

function rwsExtract(xml: string, tag: string): string {
  const m =
    xml.match(new RegExp(`class="${tag}"[^>]*>([^<]*)<`, "i")) ??
    xml.match(new RegExp(`<${tag}[^>]*>([^<]*)</${tag}>`, "i"));
  return m?.[1]?.trim() ?? "";
}

function rwsExtractAll(xml: string, field: string): string[] {
  const results: string[] = [];
  const re = new RegExp(`class="${field}"[^>]*>([^<]*)<`, "gi");
  let m;
  while ((m = re.exec(xml)) !== null) results.push((m[1] ?? "").trim());
  return results;
}

// ── Helpers ──────────────────────────────────────────────────

function ok(data: any) {
  return { content: [{ type: "text" as const, text: JSON.stringify(data, null, 2) }] };
}

function err(e: any, context?: string) {
  const message = e?.message ?? String(e ?? "Unknown error");
  const prefix = context ? `[${context}] ` : "";
  return {
    content: [{ type: "text" as const, text: JSON.stringify({ error: prefix + message }, null, 2) }],
    isError: true,
  };
}

// ── MCP Server ───────────────────────────────────────────────

const server = new McpServer({
  name: "abb-robotstudio",
  version: "2.1.0",
});

// ═══════════════════════════════════════════════════════════════
// ██  SDK TOOLS (rs_*) — RobotStudio Application via Add-In  ██
// ═══════════════════════════════════════════════════════════════

server.tool("rs_ping", "Check if RobotStudio ClaudeBridge Add-In is running and connected", {},
  async () => { try {
    return ok(await bridge("/ping", "GET", undefined, 5000));
  } catch (e: any) {
    return err(new Error(`Cannot connect to ClaudeBridge at ${BRIDGE_URL}. Is RobotStudio running?`));
  }}
);

// ── Station ──────────────────────────────────────────────────

server.tool("rs_get_station", "Get active RobotStudio station info (name, controllers)", {},
  async () => { try { return ok(await bridge("/station")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_station_objects", "List all objects in the station (robots, tools, smart components). Set bbox=true for bounding box and position data.",
  { bbox: z.boolean().optional().describe("Include bounding box and position (default: false)") },
  async ({ bbox }) => { try {
    const path = bbox ? "/station/objects?bbox=true" : "/station/objects";
    return ok(await bridge(path));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_save_station", "Save the active station to disk", {},
  async () => { try { return ok(await bridge("/station/save", "POST")); } catch (e: any) { return err(e); } }
);

// ── RAPID via SDK ────────────────────────────────────────────

server.tool("rs_get_tasks", "List RAPID tasks with module counts (SDK)", {},
  async () => { try { return ok(await bridge("/rapid/tasks")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_modules", "List RAPID modules in a task (SDK)",
  { task: z.string().describe("Task name, e.g. 'T_ROB1'") },
  async ({ task }) => { try {
    return ok(await bridge(`/rapid/modules?task=${encodeURIComponent(task)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_read_module", "Read full RAPID source code of a module (SDK)",
  { task: z.string(), module: z.string().describe("e.g. 'module_MAIN', 'module_EGM', 'Wizard_Params'") },
  async ({ task, module }) => { try {
    return ok(await bridge(`/rapid/module/text?task=${encodeURIComponent(task)}&module=${encodeURIComponent(module)}`, "GET", undefined, 30000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_module", "Write RAPID module source code into running controller (SDK)",
  { task: z.string(), module: z.string(), code: z.string().describe("Full RAPID code (MODULE ... ENDMODULE)"),
    replace: z.boolean().optional().describe("Replace existing modules (default: true)") },
  async ({ task, module, code, replace }) => { try {
    return ok(await bridge(`/rapid/module/text?task=${encodeURIComponent(task)}&module=${encodeURIComponent(module)}`, "POST", { code, replace }, 30000));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_read_variable", "Read a RAPID variable initial value (SDK)",
  { task: z.string(), name: z.string() },
  async ({ task, name }) => { try {
    return ok(await bridge(`/rapid/variable?task=${encodeURIComponent(task)}&name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_variable", "Set a RAPID variable initial value (SDK)",
  { task: z.string(), name: z.string(), value: z.string() },
  async ({ task, name, value }) => { try {
    return ok(await bridge(`/rapid/variable?task=${encodeURIComponent(task)}&name=${encodeURIComponent(name)}`, "POST", { value }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_list_variables", "List RAPID variables with optional type/module filter (SDK)",
  { task: z.string().optional().describe("Task name (default: T_ROB1)"),
    typeFilter: z.string().optional().describe("Filter by data type e.g. 'robtarget', 'tooldata', 'wobjdata', 'num'"),
    module: z.string().optional().describe("Filter by module name") },
  async ({ task, typeFilter, module }) => { try {
    let p = `/rapid/variables?task=${encodeURIComponent(task || "T_ROB1")}`;
    if (typeFilter) p += `&typeFilter=${encodeURIComponent(typeFilter)}`;
    if (module) p += `&module=${encodeURIComponent(module)}`;
    return ok(await bridge(p));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_execution_errors", "Read controller event log for errors and warnings (SDK)",
  { count: z.number().optional().describe("Max messages to return (default: 10, max: 100)") },
  async ({ count }) => { try {
    return ok(await bridge(`/rapid/errors?count=${count || 10}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_get_screenshot", "Capture 3D view screenshot as base64 PNG (SDK)",
  { width: z.number().optional().describe("Width in pixels (default: 1280, max: 3840)"),
    height: z.number().optional().describe("Height in pixels (default: 720, max: 2160)") },
  async ({ width, height }) => { try {
    const result = await bridge("/screenshot", "POST", {
      width: width || 1280,
      height: height || 720,
    }, 20000);
    return {
      content: [
        { type: "image" as const, data: result.imageBase64, mimeType: result.mimeType || "image/png" },
        { type: "text" as const, text: JSON.stringify({
          width: result.width, height: result.height, timestamp: result.timestamp
        }, null, 2) },
      ],
    };
  } catch (e: any) { return err(e); } }
);

// ── Simulation ───────────────────────────────────────────────

server.tool("rs_controller_status", "Get controller state via SDK (systemState, runMode, simulation)", {},
  async () => { try { return ok(await bridge("/controller/status")); } catch (e: any) { return err(e); } }
);

server.tool("rs_start_simulation", "Start RobotStudio simulation (Play)", {},
  async () => { try { return ok(await bridge("/simulation/start", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_stop_simulation", "Stop RobotStudio simulation", {},
  async () => { try { return ok(await bridge("/simulation/stop", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_pause_simulation", "Pause RobotStudio simulation", {},
  async () => { try { return ok(await bridge("/simulation/pause", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_reset_simulation", "Reset simulation to start position (stops simulation and resets PP)", {},
  async () => { try { return ok(await bridge("/simulation/reset", "POST")); } catch (e: any) { return err(e); } }
);

server.tool("rs_simulation_status", "Get simulation state and time", {},
  async () => { try { return ok(await bridge("/simulation/status")); } catch (e: any) { return err(e); } }
);

server.tool("rs_set_sim_speed", "Set simulation speed multiplier",
  { speed: z.number().describe("Speed multiplier (1.0 = normal)") },
  async ({ speed }) => { try {
    return ok(await bridge("/simulation/speed", "POST", { speed }));
  } catch (e: any) { return err(e); } }
);

// ── Paths & Targets ─────────────────────────────────────────

server.tool("rs_get_paths", "List all robot paths in the station", {},
  async () => { try { return ok(await bridge("/paths")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_path_targets", "Get targets/waypoints in a path",
  { path: z.string() },
  async ({ path }) => { try {
    return ok(await bridge(`/paths/targets?path=${encodeURIComponent(path)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_path", "Create a new robot path",
  { name: z.string() },
  async ({ name }) => { try {
    return ok(await bridge("/paths/create", "POST", { name }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_create_target", "Create a new robot target at coordinates",
  { name: z.string(), x: z.number().describe("mm"), y: z.number().describe("mm"), z: z.number().describe("mm") },
  async ({ name, x, y, z: zp }) => { try {
    return ok(await bridge("/targets/create", "POST", { name, x, y, z: zp }));
  } catch (e: any) { return err(e); } }
);

// ── Config Files & Collision ─────────────────────────────────

server.tool("rs_read_config", "Read controller config file (SYS.cfg, EIO.cfg, SIO.cfg, MOC.cfg)",
  { name: z.string().describe("e.g. 'SIO.cfg'") },
  async ({ name }) => { try {
    return ok(await bridge(`/config/read?name=${encodeURIComponent(name)}`));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_write_config", "Write controller config file",
  { name: z.string(), content: z.string() },
  async ({ name, content }) => { try {
    return ok(await bridge(`/config/write?name=${encodeURIComponent(name)}`, "POST", { content }));
  } catch (e: any) { return err(e); } }
);

server.tool("rs_check_collisions", "Run collision check on station objects", {},
  async () => { try { return ok(await bridge("/collision/check")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_position", "Get robot position via SDK", {},
  async () => { try { return ok(await bridge("/robot/position")); } catch (e: any) { return err(e); } }
);

server.tool("rs_get_io_signals", "List I/O signals via SDK", {},
  async () => { try { return ok(await bridge("/io/signals")); } catch (e: any) { return err(e); } }
);

// ═══════════════════════════════════════════════════════════════
// ██  RWS TOOLS (rws_*) — Robot Controller via Web Services   ██
// ═══════════════════════════════════════════════════════════════

// ── Controller ───────────────────────────────────────────────

server.tool("rws_controller_status", "Get controller state via RWS (opmode, motors, RAPID execution)", {},
  async () => { try {
    const [panel, opmode, exec] = await Promise.all([
      rws( "/rw/panel/ctrlstate"),
      rws( "/rw/panel/opmode"),
      rws( "/rw/rapid/execution"),
    ]);
    return ok({
      controllerState: rwsExtract(panel.body, "ctrlstate"),
      operatingMode: rwsExtract(opmode.body, "opmode"),
      executionState: rwsExtract(exec.body, "ctrlexecstate"),
      cycle: rwsExtract(exec.body, "cycle"),
    });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_set_motors", "Turn motors on/off via RWS (real controller command)",
  { state: z.enum(["on", "off"]) },
  async ({ state }) => { try {
    const r = await rws("POST","/rw/panel/ctrlstate?action=setctrlstate",
      `ctrl-state=motor${state}`, "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? `Motors ${state}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── RAPID Execution ──────────────────────────────────────────

server.tool("rws_start_program", "Start RAPID execution on the controller via RWS",
  { mode: z.enum(["continue", "reset"]).optional() },
  async ({ mode }) => { try {
    const r = await rws("POST","/rw/rapid/execution?action=start",
      `regain=continue&execmode=${mode || "continue"}&cycle=forever&condition=none&stopatbp=disabled&alltaskbytsp=false`,
      "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? "Program started" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_stop_program", "Stop RAPID execution via RWS", {},
  async () => { try {
    const r = await rws("POST","/rw/rapid/execution?action=stop",
      "stopmode=stop&usetsp=normal", "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? "Program stopped" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_reset_pp", "Reset RAPID program pointer via RWS", {},
  async () => { try {
    const r = await rws("POST","/rw/rapid/execution?action=resetpp");
    return ok({ result: r.status === 204 ? "PP reset" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_execution_state", "Get RAPID execution state via RWS", {},
  async () => { try {
    const r = await rws( "/rw/rapid/execution");
    return ok({ state: rwsExtract(r.body, "ctrlexecstate"), cycle: rwsExtract(r.body, "cycle") });
  } catch (e: any) { return err(e); } }
);

// ── RAPID Tasks & Modules (RWS) ─────────────────────────────

server.tool("rws_get_tasks", "List RAPID tasks via RWS", {},
  async () => { try {
    const r = await rws( "/rw/rapid/tasks");
    const names = rwsExtractAll(r.body, "name");
    const excstates = rwsExtractAll(r.body, "excstate");
    return ok(names.map((n, i) => ({ name: n, executionState: excstates[i] ?? "?" })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_get_modules", "List RAPID modules in a task via RWS",
  { task: z.string() },
  async ({ task }) => { try {
    const r = await rws( `/rw/rapid/tasks/${task}/modules`);
    const names = rwsExtractAll(r.body, "name");
    const types = rwsExtractAll(r.body, "type");
    return ok(names.map((n, i) => ({ name: n, type: types[i] ?? "?" })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_module", "Read RAPID module text via RWS",
  { task: z.string(), module: z.string() },
  async ({ task, module }) => { try {
    const r = await rws( `/rw/rapid/tasks/${task}/modules/${module}/text`);
    const text = rwsExtract(r.body, "module-text") || r.body;
    return ok({ task, module, text });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_module", "Write RAPID module text via RWS (auto-mastership)",
  { task: z.string(), module: z.string(), code: z.string() },
  async ({ task, module, code }) => { try {
    await rws("POST","/rw/mastership/rapid?action=request");
    try {
      const r = await rws("POST",
        `/rw/rapid/tasks/${task}/modules/${module}/text?action=set`,
        `module-text=${encodeURIComponent(code)}`, "application/x-www-form-urlencoded");
      return ok({ result: r.status === 204 ? `Module ${module} updated` : r.body });
    } finally { await rws("POST","/rw/mastership/rapid?action=release").catch(() => {}); }
  } catch (e: any) { return err(e); } }
);

// ── RAPID Variables (RWS — live runtime values) ──────────────

server.tool("rws_read_variable", "Read a RAPID variable's live runtime value via RWS",
  { task: z.string(), module: z.string(), variable: z.string() },
  async ({ task, module, variable }) => { try {
    const r = await rws( `/rw/rapid/symbol/RAPID/${task}/${module}/${variable}/data`);
    return ok({ variable, value: rwsExtract(r.body, "value") || r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_variable", "Write a RAPID variable's live value via RWS",
  { task: z.string(), module: z.string(), variable: z.string(), value: z.string() },
  async ({ task, module, variable, value }) => { try {
    await rws("POST","/rw/mastership/rapid?action=request");
    try {
      const r = await rws("POST",
        `/rw/rapid/symbol/RAPID/${task}/${module}/${variable}/data?action=set`,
        `value=${encodeURIComponent(value)}`, "application/x-www-form-urlencoded");
      return ok({ result: r.status === 204 ? `${variable} = ${value}` : r.body });
    } finally { await rws("POST","/rw/mastership/rapid?action=release").catch(() => {}); }
  } catch (e: any) { return err(e); } }
);

// ── Robot Position (RWS — live) ──────────────────────────────

server.tool("rws_get_position", "Get live robot TCP position via RWS",
  { mechUnit: z.string().optional().describe("Default: ROB_1") },
  async ({ mechUnit }) => { try {
    const mu = mechUnit || "ROB_1";
    const [tcp, jt] = await Promise.all([
      rws( `/rw/motionsystem/mechunits/${mu}/robtarget`),
      rws( `/rw/motionsystem/mechunits/${mu}/jointtarget`),
    ]);
    return ok({
      tcp: { x: rwsExtract(tcp.body, "x"), y: rwsExtract(tcp.body, "y"), z: rwsExtract(tcp.body, "z"),
             q1: rwsExtract(tcp.body, "q1"), q2: rwsExtract(tcp.body, "q2"),
             q3: rwsExtract(tcp.body, "q3"), q4: rwsExtract(tcp.body, "q4") },
      joints: { rax_1: rwsExtract(jt.body, "rax_1"), rax_2: rwsExtract(jt.body, "rax_2"),
                rax_3: rwsExtract(jt.body, "rax_3"), rax_4: rwsExtract(jt.body, "rax_4"),
                rax_5: rwsExtract(jt.body, "rax_5"), rax_6: rwsExtract(jt.body, "rax_6") },
    });
  } catch (e: any) { return err(e); } }
);

// ── I/O Signals (RWS — live) ────────────────────────────────

server.tool("rws_get_io_signals", "List all I/O signals via RWS", {},
  async () => { try {
    const r = await rws( "/rw/iosystem/signals");
    const names = rwsExtractAll(r.body, "name");
    const types = rwsExtractAll(r.body, "type");
    const vals = rwsExtractAll(r.body, "lvalue");
    return ok(names.map((n, i) => ({ name: n, type: types[i], value: vals[i] })));
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_io", "Read a single I/O signal via RWS",
  { signal: z.string() },
  async ({ signal }) => { try {
    const r = await rws( `/rw/iosystem/signals/${signal}`);
    return ok({ signal, type: rwsExtract(r.body, "type"), value: rwsExtract(r.body, "lvalue") });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_io", "Set an I/O signal value via RWS",
  { signal: z.string(), value: z.number() },
  async ({ signal, value }) => { try {
    const r = await rws("POST",`/rw/iosystem/signals/${signal}/set-value`,
      `lvalue=${value}`, "application/x-www-form-urlencoded;v=2.0");
    return ok({ result: r.status === 204 ? `${signal} = ${value}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Speed Override (RWS) ────────────────────────────────────

server.tool("rws_get_speed_override", "Get speed override percentage via RWS", {},
  async () => { try {
    const r = await rws( "/rw/panel/speedratio");
    return ok({ speedRatio: rwsExtract(r.body, "speedratio") });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_set_speed_override", "Set speed override (0-100%) via RWS",
  { speed: z.number().min(0).max(100) },
  async ({ speed }) => { try {
    const r = await rws("POST","/rw/panel/speedratio?action=setspeedratio",
      `speed-ratio=${speed}`, "application/x-www-form-urlencoded");
    return ok({ result: r.status === 204 ? `Speed ${speed}%` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Event Log (RWS) ─────────────────────────────────────────

server.tool("rws_event_log", "Get controller event log via RWS",
  { count: z.number().optional().describe("Default: 10") },
  async ({ count }) => { try {
    const r = await rws( `/rw/elog/0?lang=en&count=${count || 10}`);
    const titles = rwsExtractAll(r.body, "title");
    const bodies = rwsExtractAll(r.body, "body");
    const timestamps = rwsExtractAll(r.body, "timestamp");
    const types = rwsExtractAll(r.body, "msgtype");
    const entries = titles.map((t, i) => ({
      title: t, body: bodies[i] ?? "", timestamp: timestamps[i] ?? "", type: types[i] ?? ""
    }));
    return ok(entries.length > 0 ? entries : { raw: r.body });
  } catch (e: any) { return err(e); } }
);

// ── Controller Files (RWS) ──────────────────────────────────

server.tool("rws_list_files", "List files on controller filesystem via RWS",
  { path: z.string().optional().describe("Default: $HOME") },
  async ({ path }) => { try {
    const r = await rws( `/fileservice/${encodeURIComponent(path || "$HOME")}`);
    return ok({ files: r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_read_file", "Read a file from controller filesystem via RWS",
  { path: z.string() },
  async ({ path }) => { try {
    const r = await rws( `/fileservice/${encodeURIComponent(path)}`);
    return ok({ content: r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_write_file", "Write a file to controller filesystem via RWS",
  { path: z.string(), content: z.string() },
  async ({ path, content }) => { try {
    const r = await rws("PUT", `/fileservice/${encodeURIComponent(path)}`, content, "text/plain");
    return ok({ result: r.status <= 204 ? `Written to ${path}` : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Mastership (RWS) ────────────────────────────────────────

server.tool("rws_request_mastership", "Request mastership (needed for writes)",
  { domain: z.enum(["rapid", "cfg", "motion"]).optional() },
  async ({ domain }) => { try {
    const r = await rws("POST",`/rw/mastership/${domain || "rapid"}?action=request`);
    return ok({ result: r.status === 204 ? "Mastership acquired" : r.body });
  } catch (e: any) { return err(e); } }
);

server.tool("rws_release_mastership", "Release mastership",
  { domain: z.enum(["rapid", "cfg", "motion"]).optional() },
  async ({ domain }) => { try {
    const r = await rws("POST",`/rw/mastership/${domain || "rapid"}?action=release`);
    return ok({ result: r.status === 204 ? "Mastership released" : r.body });
  } catch (e: any) { return err(e); } }
);

// ── Start Server ─────────────────────────────────────────────

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("ABB RobotStudio MCP Server v2.1.0 (SDK + RWS combined)");
  console.error("  SDK Bridge: " + BRIDGE_URL + " (TcpListener, no admin rights)");
  console.error("  RWS:        " + RWS_URL);
  console.error("  Tools:      " + "rs_* (31) + rws_* (24) = 55 total");
}

main().catch((err) => {
  console.error("Fatal:", err);
  process.exit(1);
});
