# ABB RobotStudio MCP Server — LiskinLabs Edition

> **v2.0.0** — Fork of [eliasbitsch/abb-robotstudio-mcp](https://github.com/eliasbitsch/abb-robotstudio-mcp) with TcpListener transport, 55 MCP tools, AutoLoad, and security hardening.

Model Context Protocol server for ABB RobotStudio and ABB robot controllers.

Exposes **55 tools** across two transports:

- **`rs_*`** (31 tools) — RobotStudio SDK via TcpListener Add-In. Inspect/edit stations, targets, paths, modules, variables, IO, configs. Drive simulation. Capture screenshots.
- **`rws_*`** (24 tools) — Robot Web Services REST API direct to controller. Motors, program control, IO, files, mastership, event log.

Tested: ABB RobotStudio 2025 + IRB 4600 Virtual Controller (IRC5).

---

## ✨ Improvements over original v1.0.0

| Feature | Original | LiskinLabs v2.0.0 |
|---|---|---|
| HTTP Transport | `HttpListener` — needs `netsh http add urlacl` (admin) | **`TcpListener`** — zero admin rights |
| Tools | 50 | **55** (+5 new) |
| Add-In startup | Manual "Load Add-In" each session | **AutoLoad** |
| Screenshot | ❌ | ✅ `rs_get_screenshot` |
| Event Log (SDK) | ❌ | ✅ `rs_get_execution_errors` |
| Variable listing | ❌ | ✅ `rs_list_variables` (type/module filter) |
| Bounding Box | ❌ | ✅ `rs_get_station_objects?bbox=true` |
| IO Signals (SDK) | Stubs → "not implemented" | ✅ Real API |
| RAPID upload | Raw UTF-8 | ✅ BOM-free + CRLF |
| Simulation reset | Stop only | ✅ Stop + PP reset |
| Request timeout | None | ✅ 15-30s AbortController |
| Path validation | N/A | ✅ Screenshot sandboxed |
| CORS | N/A | ✅ Removed (not a browser API) |

## New MCP Tools in v2.0.0

| Tool | Description |
|---|---|
| `rs_get_screenshot` | Capture 3D view → base64 PNG (width/height params) |
| `rs_get_execution_errors` | Controller event log via SDK (count param) |
| `rs_list_variables` | RAPID variables with type/module filters |
| `rs_get_station_objects` | Now supports `bbox=true` for position + dimensions |

---

## Quick Start

### 1. Build

```bash
git clone https://github.com/LiskinLabs/abb-robotstudio-mcp.git
cd abb-robotstudio-mcp

# TypeScript MCP Server
cd src && npm install && npm run build && cd ..

# C# Add-In
cd addin && dotnet build ClaudeBridge.csproj && cd ..
```

### 2. Install Add-In

```powershell
# As Administrator (copies to Program Files)
powershell -ExecutionPolicy Bypass -File install-addin.ps1

# Or specify RobotStudio version:
powershell -ExecutionPolicy Bypass -File install-addin.ps1 -RobotStudioVersion "2024"
```

### 3. Configure Claude Code

Add to `.mcp.json`:

```json
{
  "mcpServers": {
    "abb-robotstudio": {
      "command": "node",
      "args": ["C:/path/to/abb-robotstudio-mcp/dist/index.js"],
      "env": {
        "ABB_BRIDGE_URL": "http://localhost:58080",
        "ABB_RWS_URL": "http://localhost:80",
        "ABB_RWS_USER": "Default User",
        "ABB_RWS_PASS": "robotics"
      }
    }
  }
}
```

### 4. Launch RobotStudio

1. Open a station with Virtual Controller
2. Add-In loads automatically (AutoLoad)
3. Verify: `curl http://localhost:58080/ping`

**No admin rights needed** — TcpListener binds to `127.0.0.1` natively.

---

## Architecture

```
AI Assistant ←→ MCP stdio ←→ TypeScript Server ←→ HTTP ←→ C# Add-In (TcpListener :58080)
                                                     ←→ REST → Controller RWS (:80)
```

- **C# Add-In** (.NET 4.8): `TcpListener(IPAddress.Loopback, 58080)`, manual HTTP parsing, 32+ endpoints, UI thread marshaling
- **TypeScript** (Node.js 18+): 55 MCP tools, AbortController timeouts, structured error handling

---

## All 55 Tools

### SDK (`rs_*`) — 31 tools

`rs_ping` `rs_get_station` `rs_get_station_objects` `rs_save_station`
`rs_get_tasks` `rs_get_modules` `rs_read_module` `rs_write_module`
`rs_read_variable` `rs_write_variable` `rs_list_variables` `rs_get_execution_errors`
`rs_get_screenshot` `rs_controller_status`
`rs_start_simulation` `rs_stop_simulation` `rs_pause_simulation` `rs_reset_simulation`
`rs_simulation_status` `rs_set_sim_speed`
`rs_get_paths` `rs_get_path_targets` `rs_create_path` `rs_create_target`
`rs_read_config` `rs_write_config` `rs_check_collisions`
`rs_get_position` `rs_get_io_signals`

### RWS (`rws_*`) — 24 tools

`rws_controller_status` `rws_set_motors` `rws_start_program` `rws_stop_program`
`rws_reset_pp` `rws_execution_state` `rws_get_tasks` `rws_get_modules`
`rws_read_module` `rws_write_module` `rws_read_variable` `rws_write_variable`
`rws_get_position` `rws_get_io_signals` `rws_read_io` `rws_write_io`
`rws_get_speed_override` `rws_set_speed_override` `rws_event_log`
`rws_list_files` `rws_read_file` `rws_write_file`
`rws_request_mastership` `rws_release_mastership`

---

## Environment Variables

| Variable | Default | Purpose |
|---|---|---|
| `ABB_BRIDGE_URL` | `http://localhost:58080` | Add-In TcpListener endpoint |
| `ABB_RWS_URL` | `http://localhost:80` | Controller RWS (IP for real robots) |
| `ABB_RWS_USER` | `Default User` | RWS auth user |
| `ABB_RWS_PASS` | `robotics` | RWS auth password |

---

## Safety

⚠️ These tools can move real hardware (`rws_set_motors`, `rws_start_program`, `rws_write_*`). Use a virtual controller for development. Point `ABB_RWS_URL` at a physical robot only when you fully understand the risks.

---

## Credits & Attribution

- **Original author:** [Elias Bitsch](https://github.com/eliasbitsch) — [abb-robotstudio-mcp](https://github.com/eliasbitsch/abb-robotstudio-mcp) (MIT License). The 50-tool foundation, SDK/RWS dual-transport architecture, and ClaudeBridge Add-In concept.
- **Improvements:** [LiskinLabs](https://github.com/LiskinLabs) — Silvestr Liskin & Claude Code
  - TcpListener transport (eliminates admin rights requirement)
  - 5 new MCP tools (screenshot, event log, variable listing, bounding box, IO signals)
  - RAPID upload fixes (BOM-free UTF-8, CRLF normalization, module cleanup)
  - AutoLoad manifest, security hardening, request timeouts
- **Inspiration:** [zhou-zhichao/robotstudio-mcp](https://github.com/zhou-zhichao/robotstudio-mcp) — pioneered the TcpListener approach for Windows permission handling.

---

## License

MIT — see [LICENSE](LICENSE)
