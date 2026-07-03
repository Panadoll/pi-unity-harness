import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import net from "node:net";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";

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
}

interface PendingRequest {
  resolve: (value: any) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

const STATE_PLANE_MAGIC = 0x48554950;
const STATE_PLANE_VERSION = 1;
const STATE_PLANE_HEADER_SIZE = 64;
const STATE_PLANE_SLOT_COUNT = 2;
const STATE_PLANE_SLOT_SIZE = 64 * 1024;
const STATE_PLANE_SLOT_PAYLOAD_OFFSET = 24;
const INLINE_EVAL_CODE_LIMIT_BYTES = 512 * 1024;

function parseEditorStatus(value: unknown): { editorStatus: string; focusState?: string; windowState?: string } {
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

// ---- 扫描运行中的 Unity 实例 ----

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

      const ppMatch = cmdLine.match(/-projectPath\s+"([^"]+)"/i) ??
        cmdLine.match(/-projectPath\s+(\S+)/i);
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
        } catch { /* 损坏则视为未就绪 */ }
      }

      instances.push({ projectPath, pid, bridgeReady, bridgeInfo });
    }

    return instances;
  } catch {
    return [];
  }
}

// ---- 安装 pi-unity-harness ----

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

  try { unlinkSync(fullPath); } catch { /* 请求超时或外部清理时忽略 */ }
}

function cleanupScratchRepls(projectPath: string) {
  const dir = scratchReplDir(projectPath);
  if (!existsSync(dir)) return;

  try {
    for (const name of readdirSync(dir)) {
      if (!name.startsWith("pi-eval-") || !name.endsWith(".repl")) continue;
      try { unlinkSync(join(dir, name)); } catch { /* 文件可能正被外部进程清理 */ }
    }
  } catch { /* 目录不可读时不影响 bridge 使用 */ }
}

function installPiUnityHarness(projectPath: string, packageSourceDir?: string): { ok: boolean; message: string } {
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    return { ok: false, message: `找不到 Packages/manifest.json: ${projectPath} 可能不是 Unity 项目` };
  }

  let pkgDir: string;
  if (packageSourceDir) {
    pkgDir = resolve(packageSourceDir);
  } else {
    // 从 cwd 推断仓库根目录
    pkgDir = resolve(process.cwd(), "unity", "com.pi.unity-harness");
    if (!existsSync(join(pkgDir, "package.json"))) {
      // 尝试从父级仓库目录推断
      const alt = resolve(process.cwd(), "..", "pi-unity-harness", "unity", "com.pi.unity-harness");
      if (existsSync(join(alt, "package.json"))) {
        pkgDir = alt;
      }
    }
  }

  if (!existsSync(join(pkgDir, "package.json"))) {
    return { ok: false, message: `找不到 pi-unity-harness 包目录: ${pkgDir}` };
  }

  let relPath = relative(dirname(manifestPath), pkgDir).replace(/\\/g, "/");
  if (!relPath.startsWith(".")) relPath = "./" + relPath;

  try {
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    if (!manifest.dependencies) manifest.dependencies = {};

    const pkgName = "com.pi.unity-harness";
    if (manifest.dependencies[pkgName]) {
      return { ok: true, message: `${pkgName} 已安装 (${manifest.dependencies[pkgName]})` };
    }

    manifest.dependencies[pkgName] = `file:${relPath}`;
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n", "utf8");
    return {
      ok: true,
      message: `已添加 ${pkgName} -> file:${relPath}。Unity Editor 将自动解析包，解析完成后 bridge 即就绪。`,
    };
  } catch (error: any) {
    return { ok: false, message: `写入 manifest.json 失败: ${error.message}` };
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

  configure(cwd: string) { this.cwd = cwd; }

  setProjectRoot(path: string) {
    this.projectRootOverride = resolve(path);
    cleanupScratchRepls(this.projectRootOverride);
  }

  getProjectRoot(): string | undefined {
    try { return this.resolveUnityProjectRoot(); } catch { return undefined; }
  }

  close() {
    if (this.socket) { this.socket.destroy(); this.socket = undefined; }
    if (this.connectPromise) this.connectPromise = undefined;
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error(`bridge closed before request ${id} completed`));
    }
    this.pending.clear();
    this.lineBuffer = "";
  }

  async request(type: string, payload: Record<string, unknown>, timeoutMs = 20000) {
    const bridge = this.loadBridgeInfo();
    await this.ensureConnected(bridge);

    const id = String(this.nextId++);
    const message = JSON.stringify({ id, type, token: bridge.token, timeoutMs, payload }) + "\n";

    return await new Promise<any>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`unity request timed out after ${timeoutMs}ms (${type})`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      this.socket!.write(message, (error) => {
        if (!error) return;
        const entry = this.pending.get(id);
        if (!entry) return;
        clearTimeout(entry.timer);
        this.pending.delete(id);
        reject(error);
      });
    });
  }

  async status(pipeTimeoutMs = 2000) {
    const bridge = this.loadBridgeInfo();

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
      };
    }

    // PowerShell 读取共享内存只作为 pipe 失败后的降级路径，避免每次 status 都启动子进程。
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
        `找不到 Unity bridge 文件: ${bridgePath}。请确认 Unity Editor 已启动且已安装 com.pi.unity-harness 包。`,
      );
    }

    let rawText = readFileSync(bridgePath, "utf8");
    if (rawText.charCodeAt(0) === 0xfeff) rawText = rawText.slice(1);
    const info = JSON.parse(rawText) as BridgeInfo;
    if (!info.pipe || !info.token) {
      throw new Error(`bridge.json 缺少 pipe/token: ${bridgePath}`);
    }

    const changed =
      !this.bridge ||
      this.bridge.pipe !== info.pipe ||
      this.bridge.token !== info.token;
    this.bridge = info;
    if (changed && this.socket) {
      this.socket.destroy();
      this.socket = undefined;
      this.connectPromise = undefined;
    }
    return info;
  }

  private readStatePlaneSnapshot(bridge: BridgeInfo) {
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

  private async ensureConnected(bridge: BridgeInfo) {
    if (this.socket && !this.socket.destroyed) return;
    if (this.connectPromise) { await this.connectPromise; return; }

    this.connectPromise = new Promise<void>((resolvePromise, rejectPromise) => {
      const socket = net.createConnection(bridge.pipe);
      const cleanup = () => { socket.removeAllListeners("connect"); socket.removeAllListeners("error"); };

      socket.on("connect", () => {
        cleanup();
        this.socket = socket;
        this.installSocketHandlers(socket);
        resolvePromise();
      });

      socket.on("error", (error) => {
        cleanup();
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
      type?: string; event?: string; payload?: unknown;
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
      pending.reject(new Error(message.error ?? "unity request failed"));
    }
  }

  private resolveUnityProjectRoot(): string {
    if (this.projectRootOverride) return this.projectRootOverride;
    throw new Error(
      "未配置 Unity 项目。请使用 /unity-discover 扫描运行中的 Unity 实例并选择连接。",
    );
  }
}

// ---- 扩展入口 ----

export default function (pi: ExtensionAPI) {
  const client = new UnityBridgeClient();

  // ---- session_start: 自动扫描并连接 ----

  pi.on("session_start", async (_event, ctx) => {
    client.configure(ctx.cwd);

    const instances = discoverUnityInstances();
    const readyInstances = instances.filter((i) => i.bridgeReady);

    if (readyInstances.length === 1) {
      client.setProjectRoot(readyInstances[0].projectPath);
      const name = basename(readyInstances[0].projectPath);
      ctx.ui.setStatus("pi-unity", `unity bridge: ${name}`);
    } else if (readyInstances.length > 1) {
      client.setProjectRoot(readyInstances[0].projectPath);
      const name = basename(readyInstances[0].projectPath);
      ctx.ui.setStatus("pi-unity", `unity bridge: ${name} (+${readyInstances.length - 1} more)`);
    } else if (instances.length > 0) {
      ctx.ui.setStatus("pi-unity", `found ${instances.length} Unity, bridge not installed (/unity-install)`);
    } else {
      ctx.ui.setStatus("pi-unity", "no Unity detected (/unity-discover)");
    }
  });

  pi.on("session_shutdown", async () => {
    client.close();
  });

  // ---- Commands (用户输入) ----

  pi.registerCommand("unity-discover", {
    description: "扫描运行中的 Unity Editor 实例并选择连接",
    handler: async (_args, ctx) => {
      const instances = discoverUnityInstances();

      if (instances.length === 0) {
        ctx.ui.notify("没有检测到运行中的 Unity Editor", "warn");
        return;
      }

      const readyInstances = instances.filter((i) => i.bridgeReady);
      const notReady = instances.filter((i) => !i.bridgeReady);

      // 构建选择列表
      const choices: { label: string; value: string; hint?: string }[] = [];
      for (const inst of readyInstances) {
        const name = basename(inst.projectPath);
        choices.push({ label: `[ready] ${name}`, value: inst.projectPath, hint: `PID ${inst.pid}` });
      }
      for (const inst of notReady) {
        const name = basename(inst.projectPath);
        choices.push({ label: `[no bridge] ${name}`, value: inst.projectPath, hint: `PID ${inst.pid}` });
      }

      if (choices.length === 1) {
        client.setProjectRoot(choices[0].value);
        ctx.ui.notify(`已连接: ${basename(choices[0].value)}`, "info");
        ctx.ui.setStatus("pi-unity", `unity bridge: ${basename(choices[0].value)}`);
        return;
      }

      const chosen = await ctx.ui.select("选择要连接的 Unity 实例:", choices.map((c) => ({
        label: c.label,
        value: c.value,
        hint: c.hint,
      })));

      if (chosen) {
        const inst = instances.find((i) => i.projectPath === chosen);
        client.setProjectRoot(chosen);
        if (inst?.bridgeReady) {
          ctx.ui.notify(`已连接: ${basename(chosen)}`, "info");
          ctx.ui.setStatus("pi-unity", `unity bridge: ${basename(chosen)}`);
        } else {
          ctx.ui.notify(`已设置项目: ${basename(chosen)}（bridge 未就绪，请用 /unity-install 安装）`, "warn");
          ctx.ui.setStatus("pi-unity", `unity: ${basename(chosen)} (no bridge)`);
        }
      }
    },
  });

  pi.registerCommand("unity-install", {
    description: "给 Unity 项目安装 com.pi.unity-harness 包",
    handler: async (args, ctx) => {
      let projectPath = args?.trim();

      if (!projectPath) {
        // 没有参数，扫描运行中的未安装实例让用户选
        const instances = discoverUnityInstances();
        const notReady = instances.filter((i) => !i.bridgeReady);

        if (notReady.length === 0) {
          ctx.ui.notify(
            instances.length > 0
              ? "所有运行中的 Unity 都已安装 bridge"
              : "没有检测到运行中的 Unity。请指定项目路径: /unity-install <path>",
            "warn",
          );
          return;
        }

        if (notReady.length === 1) {
          projectPath = notReady[0].projectPath;
        } else {
          const chosen = await ctx.ui.select(
            "选择要安装的项目:",
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

      const result = installPiUnityHarness(resolve(projectPath));
      if (result.ok) {
        ctx.ui.notify(result.message, "info");
      } else {
        ctx.ui.notify(result.message, "error");
      }
    },
  });

  // ---- Tools (LLM 可调用) ----

  pi.registerTool({
    name: "unity_discover",
    label: "Unity Discover",
    description: "扫描运行中的 Unity Editor 实例，列出可连接的项目并自动选择。",
    promptSnippet: "Use unity_discover to scan for running Unity Editors and connect to one.",
    promptGuidelines: [
      "Use unity_discover when you need to find available Unity instances or switch targets.",
      "Call this before unity_ping if you are unsure which Unity project is connected.",
    ],
    parameters: Type.Object({
      projectPath: Type.Optional(Type.String({ description: "指定要连接的项目路径（可选，不提供则自动选第一个就绪的）" })),
    }),
    async execute(_toolCallId, params) {
      const instances = discoverUnityInstances();
      const readyInstances = instances.filter((i) => i.bridgeReady);

      if (params.projectPath) {
        client.setProjectRoot(resolve(params.projectPath));
      } else if (readyInstances.length > 0 && !client.getProjectRoot()) {
        client.setProjectRoot(readyInstances[0].projectPath);
      }

      const current = client.getProjectRoot();

      return {
        content: [{
          type: "text",
          text: JSON.stringify({
            current: current ?? null,
            instances: instances.map((i) => ({
              projectPath: i.projectPath,
              pid: i.pid,
              bridgeReady: i.bridgeReady,
              selected: current ? resolve(i.projectPath) === resolve(current) : false,
            })),
            hint: readyInstances.length === 0 && instances.length > 0
              ? `检测到 ${instances.length} 个运行中的 Unity，但均未安装 bridge。使用 /unity-install 安装。`
              : undefined,
          }, null, 2),
        }],
        details: { current, instances },
      };
    },
  });

  // ---- unity_ping ----

  pi.registerTool({
    name: "unity_ping",
    label: "Unity Ping",
    description: "检查 Unity native broker 是否在线。",
    promptSnippet: "Use unity_ping to verify that the Unity bridge pipe is reachable before deeper Unity automation.",
    promptGuidelines: ["Use unity_ping when Unity connectivity is uncertain or a prior Unity request failed."],
    parameters: Type.Object({}),
    async execute() {
      const result = await client.request("ping", {});
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
    description: "获取 Unity bridge 与 Editor 当前状态。",
    promptSnippet: "Use unity_status to inspect broker connectivity, managed state, and pending queue depth.",
    promptGuidelines: ["Use unity_status before retries when a Unity request times out or reports managed_reloading."],
    parameters: Type.Object({}),
    async execute() {
      const result = await client.status();
      return {
        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        details: result,
      };
    },
  });

  // ---- unity_eval ----

  pi.registerTool({
    name: "unity_eval",
    label: "Unity Eval",
    description: "在 Unity Editor 主线程执行一段 C# 代码。",
    promptSnippet: "Use unity_eval to inspect or mutate Unity Editor state from C# on the main thread.",
    promptGuidelines: ["Use unity_eval for UnityEditor or UnityEngine operations that must run inside the Editor process."],
    parameters: Type.Object({
      code: Type.String({ description: "要在 Unity Editor 中执行的 C# 代码。" }),
      timeoutMs: Type.Optional(Type.Number({ description: "请求超时，默认 20000ms。" })),
    }),
    async execute(_toolCallId, params) {
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
}
