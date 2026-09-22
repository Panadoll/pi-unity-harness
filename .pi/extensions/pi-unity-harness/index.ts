import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFile, execFileSync, spawn, type ChildProcess } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { paths, PathConversionError } from "./paths.ts";

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

### 观察顺序：速度模式优先，截图是升级不是默认
- UI / 交互任务先走 uitree：unity_pipeline uitree_snapshot -p interactive_only=true 或 uitree_find；树对不上再升 unity_snapshot / unity_eval_file，最后才 unity_observe / unity_capture。
- 不要默认每步截图：截图占用上下文，capture 结果里的 embed 元数据只是建议、宿主仍可能内联原图；只有画面/坐标/自绘 UI 确实需要核对时才截。
- 拖拽优先 unity_pipeline input_drag（一次调用内部插值），不要手动拆 start/move/end 坐标步。
- assets_refresh 是异步的且会触发导入/重载：调用后必须先 unity_recompile 再继续任何 managed 调用。
- Unity 工具只从主会话调用：broker 单客户端，子代理 / 小模型并行不要直接调 unity_*。
`.trim();

export const HARNESS_PACKAGE_NAME = "com.pi.unity-harness";
export const PIPELINE_PACKAGE_NAME = "com.unity.pipeline";
export const PIPELINE_PACKAGE_VERSION = "0.6.0-exp.1";
export const PIPELINE_COMPAT_PACKAGE_NAME = "com.pi.pipeline.compat";
export const PIPELINE_COMPAT_INPUTSYSTEM_VERSION = "1.7.0";

export interface UnityInstance {
  projectPath: string;
  pid: number;
  bridgeReady: boolean;
  bridgeInfo?: { pipe?: string; token?: string; statePlaneName?: string };
}

export function discoverUnityInstances(): UnityInstance[] {
  if (process.platform !== "win32" && !paths.wsl) return [];

  try {
    const output = execFileSync(
      "powershell.exe",
      [
        "-NoProfile", "-NonInteractive", "-Command",
        `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Get-CimInstance Win32_Process -Filter "name='Unity.exe'" | ForEach-Object { $procId = $_.ProcessId; $cmd = if ($_.CommandLine) { $_.CommandLine } else { '' }; Write-Output "PID:$procId"; Write-Output "CMD:$cmd"; Write-Output "---" }`,
      ],
      { encoding: "utf8", timeout: 15000, stdio: ["ignore", "pipe", "ignore"] },
    );

    const instances: UnityInstance[] = [];
    const blocks = output.split("---").map((b) => b.trim()).filter(Boolean);

    for (const block of blocks) {
      const pidMatch = block.match(/PID:(\d+)/);
      const cmdMatch = block.match(/CMD:(.*)/s);
      if (!pidMatch) continue;

      const pid = parseInt(pidMatch[1], 10);
      const cmdLine = cmdMatch ? cmdMatch[1].trim() : "";

      if (/AssetImportWorker|-adb2|(^|\s)-batchMode(\s|$)/i.test(cmdLine)) continue;

      const rawPath = unityProjectFromCommandLine(cmdLine);
      if (!rawPath) continue;
      const projectPath = paths.host(rawPath);
      if (instances.some((i) => sameProjectPath(i.projectPath, projectPath))) continue;

      const bridgePath = join(projectPath, "Library", "PiUnityHarness", "bridge.json");
      let bridgeReady = false;
      let bridgeInfo: any = undefined;

      if (existsSync(bridgePath)) {
        try {
          let text = readFileSync(bridgePath, "utf8");
          if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
          const raw = JSON.parse(text);
          if (raw.pipe && raw.token) {
            bridgeReady = true;
            bridgeInfo = raw;
          }
        } catch {}
      }

      instances.push({ projectPath, pid, bridgeReady, bridgeInfo });
    }

    return instances;
  } catch (error) {
    if (error instanceof PathConversionError) throw error;
    throw new Error("unity_discovery_failed: Cannot scan Windows Unity processes. Check powershell.exe / WSL interop, or select a known project path explicitly.");
  }
}

export function unityProjectFromCommandLine(commandLine: string): string | undefined {
  const match = commandLine.match(/(?:^|\s)"?-(?:projectPath|createproject)"?\s+(?:"([^"]+)"|(\S+))/i);
  return match?.[1] ?? match?.[2];
}

function readManifest(projectPath: string): any | null {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) return null;
  try {
    let text = readFileSync(manifestPath, "utf8");
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    return JSON.parse(text);
  } catch {
    return null;
  }
}

export function readProjectUnityVersion(projectPath: string): string | undefined {
  const versionPath = join(projectPath, "ProjectSettings", "ProjectVersion.txt");
  if (!existsSync(versionPath)) return undefined;
  try {
    const text = readFileSync(versionPath, "utf8");
    return text.match(/m_EditorVersion:\s*([^\r\n]+)/)?.[1]?.trim();
  } catch {
    return undefined;
  }
}

export function parseUnityMajorVersion(versionString: string | undefined): number | undefined {
  if (!versionString) return undefined;
  const m = versionString.match(/^(\d+)/);
  return m ? parseInt(m[1], 10) : undefined;
}

export interface PipelineInstallStatus {
  installed: boolean;
  packageName?: string;
  flavor?: "official" | "compat";
  version?: string;
  source?: "embedded" | "local" | "registry";
}

export function getPipelineInstallStatus(projectPath: string): PipelineInstallStatus {
  const manifest = readManifest(projectPath);
  const deps = manifest?.dependencies ?? {};

  const pipelineManifest = deps[PIPELINE_PACKAGE_NAME];
  const embeddedPackageJson = join(projectPath, "Packages", PIPELINE_PACKAGE_NAME, "package.json");
  let embeddedVersion: string | undefined;
  let embeddedDisplay: string | undefined;

  if (existsSync(embeddedPackageJson)) {
    try {
      let text = readFileSync(embeddedPackageJson, "utf8");
      if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
      const pkg = JSON.parse(text);
      embeddedVersion = pkg.version;
      embeddedDisplay = pkg.displayName;
    } catch {}
  }

  if (embeddedVersion !== undefined || pipelineManifest) {
    const manifestValue = String(pipelineManifest ?? "");
    const isCompat =
      (embeddedDisplay ?? "").toLowerCase().includes("compat") ||
      manifestValue.toLowerCase().includes("compat") ||
      manifestValue.toLowerCase().includes("pi.pipeline.compat");
    return {
      installed: true,
      packageName: PIPELINE_PACKAGE_NAME,
      flavor: isCompat ? "compat" : "official",
      version: embeddedVersion ?? manifestValue,
      source: embeddedVersion !== undefined ? "embedded" : manifestValue.startsWith("file:") ? "local" : "registry",
    };
  }

  return { installed: false };
}

function resolvePackageRepoDir(subpath: string): string | undefined {
  const candidates = [
    resolve(__dirname, "../../../", subpath),
    resolve(process.cwd(), subpath),
    resolve(process.cwd(), "..", "pi-unity-harness", subpath),
  ];
  for (const dir of candidates) {
    if (existsSync(join(dir, "package.json"))) return dir;
  }
  return undefined;
}

export function installPiUnityHarness(projectPath: string, packageSourceDir?: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    return { ok: false, message: `Cannot find Packages/manifest.json: ${projectPath} may not be a Unity project` };
  }

  const manifest = readManifest(projectPath);
  if (!manifest) {
    return { ok: false, message: `Failed to parse Packages/manifest.json: ${manifestPath}` };
  }
  if (!manifest.dependencies) manifest.dependencies = {};

  if (manifest.dependencies[HARNESS_PACKAGE_NAME]) {
    return { ok: true, message: `${HARNESS_PACKAGE_NAME} is already installed (${manifest.dependencies[HARNESS_PACKAGE_NAME]})` };
  }

  const pkgDir = packageSourceDir ? resolve(packageSourceDir) : resolvePackageRepoDir("unity/com.pi.unity-harness");
  if (!pkgDir || !existsSync(join(pkgDir, "package.json"))) {
    return { ok: false, message: `Cannot find ${HARNESS_PACKAGE_NAME} directory. Expected in unity/com.pi.unity-harness` };
  }

  let relPath = relative(dirname(manifestPath), pkgDir).replace(/\\/g, "/");
  if (!relPath.startsWith(".")) relPath = "./" + relPath;

  manifest.dependencies[HARNESS_PACKAGE_NAME] = `file:${relPath}`;

  try {
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return { ok: true, message: `Installed ${HARNESS_PACKAGE_NAME} -> "file:${relPath}". Unity Editor will compile and load the bridge.` };
  } catch (err: any) {
    return { ok: false, message: `Failed to write manifest.json: ${err.message}` };
  }
}

export function installOfficialUnityPipeline(projectPath: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  const manifest = readManifest(projectPath);
  if (!manifest) {
    return { ok: false, message: `Cannot find or parse Packages/manifest.json: ${projectPath}` };
  }
  manifest.dependencies ??= {};
  delete manifest.dependencies[PIPELINE_COMPAT_PACKAGE_NAME];
  manifest.dependencies[PIPELINE_PACKAGE_NAME] = PIPELINE_PACKAGE_VERSION;
  try {
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return { ok: true, message: `Added ${PIPELINE_PACKAGE_NAME}@${PIPELINE_PACKAGE_VERSION}. Unity Editor will resolve the package.` };
  } catch (err: any) {
    return { ok: false, message: `Failed to write manifest.json: ${err.message}` };
  }
}

export function installCompatUnityPipeline(projectPath: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  const manifest = readManifest(projectPath);
  if (!manifest) {
    return { ok: false, message: `Cannot find or parse Packages/manifest.json: ${projectPath}` };
  }
  const compatDir = resolvePackageRepoDir("unity/com.pi.pipeline.compat");
  if (!compatDir) {
    return { ok: false, message: `Cannot find compat package directory (expected unity/com.pi.pipeline.compat)` };
  }

  manifest.dependencies ??= {};
  // 旧版本曾误用 PIPELINE_PACKAGE_NAME 作为依赖 key（包内 name 实为 compat），
  // 导致同一目录被注册两次并刷屏 ArgumentException；这里清理遗留 key 并改回真实包名。
  delete manifest.dependencies[PIPELINE_PACKAGE_NAME];
  delete manifest.dependencies[PIPELINE_COMPAT_PACKAGE_NAME];

  try {
    const targetDir = join(projectPath, "Packages", PIPELINE_PACKAGE_NAME);
    rmSync(targetDir, { recursive: true, force: true });
    cpSync(compatDir, targetDir, { recursive: true, force: true });
    manifest.dependencies[PIPELINE_COMPAT_PACKAGE_NAME] = "file:com.unity.pipeline";
    if (!manifest.dependencies["com.unity.inputsystem"]) {
      manifest.dependencies["com.unity.inputsystem"] = PIPELINE_COMPAT_INPUTSYSTEM_VERSION;
    }
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return {
      ok: true,
      message:
        `Copied compat fork as embedded package ${PIPELINE_PACKAGE_NAME} (Packages/com.unity.pipeline)` +
        ` and ensured com.unity.inputsystem@${PIPELINE_COMPAT_INPUTSYSTEM_VERSION}. Unity Editor will resolve it.`,
    };
  } catch (err: any) {
    return { ok: false, message: `Failed to write manifest / copy compat package: ${err.message}` };
  }
}

export function installUnityPipelineForProject(
  projectPath: string,
  unityMajor: number | undefined,
): { ok: boolean; message: string; flavor?: "official" | "compat" } {
  if (unityMajor !== undefined && unityMajor >= 6000) {
    const r = installOfficialUnityPipeline(projectPath);
    return { ...r, flavor: "official" };
  }
  const r = installCompatUnityPipeline(projectPath);
  return { ...r, flavor: "compat" };
}

/** Find pi-unity CLI binary path */
export function findPiUnityBinary(): string {
  if (process.env.PI_UNITY_BIN) {
    const configured = process.env.PI_UNITY_BIN;
    return /[\\/]/.test(configured) ? paths.host(configured) : configured;
  }

  const workspaceRoot = resolve(__dirname, "../../../");
  const names = process.platform === "win32" || paths.wsl ? ["pi-unity.exe", "pi-unity"] : ["pi-unity"];
  for (const name of names) {
    for (const dir of ["bin", "dist", "native/target/release", "native/target/debug"]) {
      const candidate = join(workspaceRoot, dir, name);
      if (existsSync(candidate)) return candidate;
    }
  }
  return names[0];
}

/**
 * 从 startDir 向上找最近的含 Library/PiUnityHarness/bridge.json 的工程目录。
 * 只探测存在性（不读内容），避免依赖 / 泄露 bridge 里的 token。
 */
export function findNearestBridgeProject(startDir: string): string | undefined {
  let dir = paths.host(startDir);
  for (;;) {
    if (existsSync(join(dir, "Library", "PiUnityHarness", "bridge.json"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) return undefined;
    dir = parent;
  }
}

/**
 * 工程绑定优先级：显式选择（unity_discover）> 最近的本地 bridge 工程 > 继承的 UNITY_PROJECT_PATH。
 * 相对显式路径按 cwd（会话工作目录）解析。
 */
export function resolveSessionProjectPath(
  cwd: string,
  explicit?: string,
  envProject?: string,
): string | undefined {
  if (explicit) return paths.host(explicit, cwd);
  const nearest = findNearestBridgeProject(cwd);
  if (nearest) return nearest;
  const fromEnv = envProject?.trim();
  // 相对 env 路径也统一按会话 cwd 解析（与显式选择一致）。
  return fromEnv ? paths.host(fromEnv, cwd) : undefined;
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
const MAX_MUX_STDERR_TAIL_CHARS = 800;
const MAX_DIAGNOSTIC_TAIL_CHARS = 600;
/** 一次崩溃事件最多重启一次 mux 并重发未写入的排队请求。 */
const MUX_RESTART_BUDGET = 1;
/** 动态 pipeline 工具注册的最小间隔（成功触发的重试也要限速）。 */
const DYNAMIC_TOOL_REFRESH_BACKOFF_MS = 3000;

/**
 * 诊断文本的 SAFE ALLOWLIST 汇总：绝不透传任意原文。stderr 尾部可能夹带未标签密钥、
 * cookie、私钥、跨行切开的秘密片段、路径 / argv 行，黑名单删不干净。
 * 因此只输出固定类别标签 + stderr 总字节数 / 行数等有界统计，未命中类别的内容一律丢弃。
 */
const DIAGNOSTIC_CATEGORY_PATTERNS: Array<[label: string, re: RegExp]> = [
  ["panic", /\bpanic(?:ked|king)?\b/i],
  ["fatal", /\bfatal\b/i],
  ["segfault", /\bseg(?:mentation\s*)?fault\b|\bsigsegv\b/i],
  ["sigabrt", /\bsigabrt\b|\babort(?:ed)?\b/i],
  ["sigbus", /\bsigbus\b/i],
  ["sigill", /\bsigill\b/i],
  ["sigfpe", /\bsigfpe\b/i],
  ["access-violation", /\baccess\s*violation\b|\baccessviolation\b/i],
  ["stack-overflow", /\bstack\s*overflow\b/i],
  ["out-of-memory", /\bout\s*of\s*memory\b|\boom\b/i],
  ["assert-failed", /\bassert(?:ion)?\s+failed\b/i],
  ["deadlock", /\bdeadlock\b/i],
];

export function sanitizeDiagnosticTail(text: string | undefined, maxChars = MAX_DIAGNOSTIC_TAIL_CHARS): string {
  if (!text) return "";
  const raw = String(text);
  const bytes = Buffer.byteLength(raw, "utf8");
  const lines = raw.split(/\r\n|\r|\n/).length;
  const matched = DIAGNOSTIC_CATEGORY_PATTERNS.filter(([, re]) => re.test(raw)).map(([label]) => label);
  let summary = `stderr=${bytes} bytes/${lines} lines`;
  if (matched.length > 0) summary += `; matched: ${matched.join(", ")}`;
  if (summary.length > maxChars) {
    // 防御性封顶：类别词表固定，正常不可能触发。
    summary = `${summary.slice(0, Math.max(0, maxChars - 12))}…[truncated]`;
  }
  return summary;
}

export { MuxClient } from "./mux/client.ts";
import { MuxClient, type MuxCallResult } from "./mux/client.ts";

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


let activeMux: MuxClient | null = null;

export function setActiveMux(client: MuxClient | null): void {
  activeMux = client;
}

export function getActiveMux(): MuxClient | null {
  return activeMux;
}

async function execPiUnity(
  args: string[],
  options: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal; cwd?: string } = {},
): Promise<CliExecutionResult> {
  try {
    const bin = findPiUnityBinary();
    const cliArgs = paths.argv([...args, "--json"], bin);
    if (options.projectPath) cliArgs.push("--project-path", paths.cliPath(options.projectPath, bin));
    const { stdout } = await execFileAsync(bin, cliArgs, {
      encoding: "utf8",
      timeout: options.timeoutMs ?? 120000,
      maxBuffer: 32 * 1024 * 1024,
      windowsHide: true,
      signal: options.signal,
      cwd: options.cwd,
      env: paths.env(bin, process.env, options.projectPath),
    });
    return parseCliJson(stdout);
  } catch (err: unknown) {
    if (err instanceof PathConversionError) return pathFailure(err);
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
    const stderr = typeof e.stderr === "string" ? e.stderr : Buffer.isBuffer(e.stderr) ? e.stderr.toString("utf8") : "";
    const code = e.status ?? e.code;
    const exitCode = typeof code === "number" ? code : 1;
    // err.message 含完整命令行与原始输出，stderr 也可能夹带未标签密钥：一律不透传。
    // 只回固定失败文案 + 退出码 + ALLOWLIST 汇总。
    const summary = sanitizeDiagnosticTail(stderr);
    const message = `pi-unity CLI 启动失败 (exitCode=${exitCode})`;
    return {
      ok: false,
      error: message,
      error_type: "cli_failed",
      exitCode,
      help: summary ? [message, `stderr(已脱敏汇总): ${summary}`] : [message],
    };
  }
}

/** Execute pi-unity CLI asynchronously and return parsed JSON */
export function sameProjectPath(a: string, b: string): boolean {
  return paths.sameProject(a, b);
}

function pathFailure(error: PathConversionError): CliExecutionResult {
  return { ok: false, error: error.message, error_type: error.code, exitCode: 1 };
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
    let matches = !options.projectPath;
    try {
      if (options.projectPath && muxProject) matches = sameProjectPath(options.projectPath, muxProject);
    } catch (error) {
      if (error instanceof PathConversionError) return pathFailure(error);
      throw error;
    }
    if (!matches) {
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
    if (call.retryAllowed) {
      // 回退 exec 必须与 mux 用完全相同的工程与 cwd，避免绑定漂移。
      return execPiUnity(args, {
        ...options,
        projectPath: options.projectPath ?? mux.fixedProjectPath,
        cwd: mux.fixedCwd,
      });
    }
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

export interface UnityHarnessExtensionOptions {
  /** 测试注入：mux 二进制路径（默认 findPiUnityBinary()）。 */
  muxBin?: string;
  /** 测试注入：mux 子进程 spawn 实现。 */
  muxSpawnImpl?: typeof spawn;
  /** 测试注入：Unity 实例扫描（默认 discoverUnityInstances）。 */
  discoverInstances?: () => UnityInstance[];
  /** 测试注入：动态工具发现退避（默认 3000ms）。 */
  dynamicToolRefreshBackoffMs?: number;
}

export default function (pi: ExtensionAPI, opts: UnityHarnessExtensionOptions = {}) {
  let settings = loadUnityHarnessSettings();
  // 只继承扩展创建前的环境绑定；扩展自己写入 UNITY_PROJECT_PATH 的显式选择不得泄漏到新 cwd 会话。
  const inheritedEnvProjectPath = process.env.UNITY_PROJECT_PATH?.trim() || undefined;
  const dynamicToolRefreshBackoffMs = opts.dynamicToolRefreshBackoffMs ?? DYNAMIC_TOOL_REFRESH_BACKOFF_MS;
  const dynamicallyRegisteredTools = new Set<string>();
  let sessionMux: MuxClient | null = null;
  let activeProjectPath: string | undefined;
  // 用户显式选择的工程（仅 /unity-discover <path> 与 unity_discover(projectPath) 设置）；
  // 会话换到新 cwd 时清空，不沿用到新会话。
  let explicitProjectPath: string | undefined;
  // 会话工作目录：工具 execute 拿不到 ctx，相对显式路径都按它解析。
  let sessionCwd = process.cwd();
  // 会话代数：mux 被 stop/切换/禁用时加一，动态工具不再注册进旧会话。
  let sessionGeneration = 0;
  // 动态工具刷新：成功加载后本会话不再重复 list-commands；
  // 发现失败后下一个业务成功可立即补试（<3s 不限速）；stop/切换/禁用时全部重置。
  let dynamicToolsLoaded = false;
  // 启动发现失败只授予一次“首个成功业务立即补试”；该补试失败后恢复有界退避。
  let dynamicImmediateRetryPending = false;
  let dynamicRefreshInflight: { gen: number; promise: Promise<void> } | null = null;
  let lastDynamicRefreshAttempt = 0;
  // 扫描实现（默认真实扫描，测试注入）。
  const discover = opts.discoverInstances ?? discoverUnityInstances;

  async function switchProject(
    newPath: string | undefined,
    ctx?: { cwd?: string },
    opts: { explicit?: boolean } = {},
  ) {
    // 相对显式路径按会话工作目录解析。
    activeProjectPath = newPath ? paths.host(newPath, ctx?.cwd ?? sessionCwd) : undefined;
    if (opts.explicit) explicitProjectPath = activeProjectPath;
    if (activeProjectPath) {
      process.env.UNITY_PROJECT_PATH = activeProjectPath;
    } else {
      delete process.env.UNITY_PROJECT_PATH;
    }
    await stopSessionMux();
    if (settings.enabled) {
      startSessionMux(ctx?.cwd ?? sessionCwd, activeProjectPath);
      void refreshDynamicPipelineTools();
    }
  }

  async function autoConnectUnity(ctx: { ui: any; cwd: string }) {
    if (!ctx?.ui) return;
    const gen = sessionGeneration;
    let instances: UnityInstance[];
    try {
      instances = discover();
    } catch (error) {
      ctx.ui?.setStatus?.("pi-unity", "Unity discovery failed (/unity-discover)");
      ctx.ui?.notify?.(error instanceof Error ? error.message : "unity_discovery_failed", "warn");
      return;
    }
    if (instances.length === 0) {
      ctx.ui?.setStatus?.("pi-unity", "enabled, no Unity detected (/unity-discover)");
      return;
    }
    const readyInstances = instances.filter((i) => i.bridgeReady);
    if (readyInstances.length === 0) {
      ctx.ui?.setStatus?.("pi-unity", `found ${instances.length} Unity, bridge not installed (/unity-install)`);
      ctx.ui?.notify?.(`Found ${instances.length} Unity instance(s) without bridge installed. Run /unity-install to install.`, "warn");
      return;
    }
    if (readyInstances.length > 1) {
      ctx.ui?.setStatus?.("pi-unity", `multiple Unity instances (${readyInstances.length}) (/unity-discover)`);
      ctx.ui?.notify?.(`Found ${readyInstances.length} running Unity instances. Use /unity-discover to choose one.`, "info");
      return;
    }
    // 已有绑定（显式选择 / 最近本地 bridge / 继承 env）优先，扫描结果不覆盖。
    const bound = sessionMux?.fixedProjectPath ?? activeProjectPath;
    if (bound) {
      ctx.ui?.setStatus?.("pi-unity", `unity bridge: ${basename(bound)}`);
      return;
    }
    if (gen !== sessionGeneration || !settings.enabled) return; // 会话已变：本次自动连接作废
    const target = readyInstances[0].projectPath;
    activeProjectPath = paths.host(target, ctx.cwd);
    process.env.UNITY_PROJECT_PATH = activeProjectPath;
    await stopSessionMux();
    if (gen !== sessionGeneration - 1 || !settings.enabled) {
      // 停止期间被 stop/切换/禁用（代数不止我们自己那一次 +1）：作废，不重建。
      if (paths.host(target, ctx.cwd) === activeProjectPath) {
        activeProjectPath = undefined;
        delete process.env.UNITY_PROJECT_PATH;
      }
      return;
    }
    startSessionMux(ctx.cwd, activeProjectPath);
    void refreshDynamicPipelineTools();
    const name = basename(target);
    ctx.ui?.setStatus?.("pi-unity", `unity bridge: ${name}`);
    ctx.ui?.notify?.(`Connected to Unity: ${name}`, "info");
  }

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
    const res = await runPiUnityCli(args, options);
    // 首次成功业务响应后补做动态工具注册（启动期 Unity 未就绪也不丢）。
    if (res.ok) void refreshDynamicPipelineTools(undefined, "business");
    return formatResult(res);
  }

  async function stopSessionMux() {
    sessionGeneration += 1;
    // 动态工具状态随会话重置：停用/切换/禁用后重新发现，且不被旧会话限速。
    dynamicToolsLoaded = false;
    dynamicImmediateRetryPending = false;
    lastDynamicRefreshAttempt = 0;
    const mux = sessionMux;
    sessionMux = null;
    if (getActiveMux() === mux) setActiveMux(null);
    if (mux) await mux.shutdown();
  }

  function startSessionMux(cwd: string, explicit?: string) {
    // 优先级：显式选择 > 最近的本地 bridge 工程 > 继承的 UNITY_PROJECT_PATH。
    const projectPath = resolveSessionProjectPath(cwd, explicit, inheritedEnvProjectPath);
    sessionMux = new MuxClient(opts.muxBin ?? findPiUnityBinary(), projectPath, opts.muxSpawnImpl ?? spawn, cwd);
    setActiveMux(sessionMux);
    void sessionMux.start();
  }

  /** 动态工具发现：启动失败允许首个成功业务立即补试一次；之后按退避限速，同代去重、跨代隔离。 */
  async function refreshDynamicPipelineTools(
    signal?: AbortSignal,
    trigger: "scheduled" | "business" = "scheduled",
  ): Promise<void> {
    if (!settings.enabled) return;
    if (dynamicToolsLoaded) return;
    const gen = sessionGeneration;
    const muxRef = sessionMux;
    const inflight = dynamicRefreshInflight;
    if (inflight) {
      if (inflight.gen === gen) {
        await inflight.promise; // 同代并发去重：等同一尝试即可
        return;
      }
      // 旧会话残留的 in-flight：不等它；其 finally 也不会清掉新会话的槽位（身份校验）。
    }
    const now = Date.now();
    const allowImmediate = trigger === "business" && dynamicImmediateRetryPending;
    if (!allowImmediate && now - lastDynamicRefreshAttempt < dynamicToolRefreshBackoffMs) return;
    if (allowImmediate) dynamicImmediateRetryPending = false; // 只消费一次；补试再失败必须走退避
    lastDynamicRefreshAttempt = now;
    // 快照会话身份：list-commands 完成后会话已被切换/禁用时不再注册。
    const run = (async () => {
      const isCurrent = () => settings.enabled && gen === sessionGeneration && sessionMux === muxRef && getActiveMux() === muxRef;
      try {
        const res = await runPiUnityCli(["list-commands", "--full"], { timeoutMs: 15000, signal });
        if (!res.ok || !res.result) {
          if (isCurrent()) {
            if (trigger === "scheduled") dynamicImmediateRetryPending = true;
          }
          return;
        }
        if (!isCurrent()) return;
        const list = res.result as PipelineCommandList;
        const filtered = filterPipelineCommands(list);

        for (const cmd of filtered) {
          const toolName = normalizePipelineToolName(cmd.name);
          if (dynamicallyRegisteredTools.has(toolName)) continue;
          const policyLabel = cmd.policy?.mutability === "destructive"
            ? "[DESTRUCTIVE] "
            : cmd.policy?.mutability === "write"
              ? "[WRITE] "
              : "";
          const description =
            policyLabel + (cmd.description || `Execute Unity Pipeline command: ${cmd.name}`) +
            (/^assets_refresh/.test(cmd.name)
              ? " 注意：assets_refresh 是异步的，不等待导入/域重载完成；下一步任何 managed 调用前先 unity_recompile。"
              : "");
          const promptSnippet = cmd.name.startsWith("uitree_")
            ? `UI 任务优先 ${toolName}（速度模式），树对不上再升 unity_snapshot / unity_eval_file。`
            : cmd.name === "input_drag"
              ? `拖拽优先 ${toolName}（一次调用内部插值），不要手动拆 start/move/end。`
              : cmd.name.startsWith("input_drag")
                ? `手动分步拖拽命令：直接用 unity_pipeline input_drag 一次完成（内部插值）。`
                : `Use ${toolName} to execute the ${cmd.name} pipeline command.`;

          try {
            const schema = schemaToTypeBox(Type, cmd.schema, cmd.parameters);
            pi.registerTool({
              name: toolName,
              label: `Unity ${cmd.name}`,
              description,
              promptSnippet,
              parameters: schema,
              async execute(_toolCallId, params, toolSignal) {
                return runTool(["pipeline", cmd.name, "--params-json", JSON.stringify(params ?? {})], { signal: toolSignal });
              },
            });
            dynamicallyRegisteredTools.add(toolName);
          } catch {}
        }
        dynamicToolsLoaded = true;
        dynamicImmediateRetryPending = false;
      } catch {
        if (isCurrent()) {
          if (trigger === "scheduled") dynamicImmediateRetryPending = true;
        }
      } finally {
        if (dynamicRefreshInflight?.promise === run) dynamicRefreshInflight = null; // 只清自己的槽位
      }
    })();
    dynamicRefreshInflight = { gen, promise: run };
    await run;
  }

  pi.on("session_start", async (_event, ctx) => {
    settings = loadUnityHarnessSettings();
    // 新会话 cwd：不沿用上一会话的显式选择 / 绑定，按新 cwd 重新解析。
    if (ctx.cwd && !sameProjectPath(ctx.cwd, sessionCwd)) {
      explicitProjectPath = undefined;
      activeProjectPath = undefined;
    }
    sessionCwd = ctx.cwd;
    await stopSessionMux();
    if (settings.enabled) {
      startSessionMux(ctx.cwd, explicitProjectPath);
      void refreshDynamicPipelineTools();
      void autoConnectUnity(ctx);
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

  // ---- Slash Commands ----

  const harnessCommandHandler = async (args: string, ctx: { ui: any; cwd: string }) => {
    const raw = (args || "").trim();
    const tokens = raw.split(/\s+/).filter(Boolean);
    const action = tokens[0]?.toLowerCase();
    const persistScope = tokens.some((t) => t === "--project") ? "project" : "global";

    if (!action || action === "status") {
      const info = inspectUnityHarnessSettings(ctx.cwd);
      const lines = [
        `runtime enabled: ${settings.enabled ? "on" : "off"}`,
        `active project: ${activeProjectPath ?? "(auto-detect)"}`,
        `dynamically registered pipeline tools: ${dynamicallyRegisteredTools.size}`,
        `global settings: ${info.globalPath} (exists=${info.globalExists})`,
        `project settings: ${info.projectPath ?? "(none)"} (exists=${info.projectExists})`,
        `env PI_UNITY_HARNESS_ENABLED: ${info.env ?? "(unset)"}`,
        "",
        "Usage:",
        "  /unity-harness status",
        "  /unity-harness on | off              (this session)",
        "  /unity-harness on --persist          (session + ~/.pi/agent/settings.json)",
        "  /unity-harness off --persist",
        "  /unity-harness on --project          (session + <cwd>/.pi/settings.json)",
        "  /unity-harness off --project",
      ];
      ctx.ui?.notify?.(lines.join("\n"), "info");
      return;
    }

    const enable = coerceEnabled(action);
    if (enable === undefined) {
      ctx.ui?.notify?.(`Unknown action: "${action}". Use status | on | off [--persist|--project]`, "warn");
      return;
    }

    if (tokens.some((t) => t.startsWith("--"))) {
      persistEnabled(enable, persistScope, ctx.cwd);
    }
    settings = loadUnityHarnessSettings(ctx.cwd);
    settings.enabled = enable;

    if (!enable) {
      await stopSessionMux();
      ctx.ui?.setStatus?.("pi-unity", "disabled (/unity-harness on)");
      ctx.ui?.notify?.("pi-unity-harness disabled.", "info");
    } else {
      await stopSessionMux();
      startSessionMux(ctx.cwd, explicitProjectPath ?? activeProjectPath);
      void refreshDynamicPipelineTools();
      ctx.ui?.notify?.("pi-unity-harness enabled.", "info");
      await autoConnectUnity(ctx);
    }
  };

  pi.registerCommand("unity-harness", {
    description: "Inspect or toggle pi-unity-harness settings (status | on | off [--persist|--project])",
    handler: harnessCommandHandler,
  });

  pi.registerCommand("unity-harness-settings", {
    description: "Inspect or toggle pi-unity-harness settings (alias for /unity-harness)",
    handler: harnessCommandHandler,
  });

  const discoverCommandHandler = async (args: string, ctx: { ui: any; cwd: string }) => {
    if (!settings.enabled) {
      ctx.ui?.notify?.("pi-unity-harness is disabled. Use /unity-harness on first.", "warn");
      return;
    }

    const explicit = args?.trim();
    if (explicit) {
      await switchProject(explicit, ctx, { explicit: true });
      ctx.ui?.notify?.(`Connected: ${basename(explicit)}`, "info");
      ctx.ui?.setStatus?.("pi-unity", `unity bridge: ${basename(explicit)}`);
      return;
    }

    const instances = discover();
    if (instances.length === 0) {
      ctx.ui?.notify?.("No running Unity Editor detected", "warn");
      return;
    }

    const readyInstances = instances.filter((i) => i.bridgeReady);
    const notReady = instances.filter((i) => !i.bridgeReady);

    const choices: string[] = [];
    const choicePaths = new Map<string, string>();
    const addChoice = (inst: UnityInstance, state: string) => {
      const label = `${state} ${basename(inst.projectPath)} (PID ${inst.pid}) - ${inst.projectPath}`;
      choices.push(label);
      choicePaths.set(label, inst.projectPath);
    };
    for (const inst of readyInstances) addChoice(inst, "[ready]");
    for (const inst of notReady) addChoice(inst, "[no bridge]");

    if (choices.length === 1) {
      const target = choicePaths.get(choices[0])!;
      await switchProject(target, ctx, { explicit: true });
      if (readyInstances.length === 1) {
        ctx.ui?.notify?.(`Connected: ${basename(target)}`, "info");
        ctx.ui?.setStatus?.("pi-unity", `unity bridge: ${basename(target)}`);
      } else {
        ctx.ui?.notify?.(`Project set: ${basename(target)} (bridge not ready; install with /unity-install)`, "warn");
        ctx.ui?.setStatus?.("pi-unity", `unity: ${basename(target)} (no bridge)`);
      }
      return;
    }

    const chosen = await ctx.ui?.select?.(
      "Choose a Unity instance to connect:",
      choices,
    );

    const chosenPath = chosen ? choicePaths.get(chosen) : undefined;
    if (chosenPath) {
      const inst = instances.find((i) => i.projectPath === chosenPath);
      await switchProject(chosenPath, ctx, { explicit: true });
      if (inst?.bridgeReady) {
        ctx.ui?.notify?.(`Connected: ${basename(chosenPath)}`, "info");
        ctx.ui?.setStatus?.("pi-unity", `unity bridge: ${basename(chosenPath)}`);
      } else {
        ctx.ui?.notify?.(`Project set: ${basename(chosenPath)} (bridge not ready; install with /unity-install)`, "warn");
        ctx.ui?.setStatus?.("pi-unity", `unity: ${basename(chosenPath)} (no bridge)`);
      }
    }
  };

  pi.registerCommand("unity-discover", {
    description: "Scan running Unity Editor instances and choose one to connect",
    handler: discoverCommandHandler,
  });

  pi.registerCommand("unity-connect", {
    description: "Connect to a running Unity Editor instance (alias for /unity-discover)",
    handler: discoverCommandHandler,
  });

  pi.registerCommand("unity-install", {
    description: "Install the com.pi.unity-harness package and optional pipeline into a Unity project",
    handler: async (args, ctx) => {
      if (!settings.enabled) {
        ctx.ui?.notify?.("pi-unity-harness is disabled. Use /unity-harness on first.", "warn");
        return;
      }

      let projectPath = args?.trim();

      if (!projectPath) {
        const instances = discover();
        const notReady = instances.filter((i) => !i.bridgeReady);

        if (notReady.length === 0) {
          ctx.ui?.notify?.(
            instances.length > 0
              ? "All running Unity instances already have the bridge installed"
              : "No running Unity detected. Specify a project path: /unity-install <path>",
            "warn",
          );
          return;
        }

        if (notReady.length === 1) {
          projectPath = notReady[0].projectPath;
        } else {
          const installChoices = notReady.map(
            (i) => `${basename(i.projectPath)} (PID ${i.pid}) - ${i.projectPath}`,
          );
          const chosen = await ctx.ui?.select?.(
            "Choose a project to install:",
            installChoices,
          );
          if (!chosen) return;
          projectPath = notReady[installChoices.indexOf(chosen)]?.projectPath;
          if (!projectPath) return;
        }
      }

      const resolvedProject = paths.host(projectPath, ctx.cwd);
      const harnessResult = installPiUnityHarness(resolvedProject);
      if (harnessResult.ok) {
        ctx.ui?.notify?.(harnessResult.message, "info");
      } else {
        ctx.ui?.notify?.(harnessResult.message, "error");
        return;
      }

      const unityVersion = readProjectUnityVersion(resolvedProject);
      const major = parseUnityMajorVersion(unityVersion);
      const pipelineStatus = getPipelineInstallStatus(resolvedProject);
      if (!pipelineStatus.installed) {
        const isUnity6 = major !== undefined && major >= 6000;
        const title = isUnity6 ? "Install com.unity.pipeline?" : "Install the pipeline compat fork?";
        const detail = isUnity6
          ? `Project Unity version is ${unityVersion ?? "unknown"} (Unity 6+). Add the official ${PIPELINE_PACKAGE_NAME}@${PIPELINE_PACKAGE_VERSION}?`
          : `Project Unity version is ${unityVersion ?? "unknown"} (pre-Unity 6). Add ${PIPELINE_PACKAGE_NAME} (compat fork for 2021.3/2022) as the command surface?`;
        const ok = await ctx.ui?.confirm?.(title, detail);
        if (ok) {
          const pipelineResult = installUnityPipelineForProject(resolvedProject, major);
          ctx.ui?.notify?.(pipelineResult.message, pipelineResult.ok ? "info" : "error");
        }
      } else {
        ctx.ui?.notify?.(
          `pipeline already present: ${pipelineStatus.packageName}@${pipelineStatus.version} (${pipelineStatus.flavor})`,
          "info",
        );
      }
    },
  });

  // ---- unity_discover ----
  pi.registerTool({
    name: "unity_discover",
    label: "Unity Discover",
    description: "Scan running Unity Editor instances, list connectable projects, and optionally connect.",
    promptSnippet: "Use unity_discover to scan for running Unity Editors and connect to one.",
    promptGuidelines: [
      "Use unity_discover when you need to find available Unity instances or switch targets.",
      "Call this before other unity_* tools if you are unsure which Unity project is connected.",
    ],
    parameters: toolSchema({
      projectPath: Type.String({ description: "Optional project path to connect to. If omitted, scans and selects the first ready instance." }),
      ...VIEW_FIELDS,
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      let scanError: string | undefined;
      let instances: UnityInstance[];
      try {
        instances = discover();
      } catch (error) {
        if (!params.projectPath) throw error;
        instances = [];
        scanError = error instanceof Error ? error.message : "unity_discovery_failed";
      }
      const readyInstances = instances.filter((i) => i.bridgeReady);

      if (params.projectPath) {
        await switchProject(params.projectPath, undefined, { explicit: true });
      } else if (readyInstances.length > 0 && !activeProjectPath) {
        await switchProject(readyInstances[0].projectPath);
      }

      const current = activeProjectPath ?? null;
      const result = {
        current,
        ...(scanError ? { scanError } : {}),
        instances: instances.map((i) => ({
          projectPath: i.projectPath,
          pid: i.pid,
          bridgeReady: i.bridgeReady,
          selected: current ? sameProjectPath(i.projectPath, current) : false,
        })),
        hint: readyInstances.length === 0 && instances.length > 0
          ? `Detected ${instances.length} running Unity instance(s), but none have the bridge installed. Run /unity-install to install.`
          : undefined,
      };

      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
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
    description:
      "Execute C# code or expression in the Unity Editor main thread via pi-unity eval." +
      " 契约：最后一行写裸表达式（不要 return，if (...) return 会编译失败）；return {...} 块由 C# worker 自动支持，块内局部变量作用域不跨调用持久；Object 有 System/UnityEngine 歧义，用 UnityEngine.Object 全限定或 UnityObject 别名。",
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
    description:
      "Execute a C# script file or .repl file in the Unity Editor main thread via pi-unity eval -f." +
      " 契约同 unity_eval：文件最后一行写裸表达式，不要 return；return {...} 块由 worker 自动支持且块内局部变量不跨调用持久；Object 用 UnityEngine.Object 或 UnityObject 别名。",
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
      await refreshDynamicPipelineTools(signal, "business");
      return runTool(args, { timeoutMs: toolTimeout(params, 15000), signal });
    },
  });

  // ---- unity_pipeline ----
  pi.registerTool({
    name: "unity_pipeline",
    label: "Unity Pipeline",
    description:
      "Execute a registered Unity Pipeline [CliCommand] via pi-unity pipeline. Omit command/name to list available commands." +
      " UI 交互先 uitree_*，拖拽优先 input_drag；assets_refresh 异步，调用后先 unity_recompile 再继续 managed 调用。",
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
    description:
      "Capture a single GameView or SceneView screenshot via pi-unity capture." +
      " 注意：结果里的 embed:false 等元数据只是建议，宿主不强制，截图仍可能被内联进上下文；省 token 优先 unity_observe（dHash + 路径）或只读返回路径。",
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
