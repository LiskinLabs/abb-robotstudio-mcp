/**
 * ABB RobotStudio MCP Server — SDK Bridge Client
 * v3.0.0 — HTTP client to ClaudeBridge Add-In (port 58080)
 */

import { BRIDGE_URL } from "./config.js";

export class BridgeError extends Error {
  statusCode: number;
  context: string;
  constructor(message: string, statusCode: number, context: string) {
    super(message);
    this.name = "BridgeError";
    this.statusCode = statusCode;
    this.context = context;
  }
}

export async function bridge<T = any>(
  path: string,
  method: "GET" | "POST" = "GET",
  body?: object,
  timeoutMs: number = 15000,
  retries: number = 0
): Promise<T> {
  let lastError: Error | undefined;

  for (let attempt = 0; attempt <= retries; attempt++) {
    if (attempt > 0) {
      // 1-second backoff for retries
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

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
      if (json.error) throw new BridgeError(String(json.error), res.status, path);
      return json as T;
    } catch (e: any) {
      lastError = e;
      if (e instanceof BridgeError) throw e; // don't retry bridge errors
      if (e.name === "AbortError") {
        throw new Error(
          `Request to ClaudeBridge timed out after ${timeoutMs}ms. The RobotStudio UI thread may be busy.`
        );
      }
      if (e.cause?.code === "ECONNREFUSED" || e.message?.includes("ECONNREFUSED")) {
        throw new Error(
          `Cannot connect to ClaudeBridge at ${BRIDGE_URL}. Is RobotStudio running with the Add-In loaded?`
        );
      }
      // Only retry on transient fetch errors (not timeout, not connection refused)
      if (attempt >= retries) throw e;
    } finally {
      clearTimeout(timer);
    }
  }
  throw lastError ?? new Error("Bridge request failed");
}
