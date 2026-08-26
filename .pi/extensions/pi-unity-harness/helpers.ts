import { relative, resolve, join } from "node:path";
import { writeFileSync, mkdirSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";

export interface PipelineParameterInfo {
  name: string;
  description?: string;
  type?: string;
  typeFullName?: string;
  required?: boolean;
  defaultValue?: unknown;
}

export interface PipelineCommandInfo {
  name: string;
  description?: string;
  mainThreadRequired?: boolean;
  runtimeOnly?: boolean;
  schema?: Record<string, unknown> | null;
  parameters?: PipelineParameterInfo[];
}

export interface PipelineCommandList {
  pipelineAvailable: boolean;
  commands: Array<PipelineCommandInfo | null | undefined>;
}

export const PIPELINE_TOOL_EXCLUDE = new Set(["eval", "recompile", "recompile_status", "editor_status"]);
export const PIPELINE_SHORTCUT_COMMANDS = new Set(["run_tests", "list_tests", "reload_file", "reload_file_override"]);

export interface TypeBoxLike {
  Unsafe(schema: any): any;
  Object(properties: Record<string, any>): any;
  Optional(schema: any): any;
  Boolean(options?: { description?: string }): any;
  Number(options?: { description?: string }): any;
  Integer(options?: { description?: string }): any;
  String(options?: { description?: string }): any;
}

export function parseEditorStatus(value: unknown): { editorStatus: string; focusState?: string; windowState?: string } {
  const raw = String(value ?? "unknown");
  const [editorStatus, ...parts] = raw.split(";").map((part) => part.trim()).filter(Boolean);
  let focusState: string | undefined;
  let windowState: string | undefined;

  for (const part of parts) {
    const [key, val] = part.split("=", 2).map((item) => item.trim());
    if (key === "focus" && val) focusState = val;
    if (key === "window" && val) windowState = val;
  }

  return { editorStatus: editorStatus || "unknown", focusState, windowState };
}

export function parseUnityMajorVersion(version: string | undefined): number | undefined {
  const match = String(version ?? "").match(/^(\d+)/);
  return match ? Number(match[1]) : undefined;
}

export function schemaToTypeBox(TypeApi: TypeBoxLike, schema: Record<string, unknown> | null | undefined, parameters: PipelineParameterInfo[] | undefined): any {
  if (schema && typeof schema === "object" && schema.type === "object" && schema.properties && typeof schema.properties === "object") {
    return TypeApi.Unsafe(schema as any);
  }

  const properties: Record<string, any> = {};
  for (const parameter of parameters ?? []) {
    const item = parameterToTypeBox(TypeApi, parameter);
    properties[parameter.name] = parameter.required ? item : TypeApi.Optional(item);
  }
  return TypeApi.Object(properties);
}

export function parameterToTypeBox(TypeApi: TypeBoxLike, parameter: PipelineParameterInfo): any {
  const options = { description: parameter.description };
  const typeName = String(parameter.type ?? parameter.typeFullName ?? "").toLowerCase();
  if (typeName.includes("boolean")) return TypeApi.Boolean(options);
  if (typeName.includes("int") || typeName.includes("long") || typeName.includes("short") || typeName.includes("byte")) return TypeApi.Integer(options);
  if (typeName.includes("single") || typeName.includes("double") || typeName.includes("decimal") || typeName.includes("float")) return TypeApi.Number(options);
  return TypeApi.String(options);
}

export function normalizePipelineToolName(commandName: string): string {
  return `unity_${commandName.replace(/[^a-zA-Z0-9_]/g, "_").toLowerCase()}`;
}

export function isVisiblePipelineCommand(command: PipelineCommandInfo | null | undefined, excludedCommands = PIPELINE_TOOL_EXCLUDE): command is PipelineCommandInfo {
  return Boolean(command?.name) && command?.runtimeOnly !== true && !excludedCommands.has(command.name);
}

export function filterPipelineCommands(list: PipelineCommandList | null | undefined, excludedCommands = PIPELINE_TOOL_EXCLUDE): PipelineCommandInfo[] {
  const commands = Array.isArray(list?.commands) ? list.commands : [];
  return commands.filter((command): command is PipelineCommandInfo => isVisiblePipelineCommand(command, excludedCommands));
}

export function normalizePipelineRequestedCommand(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

export function shouldRefreshPipelineCommands(list: Pick<PipelineCommandList, "pipelineAvailable"> | null | undefined, requestedCommand: string, commands: PipelineCommandInfo[]): boolean {
  return requestedCommand.length > 0 && (list?.pipelineAvailable !== true || !commands.some((command) => command.name === requestedCommand));
}

export function pipelineCommandSummary(command: PipelineCommandInfo, includeSchema = false, shortcutCommands = PIPELINE_SHORTCUT_COMMANDS) {
  const shortcut = shortcutCommands.has(command.name);
  return {
    name: command.name,
    shortcut,
    shortcutTool: shortcut ? normalizePipelineToolName(command.name) : null,
    description: command.description,
    mainThreadRequired: command.mainThreadRequired,
    parameters: command.parameters ?? [],
    ...(includeSchema ? { schema: command.schema ?? null } : {}),
  };
}

export function normalizePipelineCommandParams(value: unknown): Record<string, unknown> {
  if (value === undefined) return {};
  if (value !== null && typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
  throw new Error(`params must be a JSON object, got ${pipelineParamTypeName(value)}`);
}

function pipelineParamTypeName(value: unknown): string {
  if (Array.isArray(value)) return "array";
  if (value === null) return "null";
  return typeof value;
}

export function pipelineCommandTimeoutMs(commandName: string, params: Record<string, unknown>): number {
  if (commandName === "run_tests") {
    const timeoutSeconds = typeof params.timeout === "number" && Number.isFinite(params.timeout) && params.timeout > 0
      ? params.timeout
      : 300;
    return timeoutSeconds * 1000 + 30000;
  }

  const timeoutMs = params.timeoutMs ?? params.timeout_ms;
  if (typeof timeoutMs === "number" && Number.isFinite(timeoutMs) && timeoutMs > 0) {
    return timeoutMs + 5000;
  }
  const timeoutSeconds = params.timeout;
  if (typeof timeoutSeconds === "number" && Number.isFinite(timeoutSeconds) && timeoutSeconds > 0) {
    return timeoutSeconds * 1000 + 5000;
  }
  if (commandName === "list_tests") return 60000;
  if (commandName.startsWith("reload_file")) return 120000;
  return 30000;
}

/** Resolve eval file path for Unity: relative paths are project-root based; return forward-slash path for the pipe payload. */
export function resolveEvalFilePath(projectRoot: string, filePath: string): { relativePath: string; absolutePath: string } {
  const trimmed = filePath.trim();
  if (!trimmed) {
    throw new Error("filePath is required");
  }

  const absolutePath = resolve(projectRoot, trimmed);
  const relativePath = relative(projectRoot, absolutePath).replace(/\\/g, "/");
  if (!relativePath || relativePath.startsWith("..") || relativePath.includes(":")) {
    // Absolute path outside project is still allowed if it exists; send absolute for Unity to open.
    return { relativePath: absolutePath.replace(/\\/g, "/"), absolutePath };
  }

  return { relativePath, absolutePath };
}

export const MAX_SAFE_RESPONSE_CHARS = 32768; // 32KB max response size per tool call
const MIN_BASE64_DETECT_LENGTH = 256;

/** Checks if a string looks like Base64 image payload or data URL */
function isBase64Data(str: string): boolean {
  if (str.startsWith("data:image/") && str.includes(";base64,")) return true;
  if (str.length >= MIN_BASE64_DETECT_LENGTH) {
    const prefix = str.slice(0, 128);
    return /^[A-Za-z0-9+/=]+$/.test(prefix);
  }
  return false;
}

/** Recursively sanitize large base64 image strings from tool output */
export function stripLargeBase64(value: unknown, projectRoot?: string, depth = 0): unknown {
  if (depth > 20 || value === null || value === undefined) return value;

  if (typeof value === "string") {
    if (isBase64Data(value)) {
      const isDataUrl = value.startsWith("data:image/");
      const rawBase64 = isDataUrl ? value.slice(value.indexOf(";base64,") + 8) : value;
      const sizeBytes = Math.round(rawBase64.length * 0.75);
      const sizeKB = (sizeBytes / 1024).toFixed(1);

      if (projectRoot && existsSync(projectRoot)) {
        try {
          const captureDir = join(projectRoot, "Temp", "PiUnityHarness", "Captures");
          if (!existsSync(captureDir)) mkdirSync(captureDir, { recursive: true });
          const fileName = `capture_${Date.now()}_${Math.random().toString(36).slice(2, 8)}.png`;
          const filePath = join(captureDir, fileName);
          writeFileSync(filePath, Buffer.from(rawBase64, "base64"));
          const relPath = relative(projectRoot, filePath).replace(/\\/g, "/");
          return `[Base64 Image (${sizeKB} KB) stripped to prevent 413 Payload Too Large. Saved to: ${relPath}]`;
        } catch {
          // Fall back if disk write fails
        }
      }
      return `[Base64 Image data (${sizeKB} KB) stripped to prevent 413 Payload Too Large]`;
    }
    return value;
  }

  if (Array.isArray(value)) {
    return value.map((item) => stripLargeBase64(item, projectRoot, depth + 1));
  }

  if (typeof value === "object") {
    const obj = value as Record<string, unknown>;
    const result: Record<string, unknown> = {};

    const siblingSavedPath = typeof obj.SavedPath === "string" ? obj.SavedPath
      : typeof obj.savedPath === "string" ? obj.savedPath
      : typeof obj.filePath === "string" ? obj.filePath
      : typeof obj.path === "string" ? obj.path
      : undefined;

    for (const [key, val] of Object.entries(obj)) {
      const isBase64Key = /^(base64|image_base64|rawimage|screenshot_base64)$/i.test(key);
      if (isBase64Key && typeof val === "string" && val.length > 64) {
        const sizeBytes = Math.round(val.length * 0.75);
        const sizeKB = (sizeBytes / 1024).toFixed(1);
        if (siblingSavedPath) {
          result[key] = `[Base64 Image (${sizeKB} KB) stripped to prevent 413 Payload Too Large. Image saved at: ${siblingSavedPath}]`;
        } else {
          result[key] = stripLargeBase64(val, projectRoot, depth + 1);
        }
      } else {
        result[key] = stripLargeBase64(val, projectRoot, depth + 1);
      }
    }
    return result;
  }

  return value;
}

/** Formats and truncates tool output safely to ensure request payloads never exceed LLM context bounds (Error 413) */
export function safeFormatToolResponse(
  rawResult: unknown,
  projectRoot?: string,
  maxChars = MAX_SAFE_RESPONSE_CHARS,
): { text: string; details: unknown } {
  const sanitizedDetails = stripLargeBase64(rawResult, projectRoot);

  let text: string;
  if (typeof sanitizedDetails === "string") {
    text = sanitizedDetails;
  } else if (
    sanitizedDetails &&
    typeof sanitizedDetails === "object" &&
    "output" in (sanitizedDetails as Record<string, unknown>) &&
    Object.keys(sanitizedDetails as Record<string, unknown>).length <= 3
  ) {
    const obj = sanitizedDetails as { output?: unknown; typeName?: unknown; error?: unknown };
    text = typeof obj.output === "string" ? obj.output : JSON.stringify(obj.output ?? obj, null, 2);
  } else {
    text = JSON.stringify(sanitizedDetails, null, 2);
  }

  if (text.length <= maxChars) {
    return { text, details: sanitizedDetails };
  }

  let savedScratchPath: string | undefined;
  if (projectRoot && existsSync(projectRoot)) {
    try {
      const scratchDir = join(projectRoot, "Temp", "PiUnityHarness", "AgentScratch");
      if (!existsSync(scratchDir)) mkdirSync(scratchDir, { recursive: true });
      const fileName = `large_tool_output_${Date.now()}.txt`;
      const fullPath = join(scratchDir, fileName);
      writeFileSync(fullPath, text, "utf8");
      savedScratchPath = relative(projectRoot, fullPath).replace(/\\/g, "/");
    } catch {
      // Fallback
    }
  }

  if (!savedScratchPath) {
    try {
      const tempPath = join(tmpdir(), `pi_unity_large_output_${Date.now()}.txt`);
      writeFileSync(tempPath, text, "utf8");
      savedScratchPath = tempPath;
    } catch {
      // Ignore
    }
  }

  const headSize = Math.floor(maxChars * 0.75);
  const tailSize = Math.floor(maxChars * 0.20);
  const head = text.slice(0, headSize);
  const tail = text.slice(-tailSize);
  const omittedCount = text.length - headSize - tailSize;

  const warningHeader = `[WARNING: Tool output was truncated from ${text.length.toLocaleString()} to ${maxChars.toLocaleString()} characters to prevent Error 413 (Payload Too Large).\nFull un-truncated output saved to: ${savedScratchPath ?? "Temp/PiUnityHarness/AgentScratch/"}]\n\n`;

  const truncatedText = `${warningHeader}--- Output (first ${headSize.toLocaleString()} chars) ---\n${head}\n\n... [${omittedCount.toLocaleString()} characters omitted] ...\n\n--- Output (last ${tailSize.toLocaleString()} chars) ---\n${tail}`;

  return { text: truncatedText, details: sanitizedDetails };
}

