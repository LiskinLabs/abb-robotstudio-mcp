/**
 * ABB RobotStudio MCP Server — Safety Guard
 * v3.0.0 — Pre-flight safety checks before simulation or execution
 *
 * RULE: NEVER run simulation or start program if there are RAPID errors.
 */

import { bridge } from "./bridge-client.js";
import { rws, rwsExtract } from "./rws-client.js";

export interface SafetyCheckResult {
  safe: boolean;
  reason?: string;
  details: {
    controllerState?: string;
    executionState?: string;
    simulationState?: string;
    errorCount?: number;
    errors?: Array<{
      title: string;
      body: string;
      timestamp: string;
      type: string;
    }>;
  };
}

/**
 * Check safety before starting RobotStudio simulation.
 * Blocks if RAPID errors exist in the controller event log.
 */
export async function checkSimulationSafety(): Promise<SafetyCheckResult> {
  const details: SafetyCheckResult["details"] = {};

  try {
    // Check controller status
    const status = (await bridge("/controller/status", "GET", undefined, 10000)) as any;
    details.simulationState = status?.simulationState;
    details.controllerState = status?.systemState;

    // Check for RAPID errors
    const errors = (await bridge("/rapid/errors?count=20", "GET", undefined, 15000)) as any;
    if (errors?.errors && Array.isArray(errors.errors)) {
      const errorList = errors.errors.filter(
        (e: any) =>
          e.type === "Error" &&
          !e.title?.includes("Backup step ready") &&
          !e.title?.includes("Motors ON") &&
          !e.title?.includes("Motors OFF") &&
          !e.title?.includes("Automatic mode")
      );
      details.errorCount = errorList.length;
      details.errors = errorList.slice(0, 20);

      if (errorList.length > 0) {
        const titles = errorList.map((e: any) => e.title).join("; ");
        return {
          safe: false,
          reason: `${errorList.length} RAPID error(s) detected: ${titles}`,
          details,
        };
      }
    }

    return { safe: true, reason: "No RAPID errors — safe to start", details };
  } catch (e: any) {
    return {
      safe: false,
      reason: `Safety check unavailable: ${e.message}`,
      details,
    };
  }
}

/**
 * Check safety before starting program on a real controller via RWS.
 * More thorough — checks controller state, execution state, and event log.
 */
export async function checkExecutionSafety(): Promise<SafetyCheckResult> {
  const details: SafetyCheckResult["details"] = {};

  try {
    // Check controller state
    const panel = await rws("/rw/panel/ctrlstate");
    details.controllerState = rwsExtract(panel.body, "ctrlstate");

    // Check execution state
    const exec = await rws("/rw/rapid/execution");
    details.executionState = rwsExtract(exec.body, "ctrlexecstate");

    if (details.executionState === "running") {
      return {
        safe: false,
        reason: "Program is already running on the controller",
        details,
      };
    }

    // Check event log for recent errors
    const elog = await rws("/rw/elog/0?lang=en&count=10");
    const titles = elog.body.match(/class="title"[^>]*>([^<]*)</gi) || [];
    const errorTitles = titles.filter(
      (t) =>
        t.includes("Error") ||
        t.includes("error") ||
        t.includes("Syntax") ||
        t.includes("syntax")
    );
    details.errorCount = errorTitles.length;

    if (errorTitles.length > 0) {
      return {
        safe: false,
        reason: `${errorTitles.length} controller error(s) detected in event log`,
        details,
      };
    }

    return { safe: true, reason: "Controller ready for execution", details };
  } catch (e: any) {
    return {
      safe: false,
      reason: `Execution safety check unavailable: ${e.message}`,
      details,
    };
  }
}
