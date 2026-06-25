/**
 * ABB RobotStudio MCP Server — RAPID Syntax Validator
 * v3.0.0 — Structural balance analysis for RAPID source code
 *
 * Checks: MODULE/ENDMODULE, PROC/ENDPROC, IF/ENDIF, FOR/ENDFOR,
 *         WHILE/ENDWHILE, TEST/ENDTEST, TRY/ENDTRY, FUNC/ENDFUNC
 */

const BLOCK_PAIRS: Record<string, { closer: string; label: string }> = {
  MODULE: { closer: "ENDMODULE", label: "MODULE" },
  PROC: { closer: "ENDPROC", label: "PROC" },
  FUNC: { closer: "ENDFUNC", label: "FUNC" },
  IF: { closer: "ENDIF", label: "IF (block)" },
  FOR: { closer: "ENDFOR", label: "FOR" },
  WHILE: { closer: "ENDWHILE", label: "WHILE" },
  TEST: { closer: "ENDTEST", label: "TEST" },
  TRY: { closer: "ENDTRY", label: "TRY" },
};

const OPENERS = new Set(Object.keys(BLOCK_PAIRS));
const CLOSERS = new Set(Object.values(BLOCK_PAIRS).map((p) => p.closer));

interface BlockEntry {
  keyword: string; // "IF", "PROC", "FOR", etc.
  line: number;
}

export interface RapidValidationError {
  line: number;
  message: string;
  type: string; // e.g. "IF_ENDIF", "PROC_ENDPROC"
}

export interface RapidValidationResult {
  valid: boolean;
  errors: RapidValidationError[];
  warnings: string[];
  lineCount: number;
}

/**
 * Validate RAPID source code for structural block balance.
 * Does NOT validate semantics — only block matching.
 */
export function validateRapidCode(code: string): RapidValidationResult {
  const errors: RapidValidationError[] = [];
  const warnings: string[] = [];

  // Preprocess: strip comments
  const clean = stripRapidComments(code);

  const lines = clean.split("\n");
  const stack: BlockEntry[] = [];

  for (let i = 0; i < lines.length; i++) {
    const rawLine = lines[i];
    const lineNum = i + 1;

    // Tokenize the line (case-insensitive for RAPID keywords)
    const tokens = rawLine
      .toUpperCase()
      .split(/[\s(),;:=+*/\-<>\[\]{}"\\]+/)
      .filter((t) => t.length > 0);

    // Skip identifier tokens after MODULE, PROC, FUNC declarations
    let skipCount = 0;
    const filtered: string[] = [];
    for (const tok of tokens) {
      if (skipCount > 0) { skipCount--; continue; }
      if (tok === "MODULE" || tok === "PROC") {
        skipCount = 1; // next token is the name, not a keyword
      } else if (tok === "FUNC") {
        skipCount = 2; // next tokens: return_type name
      }
      filtered.push(tok);
    }

    for (const token of filtered) {
      // ── Track openers ──────────────────────────
      if (OPENERS.has(token)) {
        // IF is special: only track block IF (IF ... THEN on same line)
        if (token === "IF") {
          const hasThen = tokens.includes("THEN");
          if (!hasThen) continue; // compact IF — no ENDIF needed
        }
        stack.push({ keyword: token, line: lineNum });
      }

      // ── Track closers ──────────────────────────
      if (CLOSERS.has(token)) {
        // Find matching opener
        const closer = token;
        let matched = false;

        // Walk stack backwards to find matching opener
        for (let s = stack.length - 1; s >= 0; s--) {
          const expectedCloser = BLOCK_PAIRS[stack[s].keyword]?.closer;
          if (expectedCloser === closer) {
            // Report unclosed inner blocks before removing
            for (let u = s + 1; u < stack.length; u++) {
              const inner = stack[u];
              const innerPair = BLOCK_PAIRS[inner.keyword];
              errors.push({
                line: inner.line,
                message: `${innerPair.label} at line ${inner.line} has no matching ${innerPair.closer} (closed by outer ${closer} at line ${lineNum})`,
                type: `${inner.keyword}_${innerPair.closer}`,
              });
            }
            // Remove matched opener and all nested blocks
            stack.splice(s);
            matched = true;
            break;
          }
        }

        if (!matched) {
          errors.push({
            line: lineNum,
            message: `${closer} at line ${lineNum} has no matching opener`,
            type: "UNMATCHED_CLOSER",
          });
        }
      }
    }
  }

  // ── Report unclosed blocks ──────────────────────────────
  for (const entry of stack) {
    const pair = BLOCK_PAIRS[entry.keyword];
    errors.push({
      line: entry.line,
      message: `${pair.label} at line ${entry.line} has no matching ${pair.closer}`,
      type: `${entry.keyword}_${pair.closer}`,
    });
  }

  // ── Warnings ───────────────────────────────────────────
  const hasModule =
    lines.some((l) => /^\s*MODULE\s+\w+/i.test(l)) ||
    !lines.some((l) => /^\s*(?:PROC|FUNC)\s+/i.test(l));
  if (!hasModule && lines.some((l) => /^\s*(?:PROC|FUNC)\s+/i.test(l))) {
    warnings.push("No MODULE wrapper found — code may be a fragment");
  }

  const procCount = lines.filter((l) => /^\s*PROC\s+/i.test(l)).length;
  const funcCount = lines.filter((l) => /^\s*FUNC\s+/i.test(l)).length;
  if (procCount === 0 && funcCount === 0 && lines.length > 5) {
    warnings.push("No PROC or FUNC found in module");
  }

  return {
    valid: errors.length === 0,
    errors,
    warnings,
    lineCount: lines.length,
  };
}

/**
 * Strip RAPID comments from source code.
 * Handles: ! single-line comments, (* block comments *)
 */
function stripRapidComments(code: string): string {
  // Remove block comments (* ... *)
  let result = code.replace(/\(\*[\s\S]*?\*\)/g, " ");
  // Remove single-line comments (! to end of line)
  result = result.replace(/![^\n]*/g, "");
  return result;
}
