import { spawn, type ChildProcess } from "node:child_process";
import { paths, PathConversionError } from "../paths.ts";
import { decide } from "./restart-policy.ts";

export interface CliExecutionResult { ok: boolean; result?: unknown; error?: string; error_type?: string; help?: string[]; exitCode?: number; truncated?: boolean; savedScratchPath?: string; text?: string; }
const DEFAULT_MUX_TIMEOUT_MS = 120000;
const MAX_MUX_STDOUT_CHARS = 32 * 1024 * 1024;
const MAX_MUX_STDERR_TAIL_CHARS = 800;
const MUX_RESTART_BUDGET = 1;
const DIAGNOSTIC_CATEGORY_PATTERNS: Array<[string, RegExp]> = [["panic", /panic/i], ["broken pipe", /broken pipe/i], ["access denied", /access denied/i]];
function sanitizeDiagnosticTail(text: string | undefined, maxChars = 600): string { if (!text) return ""; const raw=String(text); const matched=DIAGNOSTIC_CATEGORY_PATTERNS.filter(([,re])=>re.test(raw)).map(([x])=>x); return (`stderr=${Buffer.byteLength(raw,"utf8")} bytes/${raw.split(/\r\n|\r|\n/).length} lines`+(matched.length?`; matched: ${matched.join(", ")}`:"")).slice(0,maxChars); }
function pathFailure(error: PathConversionError): CliExecutionResult { return { ok:false, error:error.message, error_type:error.code, exitCode:1 }; }
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
  private readonly bin: string;
  private readonly projectPath?: string;
  private readonly spawnImpl: typeof spawn;
  private readonly cwd?: string;
  /** 最近一次 mux 子进程意外退出的 exit code / signal（用于崩溃诊断，有界）。 */
  private lastExit: { code: number | null; signal: string | null } | null = null;
  /** 自最近一次成功业务回复以来已消耗的重启次数；成功回复后归零。 */
  private restartsSinceStable = 0;
  /** mux stderr 的有界尾部（脱敏后用于崩溃诊断）。 */
  private stderrTail = "";

  constructor(
    bin: string,
    projectPath?: string,
    spawnImpl: typeof spawn = spawn,
    cwd?: string,
  ) {
    this.bin = bin;
    this.projectPath = projectPath;
    this.spawnImpl = spawnImpl;
    this.cwd = cwd;
  }

  get alive(): boolean {
    return !this.closed && this.child !== null && this.child.exitCode === null;
  }

  get fixedProjectPath(): string | undefined {
    return this.projectPath;
  }

  get fixedCwd(): string | undefined {
    return this.cwd;
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
      // Convert only process arguments; cwd is interpreted by the host Node process.
      let settled = false;
      const finish = (ok: boolean) => {
        if (settled) return;
        settled = true;
        resolve(ok);
      };
      let child: ChildProcess;
      try {
        if (this.projectPath) args.push("--project-path", paths.cliPath(this.projectPath, this.bin));
        child = this.spawnImpl(this.bin, args, {
          stdio: ["pipe", "pipe", "pipe"],
          windowsHide: true,
          cwd: this.cwd,
          env: paths.env(this.bin, process.env, this.projectPath),
        });
      } catch {
        finish(false);
        return;
      }
      this.child = child;
      this.closed = false;
      child.stdout?.setEncoding("utf8");
      child.stdout?.on("data", (chunk: string) => {
        if (this.child !== child) return;
        this.onStdout(chunk);
      });
      child.stderr?.setEncoding("utf8");
      child.stderr?.on("data", (chunk: string) => {
        if (this.child !== child) return;
        this.stderrTail = (this.stderrTail + chunk).slice(-MAX_MUX_STDERR_TAIL_CHARS);
      });
      const failStart = () => {
        if (this.child === child) this.child = null;
        this.buffer = "";
        try {
          child.kill();
        } catch {}
        finish(false);
      };
      child.once("spawn", () => {
        if (this.child !== child) return;
        this.startedOnce = true;
        finish(true);
      });
      child.once("error", () => {
        if (this.child !== child) return;
        if (!this.startedOnce) {
          failStart();
          return;
        }
        this.failTransport("mux 进程错误");
        finish(false);
      });
      child.once("exit", (code, signal) => {
        if (this.child !== child) return;
        if (!this.startedOnce) {
          failStart();
          return;
        }
        // 意外退出：有界诊断 + 最多一次重启重发未写入的排队请求。
        this.onUnexpectedExit(code, signal === undefined ? null : signal);
        finish(false);
      });
      child.stdin?.once("error", () => {
        if (this.child !== child) return;
        if (!this.startedOnce) {
          failStart();
          return;
        }
        this.failTransport("mux stdin 错误");
        finish(false);
      });
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
    // 一次成功回复 = 通道恢复稳定，重启预算归零，后续崩溃还可再重启一次。
    if (value.status === "completed") this.restartsSinceStable = 0;
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

  /**
   * 崩溃诊断：exitCode/signal + ALLOWLIST 汇总的 stderr + mux_crash 提示。
   * state 描述重启结局（pending = 刚发起、结果未知），成功前不宣称已重启。
   */
  private crashResult(state: "pending" | "failed" | "skipped" = "pending"): CliExecutionResult {
    const code = this.lastExit?.code ?? null;
    const signal = this.lastExit?.signal ?? null;
    const tail = sanitizeDiagnosticTail(this.stderrTail);
    const restartText = state === "failed"
      ? "重启失败，排队请求已放弃（已发出的请求不重放）"
      : state === "skipped"
        ? "未重启（无排队请求或重启预算已耗尽）；下一条业务调用会自动重连"
        : "排队请求正等待一次重启尝试，结果尚未确定（已发出的请求不重放）";
    return {
      ok: false,
      error: `mux 进程异常退出 (exitCode=${code}, signal=${signal})`,
      error_type: "mux_crash",
      exitCode: 1,
      help: [
        `mux_crash: mux 子进程异常退出 (exitCode=${code}, signal=${signal})；${restartText}`,
        tail ? `stderr(已脱敏汇总): ${tail}` : "stderr: (无输出)",
        "若反复 mux_crash，检查 ~/.pi-unity/logs 与控制台日志",
      ],
    };
  }

  /**
   * mux 子进程意外退出：
   * - 已写入的 in-flight 请求视为不确定副作用，绝不重放（in-flight-lost）。
   * - 未写入的排队请求（unshift 回队首，保持 FIFO）可重启一次后重发。
   * - abort / shutdown / 协议损坏 / 写入不确定都不走这里（killChild 已把 this.child 置空，退出事件被拦截）。
   */
  private onUnexpectedExit(code: number | null, signal: string | null): void {
    this.lastExit = { code, signal };
    this.buffer = "";
    this.child = null;
    if (this.closed) return; // shutdown/abort 的清理路径自己负责，不重启不重发

    const current = this.inflight;
    this.inflight = null;
    if (current?.dispatched) {
      // in-flight 已写入：副作用不确定，绝不重放（诊断先不宣称重启成功）。
    } else if (current) {
      // 未写入（dispatch 尚未写帧）：视为未触碰，回到队首保 FIFO。
      this.queue.unshift(current);
    }

    // 先定重启决策再出诊断：无排队请求或预算耗尽时不会重启，文本不得宣称已重启。
    const policy = decide({ restartsSinceStable: this.restartsSinceStable, budget: MUX_RESTART_BUDGET, inflightDispatched: current?.dispatched === true, queueLength: this.queue.length, lastExit: this.lastExit ?? { code, signal } });
    const restartable = policy.kind === "restart" || (policy.kind === "fail_inflight_keep_queue" && this.queue.length > 0 && this.restartsSinceStable < MUX_RESTART_BUDGET);
    const state: "pending" | "skipped" = restartable ? "pending" : "skipped";
    if (current?.dispatched) {
      this.finishJob(current, {
        status: "in-flight-lost",
        written: true,
        retryAllowed: false,
        result: this.crashResult(state),
      });
    }

    if (restartable) {
      this.restartsSinceStable += 1;
      void this.start().then((ok) => {
        if (!ok || !this.alive) {
          this.dropQueuedWithCrash(`mux 重启失败 (exitCode=${code}, signal=${signal})`, "failed");
          return;
        }
        this.pump();
      });
      return;
    }

    if (current?.dispatched && this.queue.length === 0) return; // 无可重发项，下一条业务调用自然重连
    this.dropQueuedWithCrash(this.crashResult(state).error, state);
  }

  private dropQueuedWithCrash(reason: string, state: "pending" | "failed" | "skipped"): void {
    const queued = this.queue.splice(0);
    const help = this.crashResult(state).help;
    for (const job of queued) {
      this.finishJob(job, {
        status: "queue-dropped",
        written: false,
        retryAllowed: false,
        result: { ok: false, error: reason, error_type: "mux_crash", exitCode: 1, help },
      });
    }
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
      const canFallback = !this.startedOnce;
      this.finishJob(job, {
        status: canFallback ? "unavailable-before-start" : "queue-dropped",
        written: false,
        retryAllowed: canFallback,
        result: {
          ok: false,
          error: canFallback ? "mux 不可用" : "mux 未发送已终止",
          error_type: "other",
          exitCode: 1,
        },
      });
      if (this.inflight === job) this.inflight = null;
      this.dropQueued("mux 未发送已终止");
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
    try {
      argv = paths.argv(argv, this.bin);
      if (this.projectPath) paths.cliPath(this.projectPath, this.bin);
      paths.env(this.bin, process.env, this.projectPath);
    } catch (error) {
      if (!(error instanceof PathConversionError)) throw error;
      return { status: "queue-dropped", written: false, retryAllowed: false, result: pathFailure(error) };
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
