import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFile, spawn, type ChildProcess } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

import {
  coerceEnabled,
  describeSettingsPaths,
  inspectUnityHarnessSettings,
  loadUnityHarnessSettings,
  persistEnabled,
} from "./config.ts";

import {
  filterPipelineCommands,
  normalizePipelineToolName,
  schemaToTypeBox,
  type PipelineCommandList,
  type TypeBoxLike,
} from "./helpers.ts";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

export const Type: TypeBoxLike = {
  Object: (properties: Record<string, any>) => ({ type: "object", properties }),
  String: (options?: { description?: string }) => ({ type: "string", ...options }),
  Number: (options?: { description?: string }) => ({ type: "number", ...options }),
  Integer: (options?: { description?: string }) => ({ type: "integer", ...options }),
  Boolean: (options?: { description?: string }) => ({ type: "boolean", ...options }),
  Optional: (schema: any) => schema,
  Unsafe: (schema: any) => schema,
};

/** Injected into system prompt while harness tools are active. */
const UNITY_VERIFY_WORKFLOW_PROMPT = `
## Unity verify loop (required)
When changing Unity scripts, scenes, assets, or runtime behavior, close the loop — do not stop after edits alone:

1. **Observe**: call unity_snapshot (or a targeted unity_eval / unity_eval_file probe) before acting when context is unclear.
2. **Act**: apply the change (files, eval, pipeline commands).
3. **Compile**: after C# script edits, call unity_recompile and fix compile errors before claiming success.
4. **Verify**: confirm with unity_snapshot logs, unity_run_tests / unity_pipeline list_tests+run_tests, PlayMode (editor_play/stop), or vision/input probes as appropriate.
5. **Re-observe**: take a post-change unity_snapshot (or equivalent probe) and compare against the expected outcome.

Prefer unity_eval_file over unity_eval for multi-line or non-trivial C# (write a .repl under Temp/PiUnityHarness/AgentScratch/, then pass that path). Never call AssetDatabase.Refresh or other Domain Reload triggers inside eval — use unity_recompile instead.
`.trim();

/** Find pi-unity CLI binary path */
export function findPiUnityBinary(): string {
  if (process.env.PI_UNITY_BIN && existsSync(process.env.PI_UNITY_BIN)) {
    return resolve(process.env.PI_UNITY_BIN);
  }

  const workspaceRoot = resolve(__dirname, "../../../");
  const isWindows = process.platform === "win32";
  const binName = isWindows ? "pi-unity.exe" : "pi-unity";

  const candidates = [
    join(workspaceRoot, "bin", binName),
    join(workspaceRoot, "dist", binName),
    join(workspaceRoot, "native", "target", "release", binName),
    join(workspaceRoot, "native", "target", "debug", binName),
  ];

  for (const c of candidates) {
    if (existsSync(c)) {
      return resolve(c);
    }
  }

  return binName;
}

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

const DEFAULT_MUX_TIMEOUT_MS = 120000;
const MAX_MUX_STDOUT_CHARS = 32 * 1024 * 1024;

/** mux 调用结果。只有从未拉起成功时才允许 exec 回退。 */
export type MuxCallResult =
  | { status: "completed"; written: true; retryAllowed: false; result: CliExecutionResult }
  | { status: "unavailable-before-start"; written: false; retryAllowed: true; result: CliExecutionResult }
  | { status: "aborted"; written: false; retryAllowed: false; result: CliExecutionResult }
  | { status: "in-flight-lost"; written: true; retryAllowed: false; result: CliExecutionResult }
  | { status: "queue-dropped"; written: false; retryAllowed: false; result: CliExecutionResult };

interface MuxJob {
  argv: string[];
  timeoutMs: number;
  signal?: AbortSignal;
  resolve: (value: MuxCallResult) => void;
  dispatched: boolean;
  settled: boolean;
  id?: string;
  timer?: ReturnType<typeof setTimeout>;
  onAbort?: () => void;
}

function asRecord(value: unknown): Record<string, unknown> | null {
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

function parseCliJson(text: string): CliExecutionResult {
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

function parseMuxReply(value: unknown): CliExecutionResult | null {
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

export class MuxClient {
  private child: ChildProcess | null = null;
  private buffer = "";
  private nextId = 1;
  private starting: Promise<boolean> | null = null;
  private closed = false;
  private startedOnce = false;
  private readonly queue: MuxJob[] = [];
  private inflight: MuxJob | null = null;

  constructor(
    private readonly bin: string,
    private readonly projectPath?: string,
    private readonly spawnImpl: typeof spawn = spawn,
    private readonly cwd?: string,
  ) {}

  get alive(): boolean {
    return !this.closed && this.child !== null && this.child.exitCode === null;
  }

  get fixedProjectPath(): string | undefined {
    return this.projectPath;
  }

  async start(): Promise<boolean> {
    if (this.starting) return this.starting;
    if (this.alive) return true;
    if (this.closed) return false;
    this.starting = this.spawnMux();
    try {
      return await this.starting;
    } finally {
      this.starting = null;
    }
  }

  private spawnMux(): Promise<boolean> {
    return new Promise((resolve) => {
      const args = ["mux"];
      if (this.projectPath) args.push("--project-path", this.projectPath);
      let settled = false;
      const finish = (ok: boolean) => {
        if (settled) return;
        settled = true;
        resolve(ok);
      };
      let child: ChildProcess;
      try {
        child = this.spawnImpl(this.bin, args, {
          stdio: ["pipe", "pipe", "pipe"],
          windowsHide: true,
          cwd: this.cwd,
          env: {
            ...process.env,
            PI_UNITY_CLIENT: "pi-ext",
          },
        });
      } catch {
        finish(false);
        return;
      }
      this.child = child;
      this.closed = false;
      this.startedOnce = true;
      child.stdout?.setEncoding("utf8");
      child.stdout?.on("data", (chunk: string) => {
        if (this.child !== child) return;
        this.onStdout(chunk);
      });
      child.stderr?.resume();
      child.once("error", () => {
        if (this.child !== child) return;
        this.failTransport("mux 进程错误");
        finish(false);
      });
      child.once("exit", () => {
        if (this.child !== child) return;
        this.failTransport("mux 进程退出");
        finish(false);
      });
      child.stdin?.once("error", () => {
        if (this.child !== child) return;
        this.failTransport("mux stdin 错误");
        finish(false);
      });
      if (child.pid) {
        queueMicrotask(() => finish(this.alive));
      }
    });
  }

  private onStdout(chunk: string): void {
    this.buffer += chunk;
    if (this.buffer.length > MAX_MUX_STDOUT_CHARS) {
      this.failTransport("mux stdout 过大");
      return;
    }
    let nl = this.buffer.indexOf("\n");
    while (nl >= 0) {
      const line = this.buffer.slice(0, nl).replace(/\r$/, "");
      this.buffer = this.buffer.slice(nl + 1);
      if (line.trim()) this.dispatchLine(line);
      nl = this.buffer.indexOf("\n");
    }
  }

  private dispatchLine(line: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      this.failTransport("协议损坏");
      return;
    }
    const result = parseMuxReply(parsed);
    if (!result) {
      this.failTransport("协议损坏");
      return;
    }
    const obj = asRecord(parsed);
    const id = obj && typeof obj.id === "string" ? obj.id : "";
    const job = this.inflight;
    if (!job || job.id !== id) return;
    this.finishJob(job, { status: "completed", written: true, retryAllowed: false, result });
    this.inflight = null;
    this.pump();
  }

  private finishJob(job: MuxJob, value: MuxCallResult): void {
    if (job.settled) return;
    job.settled = true;
    if (job.timer) {
      clearTimeout(job.timer);
      job.timer = undefined;
    }
    if (job.onAbort) {
      job.signal?.removeEventListener("abort", job.onAbort);
      job.onAbort = undefined;
    }
    job.resolve(value);
  }

  private dropQueued(reason: string): void {
    const queued = this.queue.splice(0);
    for (const job of queued) {
      this.finishJob(job, {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: reason, error_type: "other", exitCode: 1 },
      });
    }
  }

  private killChild(): void {
    const child = this.child;
    this.child = null;
    this.buffer = "";
    if (child && child.exitCode === null) {
      try {
        child.kill();
      } catch {}
    }
  }

  private failTransport(reason: string): void {
    const current = this.inflight;
    this.inflight = null;
    this.killChild();
    if (current?.dispatched) {
      this.finishJob(current, {
        status: "in-flight-lost",
        written: true,
        retryAllowed: false,
        result: {
          ok: false,
          error: reason,
          error_type: reason.startsWith("mux 超时") ? "timeout" : "other",
          exitCode: 1,
        },
      });
    } else if (current) {
      this.finishJob(current, {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "mux 未发送已终止", error_type: "other", exitCode: 1 },
      });
    }
    this.dropQueued("mux 未发送已终止");
  }

  private pump(): void {
    if (this.closed || this.inflight) return;
    const job = this.queue.shift();
    if (!job) return;
    this.inflight = job;
    void this.dispatch(job);
  }

  private async dispatch(job: MuxJob): Promise<void> {
    if (job.signal?.aborted) {
      this.finishJob(job, {
        status: "aborted",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "aborted", error_type: "other", exitCode: 1 },
      });
      if (this.inflight === job) this.inflight = null;
      this.pump();
      return;
    }
    if (this.closed) {
      this.finishJob(job, {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "mux 未发送已终止", error_type: "other", exitCode: 1 },
      });
      if (this.inflight === job) this.inflight = null;
      return;
    }
    const started = await this.start();
    if (job.settled || this.closed || this.inflight !== job) {
      if (this.inflight === job) this.inflight = null;
      this.pump();
      return;
    }
    if (!started || !this.alive || !this.child?.stdin) {
      this.finishJob(job, {
        status: this.startedOnce ? "queue-dropped" : "unavailable-before-start",
        written: false,
        retryAllowed: !this.startedOnce,
        result: {
          ok: false,
          error: this.startedOnce ? "mux 未发送已终止" : "mux 不可用",
          error_type: "other",
          exitCode: 1,
        },
      });
      if (this.inflight === job) this.inflight = null;
      if (this.startedOnce) this.dropQueued("mux 未发送已终止");
      else this.pump();
      return;
    }
    if (job.settled || job.signal?.aborted) {
      if (!job.settled) {
        this.finishJob(job, {
          status: "aborted",
          written: false,
          retryAllowed: false,
          result: { ok: false, error: "aborted", error_type: "other", exitCode: 1 },
        });
      }
      if (this.inflight === job) this.inflight = null;
      this.pump();
      return;
    }
    const id = String(this.nextId++);
    job.id = id;
    job.dispatched = true;
    this.inflight = job;
    job.timer = setTimeout(() => {
      this.failTransport(`mux 超时 ${job.timeoutMs}ms`);
    }, job.timeoutMs);
    const payload = `${JSON.stringify({ id, argv: job.argv })}\n`;
    const child = this.child;
    const stdin = child?.stdin;
    if (!child || !stdin) {
      this.failTransport("mux stdin 写入失败");
      return;
    }
    try {
      stdin.write(payload, (err) => {
        if (this.child !== child) return;
        if (err) this.failTransport("mux stdin 写入失败");
      });
    } catch {
      if (this.child === child) this.failTransport("mux stdin 写入失败");
    }
  }

  async request(
    argv: string[],
    options: { timeoutMs?: number; signal?: AbortSignal } = {},
  ): Promise<MuxCallResult> {
    if (options.signal?.aborted) {
      return {
        status: "aborted",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "aborted", error_type: "other", exitCode: 1 },
      };
    }
    if (this.closed) {
      return {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "mux 未发送已终止", error_type: "other", exitCode: 1 },
      };
    }
    const timeoutMs = options.timeoutMs && options.timeoutMs > 0 ? options.timeoutMs : DEFAULT_MUX_TIMEOUT_MS;
    return new Promise<MuxCallResult>((resolve) => {
      const job: MuxJob = {
        argv,
        timeoutMs,
        signal: options.signal,
        resolve,
        dispatched: false,
        settled: false,
      };
      job.onAbort = () => {
        if (job.dispatched) {
          this.failTransport("aborted");
          return;
        }
        const idx = this.queue.indexOf(job);
        if (idx >= 0) this.queue.splice(idx, 1);
        const wasInflight = this.inflight === job;
        this.finishJob(job, {
          status: "aborted",
          written: false,
          retryAllowed: false,
          result: { ok: false, error: "aborted", error_type: "other", exitCode: 1 },
        });
        if (wasInflight) {
          this.inflight = null;
          this.pump();
        }
      };
      options.signal?.addEventListener("abort", job.onAbort);
      this.queue.push(job);
      this.pump();
    });
  }

  async shutdown(): Promise<void> {
    this.closed = true;
    this.dropQueued("mux 未发送已终止");
    const current = this.inflight;
    if (current && !current.dispatched) {
      this.inflight = null;
      this.finishJob(current, {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: "mux 未发送已终止", error_type: "other", exitCode: 1 },
      });
    }
    const child = this.child;
    if (!child) {
      if (this.inflight) {
        this.finishJob(this.inflight, {
          status: "queue-dropped",
          written: false,
          retryAllowed: false,
          result: { ok: false, error: "mux 未发送已终止", error_type: "other", exitCode: 1 },
        });
        this.inflight = null;
      }
      return;
    }
    const stdin = child.stdin;
    if (stdin && stdin.writable) {
      try {
        stdin.write(`${JSON.stringify({ id: "quit", quit: true })}\n`);
      } catch {}
      try {
        stdin.end();
      } catch {}
    }
    await Promise.race([
      new Promise<void>((resolve) => child.once("exit", () => resolve())),
      new Promise<void>((resolve) => setTimeout(resolve, 500)),
    ]);
    if (child.exitCode === null) {
      try {
        child.kill();
      } catch {}
    }
    this.failTransport("mux 未发送已终止");
  }
}

let activeMux: MuxClient | null = null;

export function setActiveMux(client: MuxClient | null): void {
  activeMux = client;
}

export function getActiveMux(): MuxClient | null {
  return activeMux;
}

async function execPiUnity(
  args: string[],
  options: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal } = {},
): Promise<CliExecutionResult> {
  const bin = findPiUnityBinary();
  const cliArgs = [...args, "--json"];
  if (options.projectPath) {
    cliArgs.push("--project-path", options.projectPath);
  }
  try {
    const { stdout } = await execFileAsync(bin, cliArgs, {
      encoding: "utf8",
      timeout: options.timeoutMs ?? 120000,
      maxBuffer: 32 * 1024 * 1024,
      windowsHide: true,
      signal: options.signal,
      env: {
        ...process.env,
        PI_UNITY_CLIENT: "pi-ext",
      },
    });
    return parseCliJson(stdout);
  } catch (err: unknown) {
    const e = err as {
      stdout?: string | Buffer;
      stderr?: string | Buffer;
      status?: number | null;
      code?: string | number;
      message?: string;
    };
    const stdout = typeof e.stdout === "string" ? e.stdout : Buffer.isBuffer(e.stdout) ? e.stdout.toString("utf8") : "";
    if (stdout.trim()) {
      return parseCliJson(stdout);
    }
    const stderr = typeof e.stderr === "string" ? e.stderr.trim() : Buffer.isBuffer(e.stderr) ? e.stderr.toString("utf8").trim() : "";
    const message = err instanceof Error ? err.message : String(err);
    const code = e.status ?? e.code;
    const exitCode = typeof code === "number" ? code : 1;
    return { ok: false, error: stderr || message, exitCode };
  }
}

/** Execute pi-unity CLI asynchronously and return parsed JSON */
function sameProjectPath(a: string, b: string): boolean {
  return resolve(a) === resolve(b);
}

export async function runPiUnityCli(
  args: string[],
  options: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal } = {},
): Promise<CliExecutionResult> {
  if (options.signal?.aborted) {
    return { ok: false, error: "aborted", error_type: "other", exitCode: 1 };
  }
  const mux = activeMux;
  if (mux) {
    const muxProject = mux.fixedProjectPath;
    if (options.projectPath && (!muxProject || !sameProjectPath(options.projectPath, muxProject))) {
      return {
        ok: false,
        error: muxProject
          ? `mux 已绑定工程 ${muxProject}，不能改用 ${options.projectPath}`
          : `mux 工程未固定，不能传 --project-path ${options.projectPath}`,
        error_type: "usage",
        help: ["不要在 mux 会话中传不同的 --project-path"],
        exitCode: 2,
      };
    }
    const call = await mux.request(args, options);
    if (call.retryAllowed) return execPiUnity(args, options);
    return call.result;
  }
  return execPiUnity(args, options);
}

function formatResult(cliRes: CliExecutionResult): { content: Array<{ type: "text"; text: string }>; details: unknown } {
  if (!cliRes.ok) {
    const payload = {
      ok: false,
      error: cliRes.error || "pi-unity command failed",
      error_type: cliRes.error_type,
      help: cliRes.help ?? [],
      exitCode: cliRes.exitCode ?? 1,
    };
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

/** Push `--flag <value>` when the value is present (empty string is skipped). */
function pushArg(args: string[], flag: string, value: string | number | undefined) {
  if (value === undefined || value === "") return;
  args.push(flag, String(value));
}

export function pushViewArgs(args: string[], params: { fields?: unknown; full?: unknown }) {
  if (typeof params.fields === "string" && params.fields.trim()) {
    args.push("--fields", params.fields.trim());
  }
  if (params.full === true) args.push("--full");
}

const VIEW_FIELDS = {
  fields: Type.String({ description: "追加字段，逗号分隔" }),
  full: Type.Boolean({ description: "输出完整 schema" }),
};

/** Tool parameter schema. Names in `required` stay required; the rest are optional. */
function toolSchema(fields: Record<string, any>, required: string[] = []): any {
  return Type.Object(
    Object.fromEntries(
      Object.entries(fields).map(([name, def]) => [
        name,
        required.includes(name) ? def : Type.Optional(def),
      ]),
    ),
  );
}

export default function (pi: ExtensionAPI) {
  let settings = loadUnityHarnessSettings();
  const dynamicallyRegisteredTools = new Set<string>();
  let sessionMux: MuxClient | null = null;

  function assertEnabled() {
    settings = loadUnityHarnessSettings();
    if (!settings.enabled) {
      throw new Error(
        `pi-unity-harness is disabled in settings. Enable it with /unity-harness-settings enable or set PI_UNITY_HARNESS_ENABLED=1. Paths: ${describeSettingsPaths()}`,
      );
    }
  }

  function toolTimeout(params: { timeoutMs?: unknown }, fallback: number, extra = 0): number {
    const n = typeof params.timeoutMs === "number" && Number.isFinite(params.timeoutMs)
      ? params.timeoutMs
      : fallback;
    return n + extra + 1000;
  }

  async function runTool(
    args: string[],
    options?: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal },
  ) {
    assertEnabled();
    return formatResult(await runPiUnityCli(args, options));
  }

  async function stopSessionMux() {
    const mux = sessionMux;
    sessionMux = null;
    if (getActiveMux() === mux) setActiveMux(null);
    if (mux) await mux.shutdown();
  }

  function startSessionMux(cwd: string) {
    const projectPath = process.env.UNITY_PROJECT_PATH?.trim() || undefined;
    sessionMux = new MuxClient(findPiUnityBinary(), projectPath, spawn, cwd);
    setActiveMux(sessionMux);
    void sessionMux.start();
  }

  async function refreshDynamicPipelineTools(signal?: AbortSignal) {
    try {
      const res = await runPiUnityCli(["list-commands", "--full"], { timeoutMs: 15000, signal });
      if (!res.ok || !res.result) return;
      const list = res.result as PipelineCommandList;
      const filtered = filterPipelineCommands(list);

      for (const cmd of filtered) {
        const toolName = normalizePipelineToolName(cmd.name);
        if (dynamicallyRegisteredTools.has(toolName)) continue;

        try {
          const schema = schemaToTypeBox(Type, cmd.schema, cmd.parameters);
          pi.registerTool({
            name: toolName,
            label: `Unity ${cmd.name}`,
            description: cmd.description || `Execute Unity Pipeline command: ${cmd.name}`,
            promptSnippet: `Use ${toolName} to execute the ${cmd.name} pipeline command.`,
            parameters: schema,
            async execute(_toolCallId, params, signal) {
              return runTool(["pipeline", cmd.name, "--params-json", JSON.stringify(params ?? {})], { signal });
            },
          });
          dynamicallyRegisteredTools.add(toolName);
        } catch {}
      }
    } catch {
      // Ignore if Unity is offline during startup
    }
  }

  pi.on("session_start", async (_event, ctx) => {
    settings = loadUnityHarnessSettings();
    await stopSessionMux();
    if (settings.enabled) {
      startSessionMux(ctx.cwd);
      void refreshDynamicPipelineTools();
    }
  });

  pi.on("before_agent_start", (event) => {
    settings = loadUnityHarnessSettings();
    if (!settings.enabled) return;
    if (event.systemPrompt.includes(UNITY_VERIFY_WORKFLOW_PROMPT)) return;
    return { systemPrompt: `${event.systemPrompt}\n\n${UNITY_VERIFY_WORKFLOW_PROMPT}` };
  });

  pi.on("session_shutdown", async () => {
    await stopSessionMux();
  });

  // ---- /unity-harness-settings command ----
  pi.registerCommand("unity-harness-settings", {
    description: "Inspect or toggle pi-unity-harness settings (enabled/disabled)",
    handler: async (args, ctx) => {
      const enable = coerceEnabled(args.trim());
      if (enable === undefined) {
        ctx.ui.notify(JSON.stringify(inspectUnityHarnessSettings(), null, 2), "info");
        return;
      }
      persistEnabled(enable, "global", ctx.cwd);
      settings = loadUnityHarnessSettings(ctx.cwd);
      if (!enable) {
        await stopSessionMux();
      } else {
        await stopSessionMux();
        startSessionMux(ctx.cwd);
        void refreshDynamicPipelineTools();
      }
      ctx.ui.notify(`pi-unity-harness ${enable ? "enabled" : "disabled"}.`, "info");
    },
  });

  // ---- unity_ping ----
  pi.registerTool({
    name: "unity_ping",
    label: "Unity Ping",
    description: "Ping the Unity native broker via pi-unity CLI to verify connection responsiveness.",
    promptSnippet: "Use unity_ping to probe whether the Unity Editor is running and responding.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 5000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["ping", "--timeout", String(params.timeoutMs ?? 5000)];
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 5000), signal });
    },
  });

  // ---- unity_status ----
  pi.registerTool({
    name: "unity_status",
    label: "Unity Status",
    description: "Get Unity Editor and broker status, domain reload state, and modal window probe via pi-unity status.",
    promptSnippet: "Use unity_status to check Editor state, domain reload generation, and modal dialogs.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 5000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["status", "--timeout", String(params.timeoutMs ?? 5000)];
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 5000), signal });
    },
  });

  // ---- unity_snapshot ----
  pi.registerTool({
    name: "unity_snapshot",
    label: "Unity Snapshot",
    description: "Get context snapshot of active scene hierarchy, selection, components, and console logs via pi-unity snapshot.",
    promptSnippet: "Use unity_snapshot to observe scene hierarchy, selection, and error logs before and after changes.",
    parameters: toolSchema({
      depth: Type.Integer({ description: "Max hierarchy depth to traverse (default: 3)" }),
      maxNodes: Type.Integer({ description: "Max GameObjects to include (default: 500)" }),
      logLimit: Type.Integer({ description: "Max recent logs to include (default: 50)" }),
      logLevel: Type.String({ description: "Log level filter: error, warning, or all (default: error)" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 20000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["snapshot"];
      pushArg(args, "--depth", params.depth);
      pushArg(args, "--max-nodes", params.maxNodes);
      pushArg(args, "--log-limit", params.logLimit);
      pushArg(args, "--log-level", params.logLevel);
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 20000), signal });
    },
  });

  // ---- unity_eval ----
  pi.registerTool({
    name: "unity_eval",
    label: "Unity Eval",
    description: "Execute C# code or expression in the Unity Editor main thread via pi-unity eval.",
    promptSnippet: "Use unity_eval for short C# probes on the Unity main thread; prefer unity_eval_file for multi-line scripts.",
    parameters: toolSchema({
      code: Type.String({ description: "C# code or expression to execute in Unity Editor." }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
      ...VIEW_FIELDS,
    }, ["code"]),
    async execute(_toolCallId, params, signal) {
      const args = ["eval", params.code];
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 30000), signal });
    },
  });

  // ---- unity_eval_file ----
  pi.registerTool({
    name: "unity_eval_file",
    label: "Unity Eval File",
    description: "Execute a C# script file or .repl file in the Unity Editor main thread via pi-unity eval -f.",
    promptSnippet: "Use unity_eval_file to run multi-line C# from a file (e.g. Temp/PiUnityHarness/AgentScratch/*.repl).",
    parameters: toolSchema({
      filePath: Type.String({ description: "Path to .cs or .repl file (relative to project root or absolute)." }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
      ...VIEW_FIELDS,
    }, ["filePath"]),
    async execute(_toolCallId, params, signal) {
      const args = ["eval", "-f", params.filePath];
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 30000), signal });
    },
  });

  // ---- unity_recompile ----
  pi.registerTool({
    name: "unity_recompile",
    label: "Unity Recompile",
    description: "Trigger Unity script compilation and wait for domain reload to complete via pi-unity compile.",
    promptSnippet: "Use unity_recompile after C# edits to trigger compilation and await domain reload.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 120000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["compile"];
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 120000, 10000), signal });
    },
  });

  // ---- unity_list_commands ----
  pi.registerTool({
    name: "unity_list_commands",
    label: "Unity List Commands",
    description: "List all registered Unity Pipeline [CliCommand] handlers via pi-unity list-commands.",
    promptSnippet: "Use unity_list_commands to discover available pipeline commands and parameter schemas.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 15000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      assertEnabled();
      const args = ["list-commands"];
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      void refreshDynamicPipelineTools(signal);
      return runTool(args, { timeoutMs: toolTimeout(params, 15000), signal });
    },
  });

  // ---- unity_pipeline ----
  pi.registerTool({
    name: "unity_pipeline",
    label: "Unity Pipeline",
    description: "Execute a registered Unity Pipeline [CliCommand] via pi-unity pipeline. Omit command/name to list available commands.",
    promptSnippet: "Use unity_pipeline to run pipeline commands like uitree_*, assets_*, input_*, etc.",
    parameters: toolSchema({
      command: Type.String({ description: "Name of the pipeline command to execute (alias for name)." }),
      name: Type.String({ description: "Name of the pipeline command to execute." }),
      params: Type.Unsafe({
        type: "object",
        description: "Parameters object passed to the pipeline command (alias for parameters).",
        additionalProperties: true,
      }),
      parameters: Type.Unsafe({
        type: "object",
        description: "Parameters object passed to the pipeline command.",
        additionalProperties: true,
      }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const cmdName = (params.name || params.command || "").trim();
      if (!cmdName) return runTool(["list-commands"], { timeoutMs: toolTimeout(params, 15000), signal });
      const args = ["pipeline", cmdName];
      const paramObj = params.parameters || params.params;
      if (paramObj) args.push("--params-json", JSON.stringify(paramObj));
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 30000), signal });
    },
  });

  // ---- unity_run_tests ----
  pi.registerTool({
    name: "unity_run_tests",
    label: "Unity Run Tests",
    description: "Run Unity UTF test suite (EditMode or PlayMode) via pi-unity run-tests.",
    promptSnippet: "Use unity_run_tests to execute EditMode or PlayMode tests and verify code changes.",
    parameters: toolSchema({
      mode: Type.String({ description: "Test mode: edit or play (default: edit)" }),
      filter: Type.String({ description: "Optional test name or namespace filter pattern" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 330000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["run-tests"];
      pushArg(args, "--mode", params.mode);
      pushArg(args, "--filter", params.filter);
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 330000, 10000), signal });
    },
  });

  // ---- unity_observe ----
  pi.registerTool({
    name: "unity_observe",
    label: "Unity Observe",
    description: "Multi-frame visual observation in PlayMode via pi-unity observe.",
    promptSnippet: "Use unity_observe for multi-frame perceptual verification and change detection in PlayMode.",
    parameters: toolSchema({
      frames: Type.Integer({ description: "Number of frames to capture (default: 3)" }),
      intervalMs: Type.Number({ description: "Interval between frames in ms (default: 160)" }),
      overlay: Type.String({ description: "Overlay mode: grid, annotations, both, or none (default: both)" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["observe"];
      pushArg(args, "--frames", params.frames);
      pushArg(args, "--interval", params.intervalMs);
      pushArg(args, "--overlay", params.overlay);
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 30000), signal });
    },
  });

  // ---- unity_capture ----
  pi.registerTool({
    name: "unity_capture",
    label: "Unity Capture",
    description: "Capture a single GameView or SceneView screenshot via pi-unity capture.",
    promptSnippet: "Use unity_capture for fast single-frame viewport screenshot capture.",
    parameters: toolSchema({
      mode: Type.String({ description: "Viewport mode: game or scene (default: game)" }),
      outPath: Type.String({ description: "Output path for the saved image file" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["capture"];
      pushArg(args, "--mode", params.mode);
      pushArg(args, "--out", params.outPath);
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 30000), signal });
    },
  });

  // ---- unity_timeline ----
  pi.registerTool({
    name: "unity_timeline",
    label: "Unity Timeline",
    description: "Query recent operations timeline and audit history from the broker via pi-unity timeline.",
    promptSnippet: "Use unity_timeline to review recent command audit history and execution times.",
    parameters: toolSchema({
      limit: Type.Integer({ description: "Number of timeline entries to return (default: 20)" }),
      success: Type.String({ description: "Filter status: all, success, or failure (default: all)" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 15000" }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params, signal) {
      const args = ["timeline"];
      pushArg(args, "--limit", params.limit);
      pushArg(args, "--success", params.success);
      pushArg(args, "--timeout", params.timeoutMs);
      pushViewArgs(args, params);
      return runTool(args, { timeoutMs: toolTimeout(params, 15000), signal });
    },
  });
}
