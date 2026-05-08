# ABB RobotStudio MCP

[Model Context Protocol](https://modelcontextprotocol.io) server for ABB RobotStudio and ABB robot controllers.

Exposes **50 tools** across two transports:

- **`rs_*`** — RobotStudio SDK tools, routed through a small RobotStudio Add-In (`ClaudeBridge`) that runs an HTTP listener inside the RobotStudio process. Used to inspect/edit stations, targets, paths, modules, variables, IO signals, controller config, and to drive the virtual controller simulation.
- **`rws_*`** — Robot Web Services (REST) tools that talk directly to a real or virtual ABB controller. Used for motors on/off, PP-to-main, start/stop program, IO read/write, file transfer, mastership, speed override, event log, etc.

Tested against IRC5 and OmniCore (CRB 15000 GoFa).

## Install

```bash
git clone https://github.com/eliasbitsch/abb-robotstudio-mcp.git
cd abb-robotstudio-mcp
npm install
npm run build
```

### Add-In (only needed for `rs_*` tools)

Build `addin/ClaudeBridge.csproj` against your RobotStudio SDK (RobotStudio 2024+), then:

```powershell
./install-addin.ps1
```

This copies the compiled add-in into RobotStudio's add-in folder. On the next RobotStudio start, an HTTP listener comes up at `http://localhost:58080`.

## Configure your MCP client

Example for Claude Code / Claude Desktop (`mcp.json`):

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

| Env var | Default | Purpose |
|---|---|---|
| `ABB_BRIDGE_URL` | `http://localhost:58080` | RobotStudio Add-In HTTP listener |
| `ABB_RWS_URL` | `http://localhost:80` | Controller RWS endpoint (use the controller's IP for real robots) |
| `ABB_RWS_USER` | `Default User` | RWS user |
| `ABB_RWS_PASS` | `robotics` | RWS password |

For a real GoFa, point `ABB_RWS_URL` at the controller (e.g. `http://192.168.125.1`) and supply real credentials.

## Tools

### RobotStudio SDK (`rs_*`, via Add-In)

`rs_ping`, `rs_get_station`, `rs_save_station`, `rs_get_station_objects`, `rs_get_tasks`,
`rs_get_paths`, `rs_get_path_targets`, `rs_create_target`, `rs_create_path`,
`rs_get_position`, `rs_check_collisions`,
`rs_get_modules`, `rs_read_module`, `rs_write_module`,
`rs_read_variable`, `rs_write_variable`,
`rs_read_config`, `rs_write_config`,
`rs_get_io_signals`, `rs_controller_status`,
`rs_start_simulation`, `rs_stop_simulation`, `rs_pause_simulation`,
`rs_reset_simulation`, `rs_simulation_status`, `rs_set_sim_speed`.

### Robot Web Services (`rws_*`, REST)

`rws_controller_status`, `rws_execution_state`, `rws_event_log`,
`rws_get_tasks`, `rws_get_modules`, `rws_read_module`, `rws_write_module`,
`rws_read_variable`, `rws_write_variable`, `rws_get_position`,
`rws_get_io_signals`, `rws_read_io`, `rws_write_io`,
`rws_set_motors`, `rws_reset_pp`, `rws_start_program`, `rws_stop_program`,
`rws_get_speed_override`, `rws_set_speed_override`,
`rws_request_mastership`, `rws_release_mastership`,
`rws_list_files`, `rws_read_file`, `rws_write_file`.

## Safety

These tools can move real hardware (`rws_set_motors`, `rws_start_program`, `rws_write_*`). Only point `ABB_RWS_URL` at a real controller when you understand what an LLM with these tools can do, and prefer a virtual controller for development.

## License

MIT — see [LICENSE](LICENSE).
