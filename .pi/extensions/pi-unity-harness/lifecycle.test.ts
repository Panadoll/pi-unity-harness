import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { EventEmitter } from "node:events";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import createExtension, {
  findNearestBridgeProject,
  getActiveMux,
  resolveSessionProjectPath,
} from "./index.ts";

function mockPi() {
  const tools: Array<{ name: string; parameters?: { properties?: Record<string, unknown> }; execute?: (...args: unknown[]) => unknown }> = [];
  const commands = new Map<string, { handler: (args: string, ctx: unknown) => unknown }>();
  const handlers = new Map<string, (...args: unknown[]) => unknown>();
  return {
    tools,
    commands,
    handlers,
    registerTool(def: { name: string; parameters: { properties?: Record<string, unknown> }; execute?: (...args: unknown[]) => unknown }) {
      tools.push(def);
    },
    registerCommand(name: string, def: { handler: (args: string, ctx: unknown) => unknown }) {
      commands.set(name, def);
    },
    on(event: string, handler: (...args: unknown[]) => unknown) {
      handlers.set(event, handler);
    },
  };
}

function commandCtx(cwd: string) {
  const notes: string[] = [];
  return {
    cwd,
    ui: {
      notify(message: unknown) {
        notes.push(typeof message === "string" ? message : JSON.stringify(message));
      },
    },
    notes,
  };
}

test("extension factory shuts mux on disable and recreates on enable", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnabled = process.env.PI_UNITY_HARNESS_ENABLED;
  const cwd = mkdtempSync(join(tmpdir(), "pi-unity-ext-"));
  process.env.PI_CODING_AGENT_DIR = cwd;
  delete process.env.PI_UNITY_HARNESS_ENABLED;
  const pi = mockPi();
  try {
    createExtension(pi as never);
    const start = pi.handlers.get("session_start");
    assert.ok(start);
    await start({}, { cwd });
    assert.ok(getActiveMux());

    const snapshot = pi.tools.find((t) => t.name === "unity_snapshot");
    assert.ok(snapshot?.parameters.properties?.fields);
    assert.ok(snapshot?.parameters.properties?.full);
    assert.equal(snapshot?.parameters.properties?.noComponents, undefined);

    const settings = pi.commands.get("unity-harness-settings");
    assert.ok(settings);
    const ctx = commandCtx(cwd);
    await settings.handler("disable", ctx);
    assert.equal(getActiveMux(), null);
    await settings.handler("enable", ctx);
    assert.ok(getActiveMux());
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnabled === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prevEnabled;
  }
});

// ---- 会话绑定：显式选择不跨新 cwd 保留 / autoConnect 优先级与代际 ----------------

/** 永不退出的假子进程：shutdown 会走完 500ms 兜底路径，给并发操作留出确定窗口。 */
function hangChild(): EventEmitter & { stdin: PassThrough; stdout: PassThrough; stderr: PassThrough; exitCode: number | null; kill: () => boolean } {
  const child = new EventEmitter() as EventEmitter & {
    stdin: PassThrough;
    stdout: PassThrough;
    stderr: PassThrough;
    exitCode: number | null;
    kill: () => boolean;
  };
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  child.kill = () => {
    child.exitCode = 1;
    child.emit("exit", 1);
    return true;
  };
  queueMicrotask(() => {
    if (child.exitCode === null) child.emit("spawn");
  });
  return child;
}

function hangSpawn(children: unknown[]) {
  return ((() => {
    const child = hangChild();
    children.push(child);
    return child;
  }) as unknown) as typeof spawn;
}

test("session_start with new cwd does not retain previous explicit selection", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnvProj = process.env.UNITY_PROJECT_PATH;
  const prevEnabled = process.env.PI_UNITY_HARNESS_ENABLED;
  const base = mkdtempSync(join(tmpdir(), "pi-unity-explicit-"));
  const projA = join(base, "ProjA");
  makeFakeBridge(projA);
  const projB = join(base, "ProjB");
  makeFakeBridge(projB);
  const cwdA = join(projA, "Assets");
  mkdirSync(cwdA, { recursive: true });
  const cwdB = join(projB, "Assets");
  mkdirSync(cwdB, { recursive: true });
  const cwdPlain = join(base, "Plain");
  mkdirSync(cwdPlain, { recursive: true });
  const explicit = join(base, "ExplicitProj");
  process.env.PI_CODING_AGENT_DIR = base;
  delete process.env.UNITY_PROJECT_PATH;
  delete process.env.PI_UNITY_HARNESS_ENABLED;
  const pi = mockPi();
  const children: unknown[] = [];
  try {
    createExtension(pi as never, { muxSpawnImpl: hangSpawn(children) });
    const start = pi.handlers.get("session_start");
    assert.ok(start);

    await start({}, { cwd: cwdA });
    let mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(projA)); // 最近本地 bridge

    // 显式选择（unity_discover projectPath）
    const disc = pi.tools.find((t) => t.name === "unity_discover");
    assert.ok(disc && typeof disc.execute === "function");
    await disc.execute!("id", { projectPath: explicit } as never);
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(explicit));

    // 再换到无本地 bridge 的 cwd：不得从扩展写入的 UNITY_PROJECT_PATH 继承旧显式选择
    await start({}, { cwd: cwdPlain });
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(mux!.fixedProjectPath, undefined);

    // 新 cwd 的会话：上一会话的显式选择不保留，按新 cwd 重新解析
    await start({}, { cwd: cwdB });
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(projB));

    // 相同 cwd 重开：显式选择保留
    await disc.execute!("id", { projectPath: explicit } as never);
    await start({}, { cwd: cwdB });
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(explicit));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnvProj === undefined) delete process.env.UNITY_PROJECT_PATH;
    else process.env.UNITY_PROJECT_PATH = prevEnvProj;
    if (prevEnabled === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prevEnabled;
  }
});

test("autoConnect: single scanned editor respects explicit/local binding, binds only when unbound", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnvProj = process.env.UNITY_PROJECT_PATH;
  const base = mkdtempSync(join(tmpdir(), "pi-unity-autoconnect-"));
  const scanned = join(base, "ScannedProj");
  const local = join(base, "LocalProj");
  makeFakeBridge(local);
  const explicit = join(base, "ExplicitProj");
  const cwdLocal = join(local, "Assets");
  mkdirSync(cwdLocal, { recursive: true });
  const cwdPlain = join(base, "empty");
  mkdirSync(cwdPlain, { recursive: true });
  process.env.PI_CODING_AGENT_DIR = base;
  delete process.env.UNITY_PROJECT_PATH;
  const discoverImpl = () => [{ projectPath: scanned, pid: 7, bridgeReady: true }];
  const pi = mockPi();
  const children: unknown[] = [];
  try {
    createExtension(pi as never, { muxSpawnImpl: hangSpawn(children), discoverInstances: discoverImpl });
    const start = pi.handlers.get("session_start");
    assert.ok(start);

    // 本地 bridge 已绑定：单编辑器扫描不覆盖
    await start({}, commandCtx(cwdLocal));
    let mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(local));
    assert.equal(children.length, 1); // 未因扫描重建

    // 显式选择已绑定：扫描不覆盖
    const disc = pi.tools.find((t) => t.name === "unity_discover");
    assert.ok(disc && typeof disc.execute === "function");
    await disc.execute!("id", { projectPath: explicit } as never);
    mux = getActiveMux();
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(explicit));

    // 无任何绑定：单编辑器扫描才绑定
    await start({}, commandCtx(cwdPlain));
    await new Promise((r) => setTimeout(r, 700)); // autoConnect 会等待旧 mux shutdown
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(scanned));
  } finally {
    const shutdown = pi.handlers.get("session_shutdown");
    if (shutdown) await shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnvProj === undefined) delete process.env.UNITY_PROJECT_PATH;
    else process.env.UNITY_PROJECT_PATH = prevEnvProj;
  }
});

test("autoConnect: stale scan aborted by disable, no mux recreated", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnvProj = process.env.UNITY_PROJECT_PATH;
  const base = mkdtempSync(join(tmpdir(), "pi-unity-autoconnect-stale-"));
  const scanned = join(base, "ScannedProj");
  const cwdPlain = join(base, "empty");
  mkdirSync(cwdPlain, { recursive: true });
  process.env.PI_CODING_AGENT_DIR = base;
  delete process.env.UNITY_PROJECT_PATH;
  const discoverImpl = () => [{ projectPath: scanned, pid: 7, bridgeReady: true }];
  const pi = mockPi();
  const children: unknown[] = [];
  try {
    createExtension(pi as never, { muxSpawnImpl: hangSpawn(children), discoverInstances: discoverImpl });
    const start = pi.handlers.get("session_start");
    const settingsCmd = pi.commands.get("unity-harness-settings");
    assert.ok(start && settingsCmd);

    // 会话启动触发 autoConnect；在它停掉旧 mux 的窗口内禁用
    const ctx = commandCtx(cwdPlain);
    void start({}, ctx);
    await new Promise((r) => setTimeout(r, 20));
    await settingsCmd.handler("off", ctx);
    await new Promise((r) => setTimeout(r, 700)); // 等 autoConnect 的 stop/shutdown 完成

    assert.equal(getActiveMux(), null);
    assert.equal(children.length, 1); // 迟到的自动连接不重建 mux
  } finally {
    const shutdown = pi.handlers.get("session_shutdown");
    if (shutdown) await shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnvProj === undefined) delete process.env.UNITY_PROJECT_PATH;
    else process.env.UNITY_PROJECT_PATH = prevEnvProj;
  }
});

function makeFakeBridge(projectDir: string, contents = "garbage-not-json{{{{") {
  mkdirSync(join(projectDir, "Library", "PiUnityHarness"), { recursive: true });
  writeFileSync(join(projectDir, "Library", "PiUnityHarness", "bridge.json"), contents, "utf8");
}

test("findNearestBridgeProject walks up and prefers nearest, existence only", () => {
  const base = mkdtempSync(join(tmpdir(), "pi-unity-nearest-"));
  const deepProj = join(base, "a", "b", "c");
  makeFakeBridge(deepProj);
  // 只探测存在性：内容可以是坏 JSON
  assert.equal(findNearestBridgeProject(join(deepProj, "d", "e")), resolve(deepProj));
  assert.equal(findNearestBridgeProject(join(base, "x", "y")), undefined);
});

test("resolveSessionProjectPath: explicit wins, nearest bridge beats env, env fallback", () => {
  const base = mkdtempSync(join(tmpdir(), "pi-unity-bind-pure-"));
  const nearest = join(base, "proj");
  makeFakeBridge(nearest);
  // 会话 cwd 在工程内部（向上能命中 bridge 目录）
  const cwd = join(nearest, "Assets", "Scripts");
  mkdirSync(cwd, { recursive: true });
  const envProj = join(base, "envproj");

  assert.equal(resolveSessionProjectPath(cwd, undefined, undefined), resolve(nearest));
  // 最近的本地 bridge 工程优先于继承的 UNITY_PROJECT_PATH
  assert.equal(resolveSessionProjectPath(cwd, undefined, envProj), resolve(nearest));
  // 没有本地 bridge（向上找不到）时回退 env
  assert.equal(resolveSessionProjectPath(base, undefined, envProj), resolve(envProj));
  assert.equal(resolveSessionProjectPath(base, undefined, "relative-env"), resolve(base, "relative-env"));
  // 显式选择优先于一切；相对路径按 cwd 解析
  assert.equal(resolveSessionProjectPath(cwd, "rel/p", envProj), resolve(cwd, "rel", "p"));
  assert.equal(resolveSessionProjectPath(cwd, resolve(envProj), undefined), resolve(envProj));
});

test("session binds nearest local bridge over env; unity_discover selection wins; relative resolved against session cwd", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnabled = process.env.PI_UNITY_HARNESS_ENABLED;
  const prevEnvProj = process.env.UNITY_PROJECT_PATH;
  const base = mkdtempSync(join(tmpdir(), "pi-unity-session-bind-"));
  // 会话 cwd 嵌在最近工程内部，向上才能命中 bridge 目录
  const nearestProj = join(base, "NearestProj");
  makeFakeBridge(nearestProj);
  const sessionCwd = join(nearestProj, "workspace", "nested", "session");
  mkdirSync(sessionCwd, { recursive: true });
  const envProj = join(base, "InheritedProj");

  process.env.PI_CODING_AGENT_DIR = base;
  delete process.env.PI_UNITY_HARNESS_ENABLED;
  process.env.UNITY_PROJECT_PATH = envProj;
  const pi = mockPi();
  try {
    createExtension(pi as never);
    const start = pi.handlers.get("session_start");
    assert.ok(start);
    await start({}, { cwd: sessionCwd });

    // 最近本地 bridge 工程（存在性）优先于继承的 env
    let mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(nearestProj));

    // 显式 unity_discover 选择优先于一切
    const disc = pi.tools.find((t) => t.name === "unity_discover");
    assert.ok(disc && typeof disc.execute === "function");
    await disc.execute!("id", { projectPath: resolve(envProj) } as never);
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(envProj));

    // 相对显式路径按会话 cwd 解析
    await disc.execute!("id", { projectPath: "rel/Target" } as never);
    mux = getActiveMux();
    assert.ok(mux);
    assert.equal(resolve(mux!.fixedProjectPath!), resolve(sessionCwd, "rel", "Target"));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnabled === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prevEnabled;
    if (prevEnvProj === undefined) delete process.env.UNITY_PROJECT_PATH;
    else process.env.UNITY_PROJECT_PATH = prevEnvProj;
  }
});
