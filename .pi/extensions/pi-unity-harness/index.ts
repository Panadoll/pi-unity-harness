import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import {
  PIPELINE_SHORTCUT_COMMANDS,
  PIPELINE_TOOL_EXCLUDE,
  filterPipelineCommands,
  normalizePipelineToolName,
  normalizePipelineCommandParams,
  normalizePipelineRequestedCommand,
  parseEditorStatus,
  parseUnityMajorVersion,
  pipelineCommandTimeoutMs,
  pipelineCommandSummary,
  resolveEvalFilePath,
  schemaToTypeBox,
  shouldRefreshPipelineCommands,
  type PipelineParameterInfo,
} from "./helpers.ts";
import {
  describeSettingsPaths,
  extensionDirPath,
  inspectUnityHarnessSettings,
  loadUnityHarnessSettings,
  persistEnabled,
  SETTINGS_KEY,
  type UnityHarnessSettings,
} from "./config.ts";
import net from "node:net";
import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";

const CORE_UNITY_TOOLS = [
  "unity_discover",
  "unity_ping",
  "unity_status",
  "unity_snapshot",
  "unity_timeline",
  "unity_pipeline",
  "unity_eval",
  "unity_eval_file",
  "unity_recompile",
] as const;

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

function isUnityManagedTool(name: string): boolean {
  if ((CORE_UNITY_TOOLS as readonly string[]).includes(name)) return true;
  // Dynamically registered pipeline shortcuts all normalize to unity_*
  return name.startsWith("unity_");
}

interface BridgeInfo {
  project: string;
  pid: number;
  pipe: string;
  token: string;
  generation: number;
  statePlaneName?: string;
}

interface UnityInstance {
  projectPath: string;
  pid: number;
  bridgeReady: boolean;
  bridgeInfo?: BridgeInfo;
  pipeOccupied?: boolean;
}

interface PendingRequest {
  resolve: (value: any) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

interface PipelineCommandInfo {
  name: string;
  description?: string;
  mainThreadRequired?: boolean;
  runtimeOnly?: boolean;
  schema?: Record<string, unknown> | null;
  parameters?: PipelineParameterInfo[];
}

interface PipelineCommandList {
  typeName?: string;
  pipelineAvailable: boolean;
  commands: PipelineCommandInfo[];
  count?: number;
}

interface PipelineInstallStatus {
  installed: boolean;
  /** Official com.unity.pipeline or the compat fork */
  packageName?: string;
  /** "official" | "compat" */
  flavor?: "official" | "compat";
  version?: string;
  source?: string;
}

const STATE_PLANE_MAGIC = 0x48554950;
const STATE_PLANE_VERSION = 1;
const STATE_PLANE_HEADER_SIZE = 64;
const STATE_PLANE_SLOT_COUNT = 2;
const STATE_PLANE_SLOT_SIZE = 64 * 1024;
const STATE_PLANE_SLOT_PAYLOAD_OFFSET = 24;
const INLINE_EVAL_CODE_LIMIT_BYTES = 512 * 1024;
const CLIENT_HEARTBEAT_INTERVAL_MS = 5000;
const CLIENT_HEARTBEAT_TIMEOUT_MS = 5000;
/** Unity 6+ official package */
const PIPELINE_PACKAGE_NAME = "com.unity.pipeline";
const PIPELINE_PACKAGE_VERSION = "0.4.0-exp.1";
/** 2021.3 / 2022.x compat fork (assembly names remain Unity.Pipeline) */
const PIPELINE_COMPAT_PACKAGE_NAME = "com.pi.pipeline.compat";
const PIPELINE_COMPAT_INPUTSYSTEM_VERSION = "1.7.0";

// ---- State Plane standalone reading (independent of the client instance) ----

function readStatePlaneSnapshotByBridge(bridge: BridgeInfo): Record<string, unknown> | null {
  if (!bridge.statePlaneName) return null;

  const script = `
$ErrorActionPreference = 'Stop'
$MappingName = $env:PI_UNITY_STATE_PLANE_NAME
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class PiUnityStatePlaneNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenFileMappingW(uint desiredAccess, bool inheritHandle, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr handle, uint desiredAccess, uint fileOffsetHigh, uint fileOffsetLow, UIntPtr bytesToMap);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(IntPtr baseAddress);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
"@
$headerSize = ${STATE_PLANE_HEADER_SIZE}
$maxSlotCount = ${STATE_PLANE_SLOT_COUNT}
$maxSlotSize = ${STATE_PLANE_SLOT_SIZE}
$totalSize = $headerSize + ($maxSlotCount * $maxSlotSize)
$handle = [PiUnityStatePlaneNative]::OpenFileMappingW(4, $false, $MappingName)
if ($handle -eq [IntPtr]::Zero) { exit 2 }
$view = [PiUnityStatePlaneNative]::MapViewOfFile($handle, 4, 0, 0, [UIntPtr]$totalSize)
if ($view -eq [IntPtr]::Zero) { [PiUnityStatePlaneNative]::CloseHandle($handle) | Out-Null; exit 3 }
try {
  $buffer = New-Object byte[] $totalSize
  [Runtime.InteropServices.Marshal]::Copy($view, $buffer, 0, $totalSize)
  $magic = [BitConverter]::ToUInt32($buffer, 0)
  $version = [BitConverter]::ToUInt16($buffer, 4)
  $slotCount = [BitConverter]::ToUInt16($buffer, 6)
  $slotSize = [BitConverter]::ToUInt32($buffer, 8)
  $writerSeq = [BitConverter]::ToUInt64($buffer, 16)
  if ($magic -ne ${STATE_PLANE_MAGIC} -or $version -ne ${STATE_PLANE_VERSION} -or $slotCount -lt 1 -or $slotCount -gt $maxSlotCount -or $slotSize -lt 64 -or $slotSize -gt $maxSlotSize -or $writerSeq -lt 1) { exit 4 }
  $slotIndex = [int](($writerSeq - 1) % $slotCount)
  $slotOffset = $headerSize + ($slotIndex * $slotSize)
  $slotSeqBefore = [BitConverter]::ToUInt64($buffer, $slotOffset)
  if ($slotSeqBefore -ne $writerSeq) { exit 5 }
  $payloadLen = [BitConverter]::ToUInt32($buffer, $slotOffset + 16)
  if ($payloadLen -lt 1 -or $payloadLen -gt ($slotSize - ${STATE_PLANE_SLOT_PAYLOAD_OFFSET})) { exit 6 }
  $payloadBytes = New-Object byte[] $payloadLen
  [Array]::Copy($buffer, $slotOffset + ${STATE_PLANE_SLOT_PAYLOAD_OFFSET}, $payloadBytes, 0, [int]$payloadLen)
  $slotSeqAfter = [BitConverter]::ToUInt64($buffer, $slotOffset)
  $writerSeqAfter = [BitConverter]::ToUInt64($buffer, 16)
  if ($slotSeqAfter -ne $slotSeqBefore -or $writerSeqAfter -ne $writerSeq) { exit 7 }
  [Console]::Out.Write([System.Text.Encoding]::UTF8.GetString($payloadBytes))
}
finally {
  [PiUnityStatePlaneNative]::UnmapViewOfFile($view) | Out-Null
  [PiUnityStatePlaneNative]::CloseHandle($handle) | Out-Null
}
`;

  try {
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    const output = execFileSync(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
      {
        encoding: "utf8",
        stdio: ["ignore", "pipe", "ignore"],
        env: { ...process.env, PI_UNITY_STATE_PLANE_NAME: bridge.statePlaneName },
      },
    ).trim();
    return output ? JSON.parse(output) : null;
  } catch {
    return null;
  }
}

// ---- Scan running Unity instances ----

function discoverUnityInstances(): UnityInstance[] {
  if (process.platform !== "win32") return [];

  try {
    const output = execFileSync(
      "powershell.exe",
      [
        "-NoProfile", "-NonInteractive", "-Command",
        `Get-CimInstance Win32_Process -Filter "name='Unity.exe'" | ForEach-Object { $procId = $_.ProcessId; $cmd = if ($_.CommandLine) { $_.CommandLine } else { '' }; Write-Output "PID:$procId"; Write-Output "CMD:$cmd"; Write-Output "---" }`,
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

      // Skip batch import workers and other helper processes; keep main Editor instances only
      if (/AssetImportWorker|-adb2|(^|\s)-batchMode(\s|$)/i.test(cmdLine)) continue;

      // Hub creates new projects with -createproject, normal opens use -projectPath;
      // both argument forms may be quoted ("-projectPath" "path")
      const ppMatch = cmdLine.match(/-projectPath[\s"]+"?([^"\s]+)/i) ??
        cmdLine.match(/-createproject[\s"]+"?([^"\s]+)/i);
      const projectPath = ppMatch ? resolve(ppMatch[1]) : null;
      if (!projectPath) continue;

      if (instances.some((i) => i.projectPath === projectPath)) continue;

      const bridgePath = join(projectPath, "Library", "PiUnityHarness", "bridge.json");
      let bridgeReady = false;
      let bridgeInfo: BridgeInfo | undefined;

      if (existsSync(bridgePath)) {
        try {
          let text = readFileSync(bridgePath, "utf8");
          if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
          const raw = JSON.parse(text);
          if (raw.pipe && raw.token) {
            bridgeReady = true;
            bridgeInfo = raw as BridgeInfo;
          }
        } catch { /* Corrupted data counts as not ready */ }
      }

      // Check whether the pipe is already occupied by another session
      let pipeOccupied = false;
      if (bridgeInfo?.statePlaneName) {
        try {
          const snapshot = readStatePlaneSnapshotByBridge(bridgeInfo);
          if (snapshot?.connected === true) {
            pipeOccupied = true;
          }
        } catch { /* Read failure must not affect discovery */ }
      }

      instances.push({ projectPath, pid, bridgeReady, bridgeInfo, pipeOccupied });
    }

    return instances;
  } catch {
    return [];
  }
}

// ---- Install pi-unity-harness ----

function scratchReplDir(projectPath: string): string {
  return join(projectPath, "Temp", "PiUnityHarness", "AgentScratch");
}

function writeScratchRepl(projectPath: string, code: string): string {
  const dir = scratchReplDir(projectPath);
  mkdirSync(dir, { recursive: true });
  const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const filePath = join(dir, `pi-eval-${id}.repl`);
  const source = code.trimStart().startsWith("// #repl-mode:") ? code : `// #repl-mode: auto\n${code}`;
  writeFileSync(filePath, source, "utf8");
  return relative(projectPath, filePath).replace(/\\/g, "/");
}

function removeScratchRepl(projectPath: string, filePath: string) {
  const dir = resolve(scratchReplDir(projectPath));
  const fullPath = resolve(projectPath, filePath);
  const rel = relative(dir, fullPath);
  if (!rel || rel.startsWith("..") || rel.includes(":") || !basename(fullPath).startsWith("pi-eval-") || !fullPath.endsWith(".repl")) {
    return;
  }

  try { unlinkSync(fullPath); } catch { /* Ignore if the request timed out or was cleaned up externally */ }
}

function cleanupScratchRepls(projectPath: string) {
  const dir = scratchReplDir(projectPath);
  if (!existsSync(dir)) return;

  try {
    for (const name of readdirSync(dir)) {
      if (!name.startsWith("pi-eval-") || !name.endsWith(".repl")) continue;
      try { unlinkSync(join(dir, name)); } catch { /* The file may be cleaned up by an external process */ }
    }
  } catch { /* An unreadable directory must not affect bridge use */ }
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

function readProjectUnityVersion(projectPath: string): string | undefined {
  const versionPath = join(projectPath, "ProjectSettings", "ProjectVersion.txt");
  if (!existsSync(versionPath)) return undefined;
  try {
    const text = readFileSync(versionPath, "utf8");
    return text.match(/m_EditorVersion:\s*([^\r\n]+)/)?.[1]?.trim();
  } catch {
    return undefined;
  }
}

function readEmbeddedPackageVersion(projectPath: string, packageName: string): string | undefined {
  const embeddedPackageJson = join(projectPath, "Packages", packageName, "package.json");
  if (!existsSync(embeddedPackageJson)) return undefined;
  try {
    let text = readFileSync(embeddedPackageJson, "utf8");
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    const pkg = JSON.parse(text);
    return typeof pkg.version === "string" ? pkg.version : undefined;
  } catch {
    return undefined;
  }
}

/** Reads the displayName of the embedded package to tell the official package from the compat fork */
function readEmbeddedPackageDisplayName(projectPath: string, packageName: string): string | undefined {
  const embeddedPackageJson = join(projectPath, "Packages", packageName, "package.json");
  if (!existsSync(embeddedPackageJson)) return undefined;
  try {
    let text = readFileSync(embeddedPackageJson, "utf8");
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    const pkg = JSON.parse(text);
    return typeof pkg.displayName === "string" ? pkg.displayName : undefined;
  } catch {
    return undefined;
  }
}

function getPipelineInstallStatus(projectPath: string): PipelineInstallStatus {
  const manifest = readManifest(projectPath);
  const deps = manifest?.dependencies ?? {};

  // The compat fork and the official package share the UPM name com.unity.pipeline
  // (official registry or embedded copy)
  const pipelineManifest = deps[PIPELINE_PACKAGE_NAME];
  const embeddedVersion = readEmbeddedPackageVersion(projectPath, PIPELINE_PACKAGE_NAME);
  if (embeddedVersion !== undefined || pipelineManifest) {
    const manifestValue = String(pipelineManifest ?? "");
    const embeddedDisplay = readEmbeddedPackageDisplayName(projectPath, PIPELINE_PACKAGE_NAME);
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

function resolvePipelineCompatPackageDir(): string | undefined {
  const candidates = [
    resolve(extensionDirPath(), "../../../unity/com.pi.pipeline.compat"),
    resolve(process.cwd(), "unity", "com.pi.pipeline.compat"),
    resolve(process.cwd(), "..", "pi-unity-harness", "unity", "com.pi.pipeline.compat"),
  ];
  for (const dir of candidates) {
    if (existsSync(join(dir, "package.json"))) return dir;
  }
  return undefined;
}

/** Unity 6+: install the official com.unity.pipeline */
function installOfficialUnityPipeline(projectPath: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  const manifest = readManifest(projectPath);
  if (!manifest) {
    return { ok: false, message: `Cannot find or parse Packages/manifest.json: ${projectPath}` };
  }

  manifest.dependencies ??= {};
  const existing = getPipelineInstallStatus(projectPath);
  if (existing.installed) {
    return {
      ok: true,
      message: `pipeline already installed: ${existing.packageName}@${existing.version} (${existing.flavor})`,
    };
  }

  manifest.dependencies[PIPELINE_PACKAGE_NAME] = PIPELINE_PACKAGE_VERSION;
  try {
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return { ok: true, message: `Added ${PIPELINE_PACKAGE_NAME}@${PIPELINE_PACKAGE_VERSION}. Unity Editor will resolve the package automatically.` };
  } catch (error: any) {
    return { ok: false, message: `Failed to write manifest.json: ${error.message}` };
  }
}

/** Pre-Unity 6: install com.pi.pipeline.compat (file: local fork) */
function installCompatUnityPipeline(projectPath: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  const manifest = readManifest(projectPath);
  if (!manifest) {
    return { ok: false, message: `Cannot find or parse Packages/manifest.json: ${projectPath}` };
  }

  const existing = getPipelineInstallStatus(projectPath);
  if (existing.installed) {
    return {
      ok: true,
      message: `pipeline already installed: ${existing.packageName}@${existing.version} (${existing.flavor})`,
    };
  }

  const pkgDir = resolvePipelineCompatPackageDir();
  if (!pkgDir) {
    return {
      ok: false,
      message: `Cannot find the compat package directory (expected unity/com.pi.pipeline.compat)`,
    };
  }

  manifest.dependencies ??= {};
  // Unity 2022 versionDefines only honor embedded/registry packages, not file: local packages
  // pointing outside the project; so the compat fork is copied into the project as an embedded
  // package at Packages/com.unity.pipeline. Re-run /unity-install after a repo update to resync.
  delete manifest.dependencies[PIPELINE_COMPAT_PACKAGE_NAME];
  try {
    const targetDir = join(projectPath, "Packages", PIPELINE_PACKAGE_NAME);
    rmSync(targetDir, { recursive: true, force: true });
    cpSync(pkgDir, targetDir, { recursive: true, force: true });
    manifest.dependencies[PIPELINE_PACKAGE_NAME] = "file:com.unity.pipeline";
    if (!manifest.dependencies["com.unity.inputsystem"]) {
      manifest.dependencies["com.unity.inputsystem"] = PIPELINE_COMPAT_INPUTSYSTEM_VERSION;
    }
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return {
      ok: true,
      message:
        `Copied the compat fork as embedded package ${PIPELINE_PACKAGE_NAME} (Packages/com.unity.pipeline)` +
        ` and ensured com.unity.inputsystem@${PIPELINE_COMPAT_INPUTSYSTEM_VERSION}.` +
        ` Unity Editor will resolve the package; compat has no Roslyn eval/HotReload, use unity_eval.`,
    };
  } catch (error: any) {
    return { ok: false, message: `Failed to write manifest / copy package: ${error.message}` };
  }
}

/** Selects official or compat by the project's Unity major version */
function installUnityPipelineForProject(
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

/** @deprecated Keep the old name, defaulting to the official install; use installUnityPipelineForProject in new code */
function installUnityPipeline(projectPath: string): { ok: boolean; message: string } {
  return installOfficialUnityPipeline(projectPath);
}

function installPiUnityHarness(projectPath: string, packageSourceDir?: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    return { ok: false, message: `Cannot find Packages/manifest.json: ${projectPath} may not be a Unity project` };
  }

  // Check whether it is already installed first, so an installed project is not misreported
  // because package source resolution failed
  try {
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    if (manifest.dependencies?.["com.pi.unity-harness"]) {
      return { ok: true, message: `com.pi.unity-harness installed (${manifest.dependencies["com.pi.unity-harness"]})` };
    }
  } catch (error: any) {
    return { ok: false, message: `Failed to read manifest.json: ${error.message}` };
  }

  let pkgDir: string;
  if (packageSourceDir) {
    pkgDir = resolve(packageSourceDir);
  } else {
    // Prefer inferring the repo root from the extension's own location (independent of cwd)
    pkgDir = resolve(extensionDirPath(), "../../../unity/com.pi.unity-harness");
    if (!existsSync(join(pkgDir, "package.json"))) {
      // Fallback: infer the repo root from cwd (when the extension was copied to a global directory)
      pkgDir = resolve(process.cwd(), "unity", "com.pi.unity-harness");
      if (!existsSync(join(pkgDir, "package.json"))) {
        // Try to infer from the parent repo directory
        const alt = resolve(process.cwd(), "..", "pi-unity-harness", "unity", "com.pi.unity-harness");
        if (existsSync(join(alt, "package.json"))) {
          pkgDir = alt;
        }
      }
    }
  }

  if (!existsSync(join(pkgDir, "package.json"))) {
    return { ok: false, message: `Cannot find the pi-unity-harness package directory: ${pkgDir}` };
  }

  let relPath = relative(dirname(manifestPath), pkgDir).replace(/\\/g, "/");
  if (!relPath.startsWith(".")) relPath = "./" + relPath;

  try {
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    if (!manifest.dependencies) manifest.dependencies = {};

    const pkgName = "com.pi.unity-harness";
    if (manifest.dependencies[pkgName]) {
      return { ok: true, message: `${pkgName} installed (${manifest.dependencies[pkgName]})` };
    }

    manifest.dependencies[pkgName] = `file:${relPath}`;
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return {
      ok: true,
      message: `Added ${pkgName} -> file:${relPath}. Unity Editor will resolve the package; the bridge becomes ready once resolved.`,
    };
  } catch (error: any) {
    return { ok: false, message: `Failed to write manifest.json: ${error.message}` };
  }
}

// ---- Client ----

class UnityBridgeClient {
  private cwd = process.cwd();
  private socket?: net.Socket;
  private connectPromise?: Promise<void>;
  private bridge?: BridgeInfo;
  private lineBuffer = "";
  private nextId = 1;
  private pending = new Map<string, PendingRequest>();
  private projectRootOverride?: string;
  private heartbeatTimer?: NodeJS.Timeout;
  private heartbeatInFlight = false;
  private pipelineCommandCache?: PipelineCommandList;
  private pipelineCacheProject?: string;
  private pipelineCacheGeneration?: number;

  configure(cwd: string) { this.cwd = cwd; }

  setProjectRoot(path: string) {
    this.projectRootOverride = resolve(path);
    this.pipelineCommandCache = undefined;
    this.pipelineCacheProject = undefined;
    this.pipelineCacheGeneration = undefined;
    cleanupScratchRepls(this.projectRootOverride);
  }

  getProjectRoot(): string | undefined {
    try { return this.resolveUnityProjectRoot(); } catch { return undefined; }
  }

  close() {
    this.stopHeartbeat();
    if (this.socket) { this.socket.destroy(); this.socket = undefined; }
    if (this.connectPromise) this.connectPromise = undefined;
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error(`bridge closed before request ${id} completed`));
    }
    this.pending.clear();
    this.lineBuffer = "";
  }

  async probeModalStatus(bridge: BridgeInfo): Promise<{ present: boolean; title?: string; buttons?: string[] } | null> {
    try {
      const status = await this.oneShotStatus(bridge);
      const obs = status?.modalObservation;
      if (!obs?.present || !obs?.windows?.length) return null;
      const w = obs.windows[0];
      return {
        present: true,
        title: w.title ?? undefined,
        buttons: w.buttons ?? undefined,
      };
    } catch {
      return null;
    }
  }

  private oneShotStatus(bridge: BridgeInfo): Promise<any> {
    return new Promise((resolve, reject) => {
      const socket = net.createConnection(bridge.pipe);
      const message = JSON.stringify({ id: "modal-probe", type: "status", token: bridge.token }) + "\n";
      let buf = "";
      const timer = setTimeout(() => {
        socket.destroy();
        reject(new Error("modal probe timed out"));
      }, 3000);
      socket.on("error", (err) => {
        clearTimeout(timer);
        socket.destroy();
        reject(err);
      });
      socket.on("data", (chunk: Buffer) => {
        buf += chunk.toString("utf8");
        const nl = buf.indexOf("\n");
        if (nl < 0) return;
        clearTimeout(timer);
        socket.destroy();
        try {
          const msg = JSON.parse(buf.slice(0, nl));
          resolve(msg.result ?? msg);
        } catch {
          reject(new Error("modal probe invalid json"));
        }
      });
      socket.on("connect", () => {
        socket.write(message);
      });
    });
  }

  async request(type: string, payload: Record<string, unknown>, timeoutMs = 20000) {
    const bridge = this.loadBridgeInfo();
    await this.ensureConnected(bridge, timeoutMs);

    const id = String(this.nextId++);
    const message = JSON.stringify({ id, type, token: bridge.token, timeoutMs, payload }) + "\n";

    return await new Promise<any>((resolve, reject) => {
      const timer = setTimeout(async () => {
        this.pending.delete(id);
        if (this.socket) {
          this.stopHeartbeat();
          this.socket.destroy();
          this.socket = undefined;
        }
        // Phase 2: probe modal before rejecting
        const modal = await this.probeModalStatus(bridge);
        if (modal?.present) {
          const buttons = modal.buttons?.length ? ` [${modal.buttons.join(" | ")}]` : "";
          reject(new Error(
            `unity request timed out after ${timeoutMs}ms (${type}) — EDITOR_MODAL: "${modal.title ?? "?"}"${buttons}. ` +
            `Close the dialog in Unity Editor manually, or retry with yolo mode.`
          ));
        } else {
          reject(new Error(`unity request timed out after ${timeoutMs}ms (${type})`));
        }
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      this.socket!.write(message, (error) => {
        if (!error) return;
        const entry = this.pending.get(id);
        if (!entry) return;
        clearTimeout(entry.timer);
        this.pending.delete(id);
        if (this.socket) {
          this.stopHeartbeat();
          this.socket.destroy();
          this.socket = undefined;
        }
        reject(error);
      });
    });
  }

  async listPipelineCommands(force = false, timeoutMs = 8000): Promise<PipelineCommandList> {
    const bridge = this.loadBridgeInfo();
    const project = this.resolveUnityProjectRoot();
    if (!force && this.pipelineCommandCache && this.pipelineCacheProject === project && this.pipelineCacheGeneration === bridge.generation) {
      return this.pipelineCommandCache;
    }

    const result = await this.request("list_commands", {}, timeoutMs) as Partial<PipelineCommandList>;
    const normalized: PipelineCommandList = {
      typeName: result?.typeName ?? "command_list",
      pipelineAvailable: result?.pipelineAvailable === true,
      commands: Array.isArray(result?.commands) ? result.commands : [],
      count: typeof result?.count === "number" ? result.count : Array.isArray(result?.commands) ? result.commands.length : 0,
    };
    this.pipelineCommandCache = normalized;
    this.pipelineCacheProject = project;
    this.pipelineCacheGeneration = bridge.generation;
    return normalized;
  }

  pipelineStatusSnapshot() {
    const project = this.getProjectRoot();
    return {
      pipelineInstalled: project ? getPipelineInstallStatus(project) : undefined,
      pipelineAvailable: this.pipelineCommandCache?.pipelineAvailable,
      pipelineCommandCount: this.pipelineCommandCache?.commands?.length,
    };
  }

  async status(pipeTimeoutMs = 2000) {
    const bridge = this.loadBridgeInfo();
    const pipelineStatus = this.pipelineStatusSnapshot();

    let pipeConnected = false;
    let pipeStatus = "disconnected";
    let pipeLatencyMs: number | undefined;
    let pipeError: string | undefined;
    let pipeCapabilities: string[] = [];
    let pipeResult: any = null;
    let pipeFocusState: string | undefined;
    let pipeWindowState: string | undefined;

    const pipeStarted = Date.now();
    try {
      pipeResult = await this.request("status", {}, pipeTimeoutMs);
      pipeLatencyMs = Date.now() - pipeStarted;
      pipeConnected = true;
      const parsedStatus = parseEditorStatus(pipeResult.editorStatus);
      pipeStatus = parsedStatus.editorStatus;
      pipeFocusState = pipeResult.focusState ?? parsedStatus.focusState;
      pipeWindowState = pipeResult.windowState ?? parsedStatus.windowState;
      pipeCapabilities = Array.isArray(pipeResult.capabilities) ? pipeResult.capabilities : [];
    } catch (error: any) {
      pipeLatencyMs = Date.now() - pipeStarted;
      pipeError = error?.message ?? "unknown_pipe_error";
    }

    if (pipeConnected && pipeResult) {
      return {
        ...pipeResult,
        source: "pipe",
        state: String(pipeResult.managedState ?? pipeStatus ?? "unknown"),
        pipeReachable: true,
        pipeLatencyMs,
        editorStatus: pipeStatus,
        focusState: pipeFocusState,
        windowState: pipeWindowState,
        capabilities: pipeCapabilities,
        ...pipelineStatus,
      };
    }

    // PowerShell shared-memory reads are only a degraded path after pipe failure, so not every status starts a subprocess.
    const snapshot = this.readStatePlaneSnapshot(bridge);
    if (!snapshot) {
      return {
        state: "disconnected",
        source: "inference",
        pipeReachable: false,
        pipeLatencyMs,
        pipeError,
        editorStatus: "disconnected",
      };
    }

    const lifecycleState = String(snapshot.managedState ?? "unknown");

    if (lifecycleState === "reloading" || lifecycleState === "initializing" || lifecycleState === "quitting") {
      return {
        ...snapshot,
        source: "state_plane",
        state: lifecycleState,
        pipeReachable: false,
        pipeLatencyMs,
        pipeError: pipeError ?? `native broker reports managed domain generation ${snapshot.managedGeneration} is ${lifecycleState}`,
        editorStatus: lifecycleState,
      };
    }

    if (lifecycleState === "ready") {
      const snapshotStatus = parseEditorStatus(snapshot.editorStatus ?? "editing");
      return {
        ...snapshot,
        source: "state_plane",
        state: "ready",
        pipeReachable: false,
        pipeLatencyMs,
        pipeError,
        editorStatus: snapshotStatus.editorStatus,
        focusState: snapshot.focusState ?? snapshotStatus.focusState,
        windowState: snapshot.windowState ?? snapshotStatus.windowState,
        capabilities: snapshot.capabilities ?? [],
        ...pipelineStatus,
      };
    }

    return {
      ...snapshot,
      source: "state_plane",
      state: lifecycleState,
      pipeReachable: false,
      pipeLatencyMs,
      pipeError,
    };
  }

  private loadBridgeInfo(): BridgeInfo {
    const bridgePath = process.env.PI_UNITY_BRIDGE_FILE
      ? resolve(process.env.PI_UNITY_BRIDGE_FILE)
      : join(this.resolveUnityProjectRoot(), "Library", "PiUnityHarness", "bridge.json");

    if (!existsSync(bridgePath)) {
      throw new Error(
        `Cannot find the Unity bridge file: ${bridgePath}. Make sure the Unity Editor is running and com.pi.unity-harness is installed.`,
      );
    }

    let rawText = readFileSync(bridgePath, "utf8");
    if (rawText.charCodeAt(0) === 0xfeff) rawText = rawText.slice(1);
    const info = JSON.parse(rawText) as BridgeInfo;
    if (!info.pipe || !info.token) {
      throw new Error(`bridge.json is missing pipe/token: ${bridgePath}`);
    }

    const changed =
      !this.bridge ||
      this.bridge.pipe !== info.pipe ||
      this.bridge.token !== info.token;
    this.bridge = info;
    if (changed) {
      this.pipelineCommandCache = undefined;
      this.pipelineCacheProject = undefined;
      this.pipelineCacheGeneration = undefined;
    }
    if (changed && this.socket) {
      this.stopHeartbeat();
      this.socket.destroy();
      this.socket = undefined;
      this.connectPromise = undefined;
    }
    return info;
  }

  private readStatePlaneSnapshot(bridge: BridgeInfo) {
    return readStatePlaneSnapshotByBridge(bridge);
  }

  private async ensureConnected(bridge: BridgeInfo, timeoutMs = 5000) {
    if (this.socket && !this.socket.destroyed) return;
    if (this.connectPromise) { await this.connectPromise; return; }

    this.connectPromise = new Promise<void>((resolvePromise, rejectPromise) => {
      const socket = net.createConnection(bridge.pipe);
      let timer: NodeJS.Timeout;
      const cleanup = () => {
        clearTimeout(timer);
        socket.removeAllListeners("connect");
        socket.removeAllListeners("error");
      };
      timer = setTimeout(() => {
        cleanup();
        socket.destroy();
        this.connectPromise = undefined;

        // After a timeout, check whether the state plane shows the pipe occupied by another session
        let occupiedHint = "";
        try {
          const snapshot = readStatePlaneSnapshotByBridge(bridge);
          if (snapshot?.connected === true) {
            occupiedHint = " (bridge occupied by another pi/codex session — close the other session or restart the Unity Editor to release the pipe)";
          }
        } catch { /* A diagnostic failure must not affect the main logic */ }

        rejectPromise(new Error(`connect timed out after ${timeoutMs}ms ${bridge.pipe}${occupiedHint}`));
      }, timeoutMs);

      socket.on("connect", () => {
        cleanup();
        this.socket = socket;
        this.installSocketHandlers(socket);
        this.startHeartbeat();
        resolvePromise();
      });

      socket.on("error", (error) => {
        cleanup();
        socket.destroy();
        this.connectPromise = undefined;
        rejectPromise(error);
      });
    });

    try { await this.connectPromise; } finally { this.connectPromise = undefined; }
  }

  private installSocketHandlers(socket: net.Socket) {
    socket.setEncoding("utf8");
    socket.on("data", (chunk: string) => {
      this.lineBuffer += chunk;
      while (true) {
        const index = this.lineBuffer.indexOf("\n");
        if (index < 0) break;
        const line = this.lineBuffer.slice(0, index).trim();
        this.lineBuffer = this.lineBuffer.slice(index + 1);
        if (!line) continue;
        this.handleLine(line);
      }
    });

    socket.on("close", () => {
      this.socket = undefined;
      this.stopHeartbeat();
      this.lineBuffer = "";
      for (const [id, pending] of this.pending) {
        clearTimeout(pending.timer);
        pending.reject(new Error(`unity bridge disconnected before request ${id} completed`));
      }
      this.pending.clear();
    });

    socket.on("error", () => { socket.destroy(); });
  }

  private handleLine(line: string) {
    const message = JSON.parse(line) as {
      reply_to?: string; ok?: boolean; result?: unknown; error?: string;
      error_type?: string; type?: string; event?: string; payload?: unknown;
    };
    if (message.type === "event") return;

    const replyTo = message.reply_to ?? "";
    const pending = this.pending.get(replyTo);
    if (!pending) return;

    clearTimeout(pending.timer);
    this.pending.delete(replyTo);
    if (message.ok) {
      pending.resolve(message.result);
    } else {
      const errorType = message.error_type ?? "unknown";
      const errorMessage = message.error ?? "unity request failed";
      const heartbeatHint = errorType === "managed_heartbeat_timeout"
        ? " (Unity main thread stalled, likely a blocking call in eval code; the harness auto-recovers within ~1s after the block ends — retry the request then)"
        : "";
      const err = new Error(`[${errorType}] ${errorMessage}${heartbeatHint}`);
      (err as any).errorType = errorType;
      pending.reject(err);
    }
  }

  private startHeartbeat() {
    if (this.heartbeatTimer) return;
    this.heartbeatTimer = setInterval(() => {
      if (!this.socket || this.socket.destroyed || this.heartbeatInFlight || this.pending.size > 0) return;
      this.heartbeatInFlight = true;
      this.request("ping", {}, CLIENT_HEARTBEAT_TIMEOUT_MS)
        .catch(() => { /* request() destroys the abnormal socket itself */ })
        .finally(() => { this.heartbeatInFlight = false; });
    }, CLIENT_HEARTBEAT_INTERVAL_MS);
    this.heartbeatTimer.unref?.();
  }

  private stopHeartbeat() {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = undefined;
    }
    this.heartbeatInFlight = false;
  }

  private resolveUnityProjectRoot(): string {
    if (this.projectRootOverride) return this.projectRootOverride;
    throw new Error(
      "No Unity project configured. Run /unity-discover to scan running Unity instances and connect.",
    );
  }
}

// ---- Extension entry ----

export default function (pi: ExtensionAPI) {
  const client = new UnityBridgeClient();
  const registeredPipelineTools = new Set<string>();
  const activePipelineTools = new Map<string, PipelineCommandInfo>();

  // Config: defaults ← ~/.pi/agent/settings.json ← <cwd>/.pi/settings.json ← env
  let harnessSettings: UnityHarnessSettings = loadUnityHarnessSettings(process.cwd());
  let runtimeEnabled = harnessSettings.enabled;
  let sessionCwd = process.cwd();
  /** Tools/commands (except /unity-harness) are registered lazily only when enabled. */
  let featuresRegistered = false;

  const isEnabled = () => runtimeEnabled;

  const assertEnabled = () => {
    if (!runtimeEnabled) {
      throw new Error(
        `pi-unity-harness is disabled. Enable with /unity-harness on, or set "${SETTINGS_KEY}.enabled": true in ~/.pi/agent/settings.json.`,
      );
    }
  };

  /**
   * Hard gate for tool visibility.
   * Pi activates all extension tools on load via includeAllExtensionTools; we must
   * strip unity_* after that, and re-strip before every agent turn.
   */
  const applyToolActivation = () => {
    try {
      const active = pi.getActiveTools().filter((name) => {
        if (!isUnityManagedTool(name)) return true;
        return runtimeEnabled;
      });

      if (runtimeEnabled && featuresRegistered) {
        for (const name of CORE_UNITY_TOOLS) {
          if (!active.includes(name)) active.push(name);
        }
        for (const toolName of activePipelineTools.keys()) {
          if (!active.includes(toolName)) active.push(toolName);
        }
      }

      pi.setActiveTools(active);
    } catch (error) {
      // Runtime may not be bound yet during factory load; session_start/before_agent_start will retry.
      console.warn(
        `[pi-unity-harness] applyToolActivation failed: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  };

  const setEnabled = (
    enabled: boolean,
    opts?: {
      persist?: "global" | "project";
      notify?: (msg: string, level?: "info" | "warn") => void;
      registerFeatures?: () => void;
    },
  ) => {
    runtimeEnabled = enabled;
    if (!enabled) {
      client.close();
      activePipelineTools.clear();
    } else {
      opts?.registerFeatures?.();
    }
    applyToolActivation();
    if (opts?.persist) {
      const { path } = persistEnabled(enabled, opts.persist, sessionCwd);
      harnessSettings = loadUnityHarnessSettings(sessionCwd);
      // After persist, env override still wins if set
      if (process.env.PI_UNITY_HARNESS_ENABLED !== undefined) {
        opts.notify?.(
          `pi-unity-harness ${enabled ? "enabled" : "disabled"} saved to ${path} (note: PI_UNITY_HARNESS_ENABLED=${process.env.PI_UNITY_HARNESS_ENABLED} still overrides on next load)`,
          "warn",
        );
      } else {
        opts.notify?.(`pi-unity-harness ${enabled ? "enabled" : "disabled"} and saved to ${path}`, "info");
      }
    } else {
      opts.notify?.(`pi-unity-harness ${enabled ? "enabled" : "disabled"} for this session`, "info");
    }
  };

  const executePipelineCommand = async (command: PipelineCommandInfo, params: Record<string, unknown> | undefined) => {
    assertEnabled();
    const parameters = params ?? {};
    const parametersJson = JSON.stringify(parameters);
    const timeoutMs = pipelineCommandTimeoutMs(command.name, parameters);
    const result = await client.request("command", { name: command.name, parametersJson }, timeoutMs);
    return {
      content: [{ type: "text", text: String(result?.output ?? "(ok)") }],
      details: result,
    };
  };

  const refreshActiveTools = () => {
    applyToolActivation();
  };

  const registerPipelineTools = async () => {
    let list: PipelineCommandList;
    try {
      list = await client.listPipelineCommands(true);
    } catch {
      // Transport errors (e.g. while the editor is reloading) keep the old tools; only degrade on pipelineAvailable:false.
      return;
    }

    activePipelineTools.clear();
    if (!list.pipelineAvailable) {
      refreshActiveTools();
      return;
    }

    for (const command of list.commands) {
      if (!command?.name || command.runtimeOnly || PIPELINE_TOOL_EXCLUDE.has(command.name) || !PIPELINE_SHORTCUT_COMMANDS.has(command.name)) continue;
      const toolName = normalizePipelineToolName(command.name);
      if (activePipelineTools.has(toolName)) {
        console.warn(
          `[pi-unity-harness] pipeline tool name collision: "${command.name}" and "${activePipelineTools.get(toolName)!.name}" both normalize to "${toolName}". Skipping "${command.name}".`,
        );
        continue;
      }
      activePipelineTools.set(toolName, command);

      if (registeredPipelineTools.has(toolName)) continue;
      registeredPipelineTools.add(toolName);
      pi.registerTool({
        name: toolName,
        label: `Unity ${command.name}`,
        description: command.description ?? `Execute Unity Pipeline command ${command.name}`,
        promptSnippet: `Execute Unity Pipeline command ${command.name} through the reload-stable pipe bridge.`,
        promptGuidelines: [
          `Use ${toolName} when you need the frequent Unity Pipeline command ${command.name}.`,
          "Use unity_pipeline to discover or run less common Unity Pipeline commands.",
        ],
        parameters: schemaToTypeBox(Type, command.schema, command.parameters),
        async execute(_toolCallId, params) {
          const current = activePipelineTools.get(toolName);
          if (!current) {
            throw new Error(`pipeline unavailable or command disabled: ${command.name}`);
          }

          return await executePipelineCommand(current, params ?? {});
        },
      });
    }

    refreshActiveTools();
  };

  // ---- Features: tools registered only when enabled (hard gate) ----
  // Slash commands /unity-discover and /unity-install are always registered below.
  const ensureFeaturesRegistered = (): void => {
    if (featuresRegistered) return;
    featuresRegistered = true;
    registerFeatureSurface();
  };

  /** Scan running Unity editors and auto-connect when possible. */
  const autoConnectUnity = async (ctx: {
    ui: {
      setStatus: (id: string, text?: string) => void;
      notify: (message: string, type?: "info" | "warn" | "error" | "success") => void;
    };
  }): Promise<void> => {
    ensureFeaturesRegistered();
    applyToolActivation();
    client.configure(sessionCwd);

    const instances = discoverUnityInstances();
    const readyInstances = instances.filter((i) => i.bridgeReady && !i.pipeOccupied);
    const occupiedInstances = instances.filter((i) => i.bridgeReady && i.pipeOccupied);

    if (readyInstances.length === 1) {
      client.setProjectRoot(readyInstances[0].projectPath);
      const name = basename(readyInstances[0].projectPath);
      ctx.ui.setStatus("pi-unity", `unity bridge: ${name}`);
      ctx.ui.notify(`Connected to Unity: ${name}`, "info");
      await registerPipelineTools();
      return;
    }

    if (readyInstances.length > 1) {
      client.setProjectRoot(readyInstances[0].projectPath);
      const name = basename(readyInstances[0].projectPath);
      ctx.ui.setStatus("pi-unity", `unity bridge: ${name} (+${readyInstances.length - 1} more)`);
      ctx.ui.notify(
        `Connected to Unity: ${name} (${readyInstances.length - 1} more available; switch with /unity-discover)`,
        "info",
      );
      await registerPipelineTools();
      return;
    }

    if (occupiedInstances.length > 0) {
      const names = occupiedInstances.map((i) => basename(i.projectPath)).join(", ");
      ctx.ui.setStatus("pi-unity", `bridge occupied by another session (${names})`);
      ctx.ui.notify(
        `Unity bridge occupied by another session: ${names}. Close the occupying session, then run /unity-discover.`,
        "warn",
      );
      return;
    }

    if (instances.length > 0) {
      ctx.ui.setStatus("pi-unity", `found ${instances.length} Unity, bridge not installed (/unity-install)`);
      ctx.ui.notify(
        `Found ${instances.length} Unity instance(s) without the bridge installed. Install with /unity-install.`,
        "warn",
      );
      return;
    }

    ctx.ui.setStatus("pi-unity", "enabled, no Unity detected (/unity-discover)");
    ctx.ui.notify("pi-unity-harness enabled, but no running Unity detected. Start the Editor, then use /unity-discover.", "info");
  };

  // ---- session_start: auto-scan and connect ----

  pi.on("session_start", async (_event, ctx) => {
    sessionCwd = ctx.cwd;
    harnessSettings = loadUnityHarnessSettings(ctx.cwd);
    runtimeEnabled = harnessSettings.enabled;
    client.configure(ctx.cwd);

    if (!runtimeEnabled) {
      applyToolActivation();
      ctx.ui.setStatus("pi-unity", "disabled (/unity-harness on)");
      return;
    }

    await autoConnectUnity(ctx);
  });

  // Re-enforce disable before every agent turn (other extensions may re-enable tools).
  // When enabled, append the observe→act→verify workflow to the system prompt.
  pi.on("before_agent_start", async (event) => {
    if (!runtimeEnabled) {
      applyToolActivation();
      return;
    }

    const current = event?.systemPrompt ?? "";
    if (current.includes("Unity verify loop (required)")) {
      return;
    }

    return {
      systemPrompt: current ? `${current}\n\n${UNITY_VERIFY_WORKFLOW_PROMPT}` : UNITY_VERIFY_WORKFLOW_PROMPT,
    };
  });

  // Hard block unity tool calls even if they remain active somehow.
  pi.on("tool_call", async (event) => {
    if (runtimeEnabled) return;
    if (!isUnityManagedTool(event.toolName)) return;
    return {
      block: true,
      reason: `pi-unity-harness is disabled. Use /unity-harness on or set "${SETTINGS_KEY}.enabled": true in ~/.pi/agent/settings.json.`,
    };
  });

  pi.on("session_shutdown", async () => {
    client.close();
  });

  // ---- Enable switch (always registered) ----

  pi.registerCommand("unity-harness", {
    description: "Enable/disable pi-unity-harness (status | on | off | on --persist | off --persist | on --project | off --project)",
    getArgumentCompletions(argumentPrefix) {
      const commands = ["status", "on", "off", "on --persist", "off --persist", "on --project", "off --project"];
      const toItem = (label: string) => ({ label, value: label });
      const prefix = argumentPrefix.trim();
      if (!prefix) return commands.map(toItem);
      return commands.filter((label) => label.startsWith(prefix)).map(toItem);
    },
    handler: async (args, ctx) => {
      const tokens = (args ?? "").trim().split(/\s+/).filter(Boolean);
      const action = (tokens[0] || "status").toLowerCase();
      const flags = new Set(tokens.slice(1).map((t) => t.toLowerCase()));
      const persistScope = flags.has("--project")
        ? "project" as const
        : flags.has("--persist") || flags.has("--global")
          ? "global" as const
          : undefined;

      if (action === "status" || action === "") {
        const info = inspectUnityHarnessSettings(sessionCwd);
        const activeUnity = (() => {
          try {
            return pi.getActiveTools().filter(isUnityManagedTool);
          } catch {
            return [];
          }
        })();
        const lines = [
          `runtime enabled: ${runtimeEnabled ? "on" : "off"}`,
          `features registered: ${featuresRegistered ? "yes" : "no"}`,
          `resolved enabled: ${info.settings.enabled ? "on" : "off"}`,
          `settings key: ${SETTINGS_KEY}`,
          `global file: ${info.globalPath} (exists=${info.globalExists})`,
          `global raw: ${JSON.stringify(info.globalRaw)}`,
          `project file: ${info.projectPath ?? "(none)"} (exists=${info.projectExists})`,
          `project raw: ${JSON.stringify(info.projectRaw)}`,
          `env PI_UNITY_HARNESS_ENABLED: ${info.env ?? "(unset)"}`,
          `active unity tools: ${activeUnity.length ? activeUnity.join(", ") : "(none)"}`,
          "",
          "usage:",
          "  /unity-harness status",
          "  /unity-harness on | off              (this session)",
          "  /unity-harness on --persist          (session + ~/.pi/agent/settings.json)",
          "  /unity-harness off --persist",
          "  /unity-harness on --project          (session + <cwd>/.pi/settings.json)",
          "  /unity-harness off --project",
          "  env PI_UNITY_HARNESS_ENABLED=0|1    (process override)",
          "",
          "NOTE: edit ~/.pi/agent/settings.json (not ~/.pi/settings.json).",
        ];
        ctx.ui.notify(lines.join("\n"), "info");
        return;
      }

      if (action === "on" || action === "enable") {
        setEnabled(true, {
          persist: persistScope,
          registerFeatures: ensureFeaturesRegistered,
          notify: (msg, level) => ctx.ui.notify(msg, level ?? "info"),
        });
        if (runtimeEnabled) {
          // Register tools + auto-scan/connect immediately (do not require a separate /unity-discover).
          await autoConnectUnity(ctx);
        }
        return;
      }

      if (action === "off" || action === "disable") {
        setEnabled(false, {
          persist: persistScope,
          notify: (msg, level) => ctx.ui.notify(msg, level ?? "info"),
        });
        ctx.ui.setStatus("pi-unity", "disabled (/unity-harness on)");
        return;
      }

      ctx.ui.notify(
        `Unknown /unity-harness action: ${action}. Use status | on | off [--persist|--project]`,
        "warn",
      );
    },
  });

  // Slash commands always available (guarded when disabled), so /unity-discover exists
  // even before tools are registered.
  pi.registerCommand("unity-discover", {
    description: "Scan running Unity Editor instances and choose one to connect",
    handler: async (_args, ctx) => {
      if (!isEnabled()) {
        ctx.ui.notify("pi-unity-harness is disabled. Use /unity-harness on first.", "warn");
        return;
      }
      ensureFeaturesRegistered();
      applyToolActivation();
      const instances = discoverUnityInstances();

      if (instances.length === 0) {
        ctx.ui.notify("No running Unity Editor detected", "warn");
        return;
      }

      const readyInstances = instances.filter((i) => i.bridgeReady && !i.pipeOccupied);
      const occupiedInstances = instances.filter((i) => i.bridgeReady && i.pipeOccupied);
      const notReady = instances.filter((i) => !i.bridgeReady);

      // Build the choice list
      const choices: { label: string; value: string; hint?: string }[] = [];
      for (const inst of readyInstances) {
        const name = basename(inst.projectPath);
        choices.push({ label: `[ready] ${name}`, value: inst.projectPath, hint: `PID ${inst.pid}` });
      }
      for (const inst of occupiedInstances) {
        const name = basename(inst.projectPath);
        choices.push({ label: `[occupied] ${name}`, value: inst.projectPath, hint: `PID ${inst.pid} (occupied by another session)` });
      }
      for (const inst of notReady) {
        const name = basename(inst.projectPath);
        choices.push({ label: `[no bridge] ${name}`, value: inst.projectPath, hint: `PID ${inst.pid}` });
      }

      if (occupiedInstances.length > 0 && readyInstances.length === 0 && notReady.length === 0) {
        ctx.ui.notify(
          `All Unity bridges are occupied by other sessions: ${occupiedInstances.map((i) => basename(i.projectPath)).join(", ")}. Close the occupying session(s), then retry.`,
          "warn",
        );
        return;
      }

      if (choices.length === 1) {
        if (readyInstances.length === 1) {
          client.setProjectRoot(choices[0].value);
          ctx.ui.notify(`Connected: ${basename(choices[0].value)}`, "info");
          ctx.ui.setStatus("pi-unity", `unity bridge: ${basename(choices[0].value)}`);
          await registerPipelineTools();
        } else {
          ctx.ui.notify(`The only Unity bridge is occupied: ${basename(choices[0].value)}. Close the occupying session, then retry.`, "warn");
        }
        return;
      }

      const chosen = await ctx.ui.select("Choose a Unity instance to connect:", choices.map((c) => ({
        label: c.label,
        value: c.value,
        hint: c.hint,
      })));

      if (chosen) {
        const inst = instances.find((i) => i.projectPath === chosen);
        client.setProjectRoot(chosen);
        if (inst?.bridgeReady && !inst?.pipeOccupied) {
          ctx.ui.notify(`Connected: ${basename(chosen)}`, "info");
          ctx.ui.setStatus("pi-unity", `unity bridge: ${basename(chosen)}`);
          await registerPipelineTools();
        } else if (inst?.pipeOccupied) {
          ctx.ui.notify(`Project set: ${basename(chosen)} (bridge occupied by another session; connection may fail)`, "warn");
          ctx.ui.setStatus("pi-unity", `unity: ${basename(chosen)} (occupied)`);
        } else {
          ctx.ui.notify(`Project set: ${basename(chosen)} (bridge not ready; install with /unity-install)`, "warn");
          ctx.ui.setStatus("pi-unity", `unity: ${basename(chosen)} (no bridge)`);
        }
      }
    },
  });

  pi.registerCommand("unity-install", {
    description: "Install the com.pi.unity-harness package into a Unity project",
    handler: async (args, ctx) => {
      if (!isEnabled()) {
        ctx.ui.notify("pi-unity-harness is disabled. Use /unity-harness on first.", "warn");
        return;
      }
      let projectPath = args?.trim();

      if (!projectPath) {
        // No arguments: scan running instances without the bridge and let the user pick
        const instances = discoverUnityInstances();
        const notReady = instances.filter((i) => !i.bridgeReady);

        if (notReady.length === 0) {
          ctx.ui.notify(
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
          const chosen = await ctx.ui.select(
            "Choose a project to install:",
            notReady.map((i) => ({
              label: basename(i.projectPath),
              value: i.projectPath,
              hint: `PID ${i.pid}`,
            })),
          );
          if (!chosen) return;
          projectPath = chosen;
        }
      }

      const resolvedProject = resolve(projectPath);
      const harnessResult = installPiUnityHarness(resolvedProject);
      if (harnessResult.ok) {
        ctx.ui.notify(harnessResult.message, "info");
      } else {
        ctx.ui.notify(harnessResult.message, "error");
        return;
      }

      const unityVersion = readProjectUnityVersion(resolvedProject);
      const major = parseUnityMajorVersion(unityVersion);
      const pipelineStatus = getPipelineInstallStatus(resolvedProject);
      if (!pipelineStatus.installed) {
        const isUnity6 = major !== undefined && major >= 6000;
        const title = isUnity6 ? "Install com.unity.pipeline?" : "Install the pipeline compat fork?";
        const detail = isUnity6
          ? `The project Unity version is ${unityVersion ?? "unknown"} (Unity 6+). Add the official ${PIPELINE_PACKAGE_NAME}@${PIPELINE_PACKAGE_VERSION}?`
          : `The project Unity version is ${unityVersion ?? "unknown"} (pre-Unity 6); the official pipeline is unavailable. Add ${PIPELINE_PACKAGE_NAME} (compat fork, file: pointing at unity/com.pi.pipeline.compat) as the command surface? Use harness unity_eval instead of Roslyn eval/HotReload.`;
        const ok = await ctx.ui.confirm(title, detail);
        if (ok) {
          const pipelineResult = installUnityPipelineForProject(resolvedProject, major);
          ctx.ui.notify(pipelineResult.message, pipelineResult.ok ? "info" : "error");
        }
      } else {
        ctx.ui.notify(
          `pipeline already present: ${pipelineStatus.packageName}@${pipelineStatus.version} (${pipelineStatus.flavor})`,
          "info",
        );
      }
    },
  });

  // If settings say enabled at load time, register tools now.
  if (runtimeEnabled) {
    ensureFeaturesRegistered();
  }

  /** Register LLM tools only (slash commands are always registered above). */
  function registerFeatureSurface(): void {
  // ---- Tools (LLM-callable) ----

  pi.registerTool({
    name: "unity_discover",
    label: "Unity Discover",
    description: "Scan running Unity Editor instances, list connectable projects, and auto-select one.",
    promptSnippet: "Use unity_discover to scan for running Unity Editors and connect to one.",
    promptGuidelines: [
      "Use unity_discover when you need to find available Unity instances or switch targets.",
      "Call this before unity_ping if you are unsure which Unity project is connected.",
    ],
    parameters: Type.Object({
      projectPath: Type.Optional(Type.String({ description: "Project path to connect to (optional; auto-selects the first ready one when omitted)" })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const instances = discoverUnityInstances();
      const readyInstances = instances.filter((i) => i.bridgeReady && !i.pipeOccupied);
      const occupiedInstances = instances.filter((i) => i.bridgeReady && i.pipeOccupied);

      if (params.projectPath) {
        client.setProjectRoot(resolve(params.projectPath));
      } else if (readyInstances.length > 0 && !client.getProjectRoot()) {
        client.setProjectRoot(readyInstances[0].projectPath);
      }

      const current = client.getProjectRoot();
      if (current && instances.some((i) => i.bridgeReady && !i.pipeOccupied && resolve(i.projectPath) === resolve(current))) {
        await registerPipelineTools();
      }
      const pipelineStatus = current ? getPipelineInstallStatus(current) : undefined;

      return {
        content: [{
          type: "text",
          text: JSON.stringify({
            current: current ?? null,
            instances: instances.map((i) => ({
              projectPath: i.projectPath,
              pid: i.pid,
              bridgeReady: i.bridgeReady,
              pipeOccupied: i.pipeOccupied ?? false,
              selected: current ? resolve(i.projectPath) === resolve(current) : false,
              pipeline: getPipelineInstallStatus(i.projectPath),
            })),
            pipelineAvailable: client.pipelineStatusSnapshot().pipelineAvailable,
            pipelineCommandCount: client.pipelineStatusSnapshot().pipelineCommandCount,
            pipeline: pipelineStatus,
            hint: readyInstances.length === 0 && occupiedInstances.length > 0
              ? `Found ${occupiedInstances.length} bridge(s), all occupied by other sessions. Close the occupying session(s), then retry /unity-discover.`
              : readyInstances.length === 0 && instances.length > 0
                ? `Found ${instances.length} running Unity instance(s) without the bridge installed. Install with /unity-install.`
                : undefined,
          }, null, 2),
        }],
        details: { current, instances, pipeline: pipelineStatus, pipelineStatus: client.pipelineStatusSnapshot() },
      };
    },
  });

  // ---- unity_ping ----

  pi.registerTool({
    name: "unity_ping",
    label: "Unity Ping",
    description: "Check whether the Unity native broker is online.",
    promptSnippet: "Use unity_ping to verify that the Unity bridge pipe is reachable before deeper Unity automation.",
    promptGuidelines: ["Use unity_ping when Unity connectivity is uncertain or a prior Unity request failed."],
    parameters: Type.Object({}),
    async execute() {
      assertEnabled();
      const result = await client.request("ping", {});
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_yolo ----

  pi.registerTool({
    name: "unity_yolo",
    label: "Unity YOLO",
    description: "Set the Unity Editor dialog auto-handling policy. off=do nothing detect=detect only safe-auto=close safe dialogs automatically",
    promptSnippet: "Use unity_yolo { mode: \"safe-auto\" } to auto-dismiss modal dialogs when the Editor is blocked.",
    promptGuidelines: [
      "Use unity_yolo before triggering operations that may cause modal dialogs (scene changes, import settings).",
      "safe-auto only clicks whitelisted buttons: Don't Save on scene dialogs, Apply on import dialogs, OK/Yes on generic prompts.",
    ],
    parameters: Type.Object({
      mode: Type.String({
        description: "Dialog handling mode: off, detect, safe-auto",
        default: "detect",
        enum: ["off", "detect", "safe-auto"],
      }),
    }),
    async execute(_toolCallId: string, params: { mode?: string }) {
      assertEnabled();
      const mode = params?.mode ?? "detect";
      const result = await client.request("set_yolo", { mode });
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_status ----

  pi.registerTool({
    name: "unity_status",
    label: "Unity Status",
    description: "Get the current Unity bridge and Editor status.",
    promptSnippet: "Use unity_status to inspect broker connectivity, managed state, and pending queue depth.",
    promptGuidelines: ["Use unity_status before retries when a Unity request times out or reports managed_reloading."],
    parameters: Type.Object({}),
    async execute() {
      assertEnabled();
      try { await client.listPipelineCommands(false, 5000); } catch { /* status may still degrade gracefully */ }
      const result = await client.status();
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_snapshot ----

  pi.registerTool({
    name: "unity_snapshot",
    label: "Unity Context Snapshot",
    description: "Fetch a bounded context snapshot of the Unity Editor, active scene hierarchy, current selection, and recent logs.",
    promptSnippet: "Use unity_snapshot to collect the current Unity context before planning or after changing a scene.",
    promptGuidelines: [
      "Prefer unity_snapshot over separate status, hierarchy, selection, and log calls when you need broad context.",
      "Keep the default bounds unless deeper hierarchy or component type information is necessary.",
      "Use logLevel=all when normal logs are needed; the default error filter keeps context concise.",
      "Call unity_snapshot before non-trivial Unity changes to observe baseline, and again after compile/play/tests to verify the outcome.",
      "Do not claim a Unity fix is done until a post-change unity_snapshot (or equivalent probe/tests) confirms the expected state.",
    ],
    parameters: Type.Object({
      maxDepth: Type.Optional(Type.Integer({ description: "Maximum hierarchy depth, default 3, range 0-20." })),
      maxNodes: Type.Optional(Type.Integer({ description: "Maximum hierarchy nodes, default 500, range 1-5000." })),
      logLimit: Type.Optional(Type.Integer({ description: "Number of recent logs, default 50, range 0-500." })),
      logLevel: Type.Optional(Type.String({ description: "Log filter: error, warning, log/info, or all. Default error." })),
      includeComponents: Type.Optional(Type.Boolean({ description: "Whether to include the component type list of each node, default false." })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const result = await client.request("context_snapshot", {
        maxDepth: params.maxDepth ?? 3,
        maxNodes: params.maxNodes ?? 500,
        logLimit: params.logLimit ?? 50,
        logLevel: params.logLevel ?? "error",
        includeComponents: params.includeComponents ?? false,
      }, 30000);
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_timeline ----

  pi.registerTool({
    name: "unity_timeline",
    label: "Unity Action Timeline",
    description: "Query the persistent Unity action-audit timeline; records request input summaries, results, durations, and success status.",
    promptSnippet: "Use unity_timeline to inspect recent audited Unity actions and failures.",
    promptGuidelines: [
      "Use unity_timeline after failures or long workflows to review what actually ran.",
      "Filter by action for a specific Pipeline command, or by requestType for eval/recompile/snapshot operations.",
      "Timeline queries are not audited themselves, avoiding recursive audit noise.",
    ],
    parameters: Type.Object({
      limit: Type.Optional(Type.Integer({ description: "Number of most recent operations, default 50, range 1-200." })),
      requestType: Type.Optional(Type.String({ description: "Filter by request type, e.g. command, validate_execute_code, context_snapshot, status." })),
      action: Type.Optional(Type.String({ description: "Filter by action; Pipeline commands use their command name." })),
      success: Type.Optional(Type.String({ description: "all, success, or failure; default all." })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const result = await client.request("timeline", {
        limit: params.limit,
        requestType: params.requestType,
        action: params.action,
        success: params.success,
      }, 10000);
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_pipeline ----

  pi.registerTool({
    name: "unity_pipeline",
    label: "Unity Pipeline",
    description: "Discover or execute Unity Pipeline commands. Empty parameters list available commands; pass command to execute one.",
    promptSnippet: "Use unity_pipeline to discover or execute Unity Pipeline commands through the pipe bridge.",
    promptGuidelines: [
      "Call unity_pipeline with no command when you need to know which Pipeline commands are available.",
      "Use the returned command names and parameter metadata to call unity_pipeline with command and params.",
      "Prefer shortcut tools only for frequent commands: unity_run_tests, unity_list_tests, unity_reload_file, unity_reload_file_override.",
      "Use unity_run_tests (or unity_pipeline run_tests) as part of the verify loop after code changes that should be covered by EditMode/PlayMode tests.",
    ],
    parameters: Type.Object({
      command: Type.Optional(Type.String({ description: "Pipeline command name; omitted to list all available commands." })),
      params: Type.Optional(Type.Unsafe({
        type: "object",
        description: "Parameters object passed to the Pipeline command.",
        additionalProperties: true,
      })),
      describeOnly: Type.Optional(Type.Boolean({ description: "Return only the command description and parameter schema without executing." })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      let list = await client.listPipelineCommands(false);
      let commands = filterPipelineCommands(list);

      const requestedCommand = normalizePipelineRequestedCommand(params.command);
      // The cache may lag behind command enable/disable state; force one refresh when a command is not found.
      if (shouldRefreshPipelineCommands(list, requestedCommand, commands)) {
        list = await client.listPipelineCommands(true);
        commands = filterPipelineCommands(list);
      }

      if (!list.pipelineAvailable) {
        if (requestedCommand) {
          throw new Error(`pipeline unavailable: cannot execute command "${requestedCommand}". Check unity_status and pipeline package installation.`);
        }
        const result = { pipelineAvailable: false, commands: [], count: 0 };
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
          details: result,
        };
      }

      if (!requestedCommand) {
        const result = {
          pipelineAvailable: true,
          count: commands.length,
          shortcutCommands: [...PIPELINE_SHORTCUT_COMMANDS].filter((name) => commands.some((command) => command.name === name)),
          commands: commands.map((command) => pipelineCommandSummary(command)),
        };
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
          details: result,
        };
      }

      const command = commands.find((item) => item.name === requestedCommand);
      if (!command) {
        const available = commands.map((item) => item.name).sort();
        throw new Error(`pipeline command not found or disabled: ${requestedCommand}. Available: ${available.join(", ")}`);
      }

      if (params.describeOnly === true) {
        const result = pipelineCommandSummary(command, true);
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
          details: result,
        };
      }

      return await executePipelineCommand(command, normalizePipelineCommandParams(params.params));
    },
  });

  // ---- unity_eval ----

  pi.registerTool({
    name: "unity_eval",
    label: "Unity Eval",
    description: "Execute a C# snippet on the Unity Editor main thread. Short expressions work; prefer unity_eval_file for multi-line code.",
    promptSnippet: "Use unity_eval for short C# probes on the Unity main thread; prefer unity_eval_file for multi-line scripts.",
    promptGuidelines: [
      "Use unity_eval for short UnityEditor or UnityEngine probes that must run inside the Editor process.",
      "Prefer unity_eval_file for multi-line or non-trivial C#: write Temp/PiUnityHarness/AgentScratch/*.repl with file tools, then call unity_eval_file — do not shell-concatenate C#.",
      "Never block the Unity main thread with Task.Wait/.Result/GetAwaiter().GetResult()/Thread.Sleep — it freezes the editor update loop and heartbeat. To await async work, return an IEnumerator (coroutine) from the eval code instead.",
      "Never call AssetDatabase.Refresh or other Domain Reload triggers inside unity_eval — use unity_recompile instead.",
      "After mutating Editor/runtime state with unity_eval, verify with unity_snapshot, tests, or PlayMode before claiming success.",
    ],
    parameters: Type.Object({
      code: Type.String({ description: "C# code to execute in the Unity Editor." }),
      timeoutMs: Type.Optional(Type.Number({ description: "Request timeout, default 20000 ms." })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const timeoutMs = typeof params.timeoutMs === "number" ? params.timeoutMs : 20000;
      const projectRoot = client.getProjectRoot();
      const codeSize = Buffer.byteLength(params.code, "utf8");
      let result;
      if (projectRoot && codeSize > INLINE_EVAL_CODE_LIMIT_BYTES) {
        let filePath: string | undefined;
        try {
          filePath = writeScratchRepl(projectRoot, params.code);
          result = await client.request("validate_execute_file", { filePath }, timeoutMs);
        } finally {
          if (filePath) removeScratchRepl(projectRoot, filePath);
        }
      } else {
        result = await client.request("validate_execute_code", { code: params.code }, timeoutMs);
      }
      return {
        content: [{ type: "text", text: String(result?.output ?? "(ok)") }],
        details: result,
      };
    },
  });

  // ---- unity_eval_file ----

  pi.registerTool({
    name: "unity_eval_file",
    label: "Unity Eval File",
    description: "Read C# from a .repl/.cs file and execute it on the Unity Editor main thread after validation. Relative paths resolve against the project root.",
    promptSnippet: "Use unity_eval_file to run multi-line C# from a project file (prefer Temp/PiUnityHarness/AgentScratch/*.repl).",
    promptGuidelines: [
      "Prefer unity_eval_file over unity_eval for multi-line or non-trivial C# scripts.",
      "Write the .repl with file tools under Temp/PiUnityHarness/AgentScratch/ (or another project path), then pass filePath — relative paths resolve against the Unity project root.",
      "Use // #repl-mode: top-level or // #repl-mode: class at the top of the file; do not mix top-level statements with class declarations.",
      "Never call AssetDatabase.Refresh or other Domain Reload triggers inside eval files — use unity_recompile instead.",
      "After mutating Editor/runtime state with unity_eval_file, verify with unity_snapshot, tests, or PlayMode before claiming success.",
    ],
    parameters: Type.Object({
      filePath: Type.String({ description: "Path to the C#/.repl file to execute; relative paths resolve against the Unity project root." }),
      timeoutMs: Type.Optional(Type.Number({ description: "Request timeout, default 20000 ms." })),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const projectRoot = client.getProjectRoot();
      if (!projectRoot) {
        throw new Error("No Unity project configured. Run unity_discover / /unity-discover to connect to an instance first.");
      }

      const timeoutMs = typeof params.timeoutMs === "number" ? params.timeoutMs : 20000;
      const { relativePath, absolutePath } = resolveEvalFilePath(projectRoot, String(params.filePath ?? ""));
      if (!existsSync(absolutePath)) {
        throw new Error(`eval file not found: ${absolutePath}`);
      }

      const result = await client.request("validate_execute_file", { filePath: relativePath }, timeoutMs);
      return {
        content: [{ type: "text", text: String(result?.output ?? "(ok)") }],
        details: { ...result, filePath: relativePath },
      };
    },
  });

  // ---- unity_recompile ----

  pi.registerTool({
    name: "unity_recompile",
    label: "Unity Recompile",
    description: "Trigger a Unity Editor recompile of C# scripts and return the compilation result and errors.",
    promptSnippet: "Use unity_recompile to trigger Unity script compilation and get compile errors.",
    promptGuidelines: [
      "Use unity_recompile after modifying C# scripts to trigger Unity compilation.",
      "This call blocks until compilation completes (up to 120s). Check the returned result for error details.",
      "On success: result.output is \"compilation_succeeded\". On failure: error_type is \"compile_error\" with the error summary.",
      "After a successful unity_recompile, verify behavior with unity_snapshot, unity_run_tests, or PlayMode — compile success alone is not task completion.",
    ],
    parameters: Type.Object({}),
    async execute(_toolCallId, _params) {
      assertEnabled();
      const timeoutMs = 120000; // Compilation may take a while
      const result = await client.request("recompile", {}, timeoutMs);
      return {
        content: [{ type: "text", text: String(result?.output ?? "(ok)") }],
        details: result,
      };
    },
  });
  } // end registerFeatureSurface
}
