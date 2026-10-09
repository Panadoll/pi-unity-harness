/**
 * Presentation：把已解析的 CLI 结果塑成工具 text/details。
 * 不解析 wire，不解释领域 schema。
 */

import type { CliExecutionResult } from "../wire/cli-envelope.ts";

export function formatResult(cliRes: CliExecutionResult): { content: Array<{ type: "text"; text: string }>; details: unknown } {
  if (!cliRes.ok) {
    const payload: Record<string, unknown> = {
      ok: false,
      error: cliRes.error || "pi-unity command failed",
      error_type: cliRes.error_type,
      help: cliRes.help ?? [],
      exitCode: cliRes.exitCode ?? 1,
    };
    if (cliRes.result !== undefined) payload.result = cliRes.result;
    const text = cliRes.text || JSON.stringify(payload, null, 2);
    const err = new Error(text) as Error & { details?: unknown };
    err.details = payload;
    throw err;
  }

  const text = cliRes.text
    ?? (typeof cliRes.result === "string"
      ? cliRes.result
      : JSON.stringify(cliRes.result ?? {}, null, 2));

  return {
    content: [{ type: "text", text }],
    details: cliRes.result,
  };
}
