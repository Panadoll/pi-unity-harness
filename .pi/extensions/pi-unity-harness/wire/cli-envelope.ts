/**
 * Wire envelope：只处理 CLI/mux 方向相关的结构字段。
 * pipe 的 reply_to 与 mux 的 id 不在这里合并。
 * 长输出落盘、TOON、Base64 属于 presentation，不进本文件。
 */

export interface CliExecutionResult {
  ok: boolean;
  result?: unknown;
  error?: string;
  error_type?: string;
  help?: string[];
  exitCode?: number;
  truncated?: boolean;
  savedScratchPath?: string;
  text?: string;
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return null;
}

function asStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const out: string[] = [];
  for (const item of value) {
    if (typeof item === "string") out.push(item);
  }
  return out;
}

/** exec stdout JSON。缺失 ok 视为成功；同时接受 exitCode / exit_code。 */
export function parseCliJson(text: string): CliExecutionResult {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, result: null };
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    return { ok: false, error: trimmed, exitCode: 1 };
  }
  const obj = asRecord(parsed);
  if (!obj) return { ok: false, error: trimmed, exitCode: 1 };
  const ok = obj.ok !== false;
  const help = asStringArray(obj.help);
  const error = typeof obj.error === "string" ? obj.error : undefined;
  const errorType = typeof obj.error_type === "string" ? obj.error_type : undefined;
  const exitCode =
    typeof obj.exitCode === "number"
      ? obj.exitCode
      : typeof obj.exit_code === "number"
        ? obj.exit_code
        : ok
          ? 0
          : 1;
  return {
    ok,
    result: obj.result,
    error,
    error_type: errorType,
    help,
    exitCode,
    truncated: obj.truncated === true,
    savedScratchPath: typeof obj.savedScratchPath === "string" ? obj.savedScratchPath : undefined,
    text: typeof obj.text === "string" ? obj.text : undefined,
  };
}

/**
 * mux 回复。必须带字符串 id；ok 只有显式 true 才成功。
 * 不接受 reply_to，也不读 exit_code。
 */
export function parseMuxReply(value: unknown): CliExecutionResult | null {
  const obj = asRecord(value);
  if (!obj || typeof obj.id !== "string") return null;
  const ok = obj.ok === true;
  const help = asStringArray(obj.help);
  const error = typeof obj.error === "string" ? obj.error : undefined;
  const errorType = typeof obj.error_type === "string" ? obj.error_type : undefined;
  const exitCode =
    typeof obj.exitCode === "number" ? obj.exitCode : ok ? 0 : 1;
  return {
    ok,
    result: obj.result,
    error,
    error_type: errorType,
    help,
    exitCode,
    truncated: obj.truncated === true,
    savedScratchPath: typeof obj.savedScratchPath === "string" ? obj.savedScratchPath : undefined,
    text: typeof obj.text === "string" ? obj.text : undefined,
  };
}
