/**
 * ABB RobotStudio MCP Server — Configuration
 * v3.0.0 — Mutable RWS URL for runtime switching
 */

// SDK Bridge — immutable (ClaudeBridge Add-In inside RobotStudio)
export const BRIDGE_URL =
  process.env.ABB_BRIDGE_URL || "http://localhost:58080";

// RWS — mutable via setRwsUrl() for virtual ↔ real controller switching
let _rwsUrl = process.env.ABB_RWS_URL || "http://localhost:80";

// Allowed RWS hosts — prevents SSRF to arbitrary internal/external hosts
const ALLOWED_HOSTS = new Set([
  "localhost",
  "127.0.0.1",
  "[::1]",
  "192.168.125.1",   // Real ABB controller (known IP in .mcp.json)
]);

export function getRwsUrl(): string { return _rwsUrl; }

export function setRwsUrl(url: string): void {
  const trimmed = url.trim().replace(/\/+$/, "");

  let host: string;
  try {
    host = new URL(trimmed).hostname;
  } catch {
    throw new Error(`Invalid ABB_RWS_URL: "${url}" — not a valid URL`);
  }

  if (!trimmed.startsWith("http://") && !trimmed.startsWith("https://")) {
    throw new Error(`Invalid ABB_RWS_URL: "${url}" — must use http:// or https://`);
  }

  if (!ALLOWED_HOSTS.has(host)) {
    throw new Error(
      `RWS URL host "${host}" is not in the allowlist. ` +
      `Allowed hosts: ${[...ALLOWED_HOSTS].join(", ")}. ` +
      `Add new hosts via ABB_RWS_ALLOWED_HOSTS env var (comma-separated).`
    );
  }

  _rwsUrl = trimmed;
}

// Allow runtime extension via env var
const extraHosts = (process.env.ABB_RWS_ALLOWED_HOSTS || "").split(",").map(h => h.trim()).filter(Boolean);
extraHosts.forEach(h => ALLOWED_HOSTS.add(h));

// ABB factory default credentials for virtual controllers — NOT secrets.
// Override via ABB_RWS_USER / ABB_RWS_PASS env vars for real controllers.
// Documented: https://developercenter.robotstudio.com/api/rwsApi/
export const RWS_USER = process.env.ABB_RWS_USER || "Default User";
export const RWS_PASS = process.env.ABB_RWS_PASS || "robotics";

// Threshold for large-module fallback (RWS file upload instead of inline code)
export const LARGE_MODULE_THRESHOLD = 50_000; // 50 KB
