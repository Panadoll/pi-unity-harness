import assert from "node:assert/strict";
import { EventEmitter, getEventListeners } from "node:events";
import { spawn } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import createExtension, { getActiveMux, MuxClient, pushViewArgs, runPiUnityCli, sanitizeDiagnosticTail, setActiveMux } from "./index.ts";

function fakeMuxScript(extraFields = ""): string {
  return [
    "const readline = require('node:readline');",
    "let n = 0;",
    "const rl = readline.createInterface({ input: process.stdin });",
    "rl.on('line', (line) => {",
    "  const msg = JSON.parse(line);",
    "  if (msg.quit) { process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { quit: true } }) + '\\n'); process.exit(0); }",
    "  n += 1;",
    `  process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { n, argv: msg.argv }${extraFields} }) + '\\n');`,
    "});",
  ].join("");
}

type FakeChild = EventEmitter & {
  stdin: PassThrough;
  stdout: PassThrough;
  stderr: PassThrough;
  pid: number;
  exitCode: number | null;
  kill: () => boolean;
  pendingWriteCbs: Array<(err?: Error | null) => void>;
  frames: Array<{ id: string; argv?: string[]; quit?: boolean }>;
};

function makeFakeChild(opts?: { holdWriteCb?: boolean }): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.pid = 4242;
  child.exitCode = null;
  child.pendingWriteCbs = [];
  child.frames = [];
  const origWrite = child.stdin.write.bind(child.stdin);
  child.stdin.write = ((chunk: unknown, encodingOrCb?: unknown, cb?: unknown) => {
    const callback =
      typeof encodingOrCb === "function"
        ? (encodingOrCb as (err?: Error | null) => void)
        : typeof cb === "function"
          ? (cb as (err?: Error | null) => void)
          : undefined;
    const text = Buffer.isBuffer(chunk) ? chunk.toString("utf8") : String(chunk);
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      try {
        child.frames.push(JSON.parse(line) as { id: string; argv?: string[]; quit?: boolean });
        child.emit("frame");
      } catch {}
    }
    origWrite(chunk as string | Buffer);
    if (callback) {
      if (opts?.holdWriteCb) child.pendingWriteCbs.push(callback);
      else queueMicrotask(() => callback(null));
    }
    return true;
  }) as typeof child.stdin.write;
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

function waitForFrames(child: FakeChild, count: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const onFrame = () => {
      if (child.frames.length >= count) {
        cleanup();
        resolve();
      }
    };
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`waitForFrames timed out waiting for ${count}, have ${child.frames.length}`));
    }, 2000);
    const cleanup = () => {
      clearTimeout(timer);
      child.off("frame", onFrame);
    };
    if (child.frames.length >= count) {
      cleanup();
      resolve();
      return;
    }
    child.on("frame", onFrame);
  });
}

function reply(child: FakeChild, index: number, extra: Record<string, unknown>): void {
  child.stdout.write(`${JSON.stringify({ id: child.frames[index].id, ...extra })}\n`);
}

test("MuxClient reuses one process for two requests", async () => {
  let spawnCount = 0;
  const spawnImpl = ((bin: string, argv: readonly string[] | undefined, opts: unknown) => {
    spawnCount += 1;
    assert.equal(bin, process.execPath);
    assert.ok(Array.isArray(argv));
    return spawn(process.execPath, ["-e", fakeMuxScript()], opts as Parameters<typeof spawn>[2]);
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const a = await client.request(["status"]);
    const b = await client.request(["ping"]);
    assert.equal(a.written, true);
    assert.equal(b.written, true);
    assert.equal(a.result.ok, true);
    assert.equal(b.result.ok, true);
    assert.deepEqual(a.result.result, { n: 1, argv: ["status"] });
    assert.deepEqual(b.result.result, { n: 2, argv: ["ping"] });
    assert.equal(spawnCount, 1);
  } finally {
    await client.shutdown();
  }
});

test("runPiUnityCli falls back to execFile when mux never writes", async () => {
  const dir = mkdtempSync(join(tmpdir(), "pi-unity-fallback-"));
  const dummy = join(dir, "pi-unity.exe"); // 存在但不可执行：execFile 必然失败，且不碰仓库里的真实 CLI
  writeFileSync(dummy, "not an executable");
  const prevBin = process.env.PI_UNITY_BIN;
  process.env.PI_UNITY_BIN = dummy;
  const spawnImpl = (() => {
    throw new Error("spawn failed");
  }) as typeof spawn;
  const client = new MuxClient(dummy, undefined, spawnImpl);
  setActiveMux(client);
  try {
    const res = await runPiUnityCli(["nonexistent-command-xyz"]);
    assert.equal(res.ok, false);
    assert.ok(res.exitCode === 1 || res.exitCode === 2);
    // 回退失败信息固定：不透传 err.message（含完整命令行与原始输出）
    assert.match(res.error ?? "", /^pi-unity CLI 启动失败 \(exitCode=[0-9]+\)$/);
    assert.equal(res.error_type, "cli_failed");
    assert.equal(res.error?.includes("nonexistent-command-xyz"), false);
    assert.equal(res.error?.includes("pi-unity.exe"), false);
  } finally {
    setActiveMux(null);
    await client.shutdown();
    if (prevBin === undefined) delete process.env.PI_UNITY_BIN;
    else process.env.PI_UNITY_BIN = prevBin;
  }
});

test("mux protocol damage fails without exec retry after write", async () => {
  const spawnImpl = ((_bin: string, _argv: readonly string[] | undefined, opts: unknown) => {
    return spawn(
      process.execPath,
      [
        "-e",
        "const readline=require('node:readline'); const rl=readline.createInterface({input:process.stdin}); rl.on('line',()=>{process.stdout.write('not-json\\n');});",
      ],
      opts as Parameters<typeof spawn>[2],
    );
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  setActiveMux(client);
  try {
    const res = await runPiUnityCli(["status"]);
    assert.equal(res.ok, false);
    assert.match(res.error ?? "", /mux|协议损坏/);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});

test("pre-abort does not spawn or write", async () => {
  let spawned = 0;
  const spawnImpl = (() => {
    spawned += 1;
    return makeFakeChild();
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  const signal = AbortSignal.abort();
  try {
    const res = await client.request(["status"], { signal });
    assert.equal(res.written, false);
    assert.equal(res.result.error, "aborted");
    assert.equal(spawned, 0);
  } finally {
    await client.shutdown();
  }
});

test("post-abort destroys mux and pending", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  const ac = new AbortController();
  try {
    const pending = client.request(["eval", "1"], { signal: ac.signal, timeoutMs: 5000 });
    await new Promise((r) => setTimeout(r, 20));
    assert.equal(children.length, 1);
    ac.abort();
    const res = await pending;
    assert.equal(res.result.error, "aborted");
    assert.equal(children[0].exitCode, 1);
  } finally {
    await client.shutdown();
  }
});

test("old child exit does not kill restarted mux", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    child.stdin.on("data", (buf: Buffer) => {
      const line = buf.toString("utf8").trim();
      if (!line) return;
      const msg = JSON.parse(line) as { id: string; quit?: boolean };
      if (msg.quit) return;
      child.stdout.write(JSON.stringify({
        id: msg.id,
        ok: true,
        exitCode: 0,
        result: { gen: children.length },
      }) + "\n");
    });
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const first = await client.request(["status"], { timeoutMs: 2000 });
    assert.equal(first.result.ok, true);
    const old = children[0];
    old.kill();
    const second = await client.request(["status"], { timeoutMs: 2000 });
    assert.equal(second.result.ok, true);
    assert.equal(children.length, 2);
    old.emit("error", new Error("stale"));
    old.emit("exit", 1);
    const third = await client.request(["ping"], { timeoutMs: 2000 });
    assert.equal(third.result.ok, true);
    assert.equal(children.length, 2);
  } finally {
    await client.shutdown();
  }
});

test("runPiUnityCli rejects mismatched projectPath without exec", async () => {
  let spawned = 0;
  const spawnImpl = (() => {
    spawned += 1;
    throw new Error("should not spawn");
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, "D:/proj-a", spawnImpl);
  setActiveMux(client);
  try {
    const res = await runPiUnityCli(["status"], { projectPath: "D:/proj-b" });
    assert.equal(res.ok, false);
    assert.equal(res.exitCode, 2);
    assert.equal(res.error_type, "usage");
    assert.match(res.error ?? "", /mux 已绑定工程/);
    assert.equal(spawned, 0);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});

test("runPiUnityCli rejects projectPath when mux project is unknown", async () => {
  const spawnImpl = (() => {
    throw new Error("should not spawn");
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  setActiveMux(client);
  try {
    const res = await runPiUnityCli(["status"], { projectPath: "D:/proj-b" });
    assert.equal(res.ok, false);
    assert.equal(res.exitCode, 2);
    assert.equal(res.error_type, "usage");
    assert.match(res.error ?? "", /mux 工程未固定/);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});

test("pushViewArgs writes fields/full and never no-components", () => {
  const args: string[] = ["snapshot"];
  pushViewArgs(args, { fields: "components,tag", full: true });
  assert.deepEqual(args, ["snapshot", "--fields", "components,tag", "--full"]);
  assert.equal(args.includes("--no-components"), false);
});

function serialMuxScript(): string {
  return [
    "const readline = require('node:readline');",
    "const rl = readline.createInterface({ input: process.stdin });",
    "let busy = 0;",
    "let maxBusy = 0;",
    "const queued = [];",
    "function handle(line) {",
    "  const msg = JSON.parse(line);",
    "  if (msg.quit) { process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { quit: true } }) + '\\n'); process.exit(0); }",
    "  busy += 1;",
    "  maxBusy = Math.max(maxBusy, busy);",
    "  const delay = (msg.argv || []).includes('slow') ? 200 : 0;",
    "  setTimeout(() => {",
    "    process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { argv: msg.argv, maxBusy } }) + '\\n');",
    "    busy -= 1;",
    "    const next = queued.shift();",
    "    if (next) handle(next);",
    "  }, delay);",
    "}",
    "rl.on('line', (line) => { if (busy > 0) queued.push(line); else handle(line); });",
  ].join("");
}

test("FIFO keeps one in-flight so a short follow-up survives a slow request", async () => {
  const spawnImpl = ((_bin: string, _argv: readonly string[] | undefined, opts: unknown) => {
    return spawn(process.execPath, ["-e", serialMuxScript()], opts as Parameters<typeof spawn>[2]);
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  setActiveMux(client);
  try {
    const [slow, fast] = await Promise.all([
      runPiUnityCli(["slow"], { timeoutMs: 5000 }),
      runPiUnityCli(["ping"], { timeoutMs: 80 }),
    ]);
    assert.equal(slow.ok, true);
    assert.equal(fast.ok, true);
    const slowRes = slow.result as { maxBusy?: number };
    const fastRes = fast.result as { maxBusy?: number };
    assert.equal(slowRes.maxBusy, 1);
    assert.equal(fastRes.maxBusy, 1);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});

test("queued abort does not kill in-flight request", async () => {
  const spawnImpl = ((_bin: string, _argv: readonly string[] | undefined, opts: unknown) => {
    return spawn(process.execPath, ["-e", serialMuxScript()], opts as Parameters<typeof spawn>[2]);
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  const ac = new AbortController();
  try {
    const slow = client.request(["slow"], { timeoutMs: 2000 });
    const queued = client.request(["ping"], { signal: ac.signal, timeoutMs: 2000 });
    await new Promise((r) => setTimeout(r, 20));
    ac.abort();
    const queuedRes = await queued;
    const slowRes = await slow;
    assert.equal(queuedRes.status, "aborted");
    assert.equal(queuedRes.written, false);
    assert.equal(slowRes.result.ok, true);
  } finally {
    await client.shutdown();
  }
});

test("in-flight timeout drops queued jobs without exec replay", async () => {
  let execTried = 0;
  const spawnImpl = ((_bin: string, _argv: readonly string[] | undefined, opts: unknown) => {
    execTried += 1;
    return spawn(process.execPath, ["-e", serialMuxScript()], opts as Parameters<typeof spawn>[2]);
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  setActiveMux(client);
  try {
    const slow = runPiUnityCli(["slow"], { timeoutMs: 50 });
    const queued = runPiUnityCli(["ping"], { timeoutMs: 2000 });
    const [slowRes, queuedRes] = await Promise.all([slow, queued]);
    assert.equal(slowRes.ok, false);
    assert.equal(queuedRes.ok, false);
    assert.match(queuedRes.error ?? "", /未发送/);
    assert.equal(execTried, 1);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});

test("stale write callback does not kill restarted mux", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const isFirst = children.length === 0;
    const child = makeFakeChild({ holdWriteCb: isFirst });
    children.push(child);
    if (!isFirst) {
      child.stdin.on("data", (buf: Buffer) => {
        const line = buf.toString("utf8").trim();
        if (!line) return;
        const msg = JSON.parse(line) as { id: string; quit?: boolean };
        if (msg.quit) return;
        child.stdout.write(JSON.stringify({
          id: msg.id,
          ok: true,
          exitCode: 0,
          result: { gen: children.length },
        }) + "\n");
      });
    }
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const first = client.request(["status"], { timeoutMs: 20 });
    const firstRes = await first;
    assert.equal(firstRes.result.ok, false);
    assert.equal(children.length, 1);
    const second = await client.request(["status"], { timeoutMs: 2000 });
    assert.equal(second.result.ok, true);
    assert.equal(children.length, 2);
    for (const cb of children[0].pendingWriteCbs.splice(0)) cb(new Error("stale write"));
    const third = await client.request(["ping"], { timeoutMs: 2000 });
    assert.equal(third.result.ok, true);
    assert.equal(children.length, 2);
  } finally {
    await client.shutdown();
  }
});

test("missing exe is unavailable-before-start and only head may fallback", async () => {
  const client = new MuxClient("Z:/definitely-missing-pi-unity.exe");
  try {
    const [a, b] = await Promise.all([
      client.request(["status"]),
      client.request(["ping"]),
    ]);
    const statuses = [a.status, b.status].sort();
    assert.deepEqual(statuses, ["queue-dropped", "unavailable-before-start"]);
    const head = a.status === "unavailable-before-start" ? a : b;
    const rest = a.status === "unavailable-before-start" ? b : a;
    assert.equal(head.retryAllowed, true);
    assert.equal(head.written, false);
    assert.equal(rest.retryAllowed, false);
    assert.equal(rest.written, false);
  } finally {
    await client.shutdown();
  }
});

test("manual fake mux is FIFO, abort/timeout drop queue, broker error keeps session", { timeout: 10000 }, async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  const ac = new AbortController();
  try {
    const firstP = client.request(["one"], { timeoutMs: 2000 });
    const secondP = client.request(["two"], { timeoutMs: 2000 });
    const thirdP = client.request(["three"], { signal: ac.signal, timeoutMs: 2000 });
    await waitForFrames(children[0], 1);
    assert.equal(children[0].frames.length, 1);
    assert.deepEqual(children[0].frames[0].argv, ["one"]);

    reply(children[0], 0, {
      ok: false,
      exitCode: 1,
      error: "broker boom",
      error_type: "execution_failed",
    });
    const first = await firstP;
    assert.equal(first.status, "completed");
    assert.equal(first.retryAllowed, false);
    assert.equal(first.result.ok, false);

    await waitForFrames(children[0], 2);
    assert.equal(children[0].frames.length, 2);
    assert.deepEqual(children[0].frames[1].argv, ["two"]);

    const abortListenersBefore = getEventListeners(ac.signal, "abort").length;
    ac.abort();
    const third = await thirdP;
    assert.equal(third.status, "aborted");
    assert.equal(third.written, false);
    assert.equal(third.retryAllowed, false);
    assert.equal(children[0].frames.length, 2);
    assert.equal(getEventListeners(ac.signal, "abort").length, abortListenersBefore - 1);

    reply(children[0], 1, { ok: true, exitCode: 0, result: { n: 2 } });
    const second = await secondP;
    assert.equal(second.status, "completed");
    assert.equal(second.result.ok, true);
    assert.equal(children.length, 1);

    const inflightAbort = new AbortController();
    const inFlight = client.request(["hold"], { signal: inflightAbort.signal, timeoutMs: 2000 });
    const queued = client.request(["later"], { timeoutMs: 2000 });
    await waitForFrames(children[0], 3);
    assert.equal(children[0].frames.length, 3);
    inflightAbort.abort();
    const lost = await inFlight;
    const dropped = await queued;
    assert.equal(lost.status, "in-flight-lost");
    assert.equal(lost.retryAllowed, false);
    assert.equal(dropped.status, "queue-dropped");
    assert.equal(dropped.retryAllowed, false);
    assert.equal(dropped.written, false);

    const afterP = client.request(["again"], { timeoutMs: 2000 });
    await waitForFrames(children[1], 1);
    reply(children[1], 0, { ok: true, exitCode: 0, result: { n: 3 } });
    const after = await afterP;
    assert.equal(after.status, "completed");
    assert.equal(after.retryAllowed, false);
    assert.equal(after.result.ok, true);
    assert.equal(children.length, 2);

    const hold = client.request(["shutdown-hold"], { timeoutMs: 2000 });
    const queuedShut = client.request(["shutdown-queued"], { timeoutMs: 2000 });
    await waitForFrames(children[1], 2);
    const spawnBeforeShutdown = children.length;
    const [holdRes, queuedRes] = await Promise.all([hold, queuedShut, client.shutdown()]);
    assert.equal(holdRes.retryAllowed, false);
    assert.equal(queuedRes.status, "queue-dropped");
    assert.equal(queuedRes.retryAllowed, false);
    assert.equal(children.length, spawnBeforeShutdown);
  } finally {
    await client.shutdown();
  }
});

test("mux text is returned as-is", async () => {
  const spawnImpl = ((_bin: string, _argv: readonly string[] | undefined, opts: unknown) => {
    return spawn(
      process.execPath,
      ["-e", fakeMuxScript(', "text": "pong: true"')],
      opts as Parameters<typeof spawn>[2],
    );
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const res = await client.request(["ping"]);
    assert.equal(res.result.ok, true);
    assert.equal(res.result.text, "pong: true");
  } finally {
    await client.shutdown();
  }
});

// ---- 崩溃重启语义（PR: mux 意外退出）----

function tick(ms = 10): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

test("crash: queued safe jobs replay once FIFO, in-flight uncertain never replayed", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const inFlight = client.request(["dangerous-write"], { timeoutMs: 2000 });
    await waitForFrames(children[0], 1); // 已写入 → 不确定副作用
    const queuedB = client.request(["safe-b"], { timeoutMs: 2000 });
    const queuedC = client.request(["safe-c"], { timeoutMs: 2000 });
    children[0].emit("exit", 1, null);

    const lost = await inFlight;
    assert.equal(lost.status, "in-flight-lost");
    assert.equal(lost.retryAllowed, false);
    assert.equal(lost.result.error_type, "mux_crash");
    assert.match(lost.result.error ?? "", /exitCode=1/);
    assert.ok((lost.result.help ?? []).some((h) => h.includes("mux_crash")));
    assert.ok((lost.result.help ?? []).some((h) => h.includes("结果尚未确定")));
    assert.equal((lost.result.help ?? []).some((h) => h.includes("已自动重启")), false);

    // 重启一次，只重发未写入的排队请求，FIFO 顺序保持（同一时刻只有一个 in-flight）
    await waitForFrames(children[1], 1);
    assert.deepEqual(children[1].frames.map((f) => f.argv), [["safe-b"]]);
    reply(children[1], 0, { ok: true, exitCode: 0, result: { n: 1 } });
    await waitForFrames(children[1], 2);
    assert.deepEqual(children[1].frames.map((f) => f.argv), [["safe-b"], ["safe-c"]]);
    reply(children[1], 1, { ok: true, exitCode: 0, result: { n: 2 } });
    const b = await queuedB;
    const c = await queuedC;
    assert.equal(b.status, "completed");
    assert.equal(b.result.ok, true);
    assert.equal(c.status, "completed");
    assert.equal(c.result.ok, true);
    assert.equal(children.length, 2); // 只重启一次
  } finally {
    await client.shutdown();
  }
});

test("crash: restart cap — second consecutive crash drops queued, next call recovers", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const a = client.request(["one"], { timeoutMs: 2000 });
    await waitForFrames(children[0], 1);
    const b = client.request(["two"], { timeoutMs: 2000 });
    children[0].emit("exit", 1, null); // 崩溃 1 → 重启一次，重发排队 b
    await waitForFrames(children[1], 1);
    assert.equal(children.length, 2);
    children[1].emit("exit", 3, "SIGABRT"); // 崩溃 2 → 已达上限：不再重启
    const [resA, resB] = await Promise.all([a, b]);
    assert.equal(resA.status, "in-flight-lost");
    assert.equal(resB.status, "in-flight-lost"); // b 已写入新 mux（不确定）→ 绝不重放
    assert.equal(resB.retryAllowed, false);
    assert.match(resB.result.error ?? "", /signal=SIGABRT/);
    assert.ok((resB.result.help ?? []).some((h) => h.includes("未重启")));
    assert.equal((resB.result.help ?? []).some((h) => h.includes("已自动重启")), false);
    assert.equal(children.length, 2); // 重启上限：不拉第三个

    // 后续业务调用必须恢复
    const rec = client.request(["again"], { timeoutMs: 2000 });
    await waitForFrames(children[2], 1);
    reply(children[2], 0, { ok: true, exitCode: 0, result: { n: 9 } });
    const recRes = await rec;
    assert.equal(recRes.status, "completed");
    assert.equal(recRes.result.ok, true);
    assert.equal(children.length, 3);
  } finally {
    await client.shutdown();
  }
});

test("crash with empty queue does not restart; next business call recovers", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const first = client.request(["status"], { timeoutMs: 2000 });
    await waitForFrames(children[0], 1);
    reply(children[0], 0, { ok: true, exitCode: 0, result: { n: 1 } });
    assert.equal((await first).result.ok, true);
    children[0].emit("exit", 1, null); // 队列空：不重启不重发
    await tick();
    assert.equal(children.length, 1);

    const second = client.request(["ping"], { timeoutMs: 2000 });
    await waitForFrames(children[1], 1);
    assert.equal(children.length, 2);
    reply(children[1], 0, { ok: true, exitCode: 0, result: { n: 2 } });
    assert.equal((await second).result.ok, true);
  } finally {
    await client.shutdown();
  }
});

test("crash diagnostics redact secrets from bounded stderr tail", async () => {
  const children: FakeChild[] = [];
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, undefined, spawnImpl);
  try {
    const req = client.request(["status"], { timeoutMs: 2000 });
    await waitForFrames(children[0], 1);
    const secret = "SUPERSECRET_TOKEN_VALUE_1234567890";
    children[0].stderr.write(`${"x".repeat(500)} token=${secret} uh-oh\n`);
    await tick();
    children[0].emit("exit", 1, null);
    const res = await req;
    assert.equal(res.status, "in-flight-lost");
    const all = JSON.stringify(res.result);
    assert.equal(all.includes(secret), false, "秘密不得进入诊断输出");
    assert.match(res.result.error ?? "", /exitCode=1/);
    const helpText = (res.result.help ?? []).join("\n");
    assert.ok(helpText.length < 1200, `诊断尾部必须有界: ${helpText.length}`);
    assert.ok(helpText.includes("已脱敏"));
    assert.ok(helpText.includes("未重启"));
    assert.equal(helpText.includes("已自动重启"), false);
  } finally {
    await client.shutdown();
  }
});

test("sanitizeDiagnosticTail: allowlist summary never echoes raw lines or secrets", () => {
  // 未标签 / 分片段密钥、私钥、cookie、路径、argv 行（fake 字符串，仅测试用）：
  // 任何原文片段都不得进入汇总。
  const secret = "SUPERSECRET_PRIVATE_VALUE_1234567890";
  const fixtures = [
    `token=${secret}\npassword: hunter2secret\nBearer abcDEF0123XYZ`,
    `-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAAAyB\n-----END PRIVATE KEY-----`,
    `session_cookie=${secret}; Path=/; HttpOnly`,
    `argv --project-path D:/proj/critical\n${secret.slice(0, 17)}\n${secret.slice(17)}`,
    secret.split("").join("\n"),
  ];
  for (const text of fixtures) {
    const out = sanitizeDiagnosticTail(text);
    for (const frag of ["SUPERSECRET", "hunter2secret", "abcDEF0123", "BEGIN PRIVATE KEY", "MIIEvQIB", "session_cookie", "D:/proj/critical"]) {
      assert.equal(out.includes(frag), false, `泄漏片段 ${frag} 出现在 ${out}`);
    }
    assert.match(out, /^stderr=\d+ bytes\/\d+ lines/);
  }
  assert.equal(sanitizeDiagnosticTail(undefined), "");
  assert.equal(sanitizeDiagnosticTail(""), "");
});

test("sanitizeDiagnosticTail: keeps known panic categories and bounds length", () => {
  const out = sanitizeDiagnosticTail(
    "thread 'main' panicked at src/main.rs:42:\nindex out of bounds\nthe application crashed unexpectedly\nsignal: SIGSEGV\n",
  );
  assert.match(out, /matched: .*panic/);
  assert.match(out, /matched: .*segfault/); // SIGSEGV 归入 segfault 类别标签
  assert.match(out, /stderr=\d+ bytes\/\d+ lines/);
  assert.equal(out.includes("src/main.rs"), false, "路径不进入汇总"); // 类别标签可留，行原文不留
  // 有界：大段噪音输出只留下统计，内容一律丢弃
  const big = "payload-" + "y".repeat(20000);
  const out2 = sanitizeDiagnosticTail(big);
  assert.ok(out2.length < 800);
  assert.equal(out2.includes("payload-"), false);
  assert.match(out2, /stderr=\d+ bytes\/\d+ lines/);
});

test("exec fallback uses mux fixed projectPath and cwd", async () => {
  const dir = mkdtempSync(join(tmpdir(), "pi-unity-exec-"));
  const workDir = join(dir, "work");
  mkdirSync(workDir, { recursive: true });
  const injectFile = join(dir, "inject.cjs");
  const injectFwd = injectFile.replace(/\\/g, "/");
  writeFileSync(
    injectFile,
    "console.log(JSON.stringify({ ok: true, exitCode: 0, result: { argv: process.argv.slice(1), cwd: process.cwd() } }));\nprocess.exit(0);\n",
  );
  const prevBin = process.env.PI_UNITY_BIN;
  const prevNodeOptions = process.env.NODE_OPTIONS;
  const spawnImpl = (() => {
    throw new Error("spawn failed");
  }) as typeof spawn;
  const client = new MuxClient(process.execPath, "D:/proj-fixed", spawnImpl, workDir);
  setActiveMux(client);
  process.env.PI_UNITY_BIN = process.execPath;
  process.env.NODE_OPTIONS = `--require "${injectFwd}"`;
  try {
    const res = await runPiUnityCli(["ping"]);
    assert.equal(res.ok, true, JSON.stringify(res));
    const detail = res.result as { argv?: string[]; cwd?: string };
    assert.ok(Array.isArray(detail.argv), JSON.stringify(detail));
    const ppIndex = detail.argv!.indexOf("--project-path");
    assert.ok(ppIndex >= 0 && ppIndex + 1 < detail.argv!.length);
    // 回退走 mux 固定的 projectPath 与 cwd，避免绑定漂移
    assert.equal(detail.argv![ppIndex + 1], "D:/proj-fixed");
    assert.equal(resolve(detail.cwd ?? ""), resolve(workDir));
  } finally {
    setActiveMux(null);
    await client.shutdown();
    if (prevBin === undefined) delete process.env.PI_UNITY_BIN;
    else process.env.PI_UNITY_BIN = prevBin;
    if (prevNodeOptions === undefined) delete process.env.NODE_OPTIONS;
    else process.env.NODE_OPTIONS = prevNodeOptions;
  }
});

// ---- 动态 pipeline 工具注册（fake mux，不依赖真实 Unity）----

function mockPiExt() {
  const tools: any[] = [];
  const commands = new Map<string, { handler: (args: string, ctx: unknown) => unknown }>();
  const handlers = new Map<string, (...args: unknown[]) => unknown>();
  return {
    tools,
    commands,
    handlers,
    registerTool(def: any) {
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

function fakeCommandList(names: string[]): unknown {
  return { pipelineAvailable: true, commands: names.map((n) => ({ name: n, description: `desc: ${n}` })) };
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

async function startSession(pi: any, cwd: string): Promise<void> {
  const start = pi.handlers.get("session_start");
  assert.ok(start);
  await start({}, { cwd });
}

function fakeMuxSetup(children: FakeChild[], dynamicToolRefreshBackoffMs?: number) {
  const spawnImpl = (() => {
    const child = makeFakeChild();
    children.push(child);
    return child;
  }) as typeof spawn;
  const base = mkdtempSync(join(tmpdir(), "pi-unity-refresh-"));
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnabled = process.env.PI_UNITY_HARNESS_ENABLED;
  process.env.PI_CODING_AGENT_DIR = base;
  process.env.PI_UNITY_HARNESS_ENABLED = "1";
  // discoverInstances 注入空扫描：autoConnect 不干扰 fake mux 的帧序列
  const pi = mockPiExt();
  createExtension(pi as never, { muxSpawnImpl: spawnImpl, discoverInstances: () => [], dynamicToolRefreshBackoffMs });
  return {
    pi,
    base,
    cleanup: () => {
      if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
      else process.env.PI_CODING_AGENT_DIR = prevDir;
      if (prevEnabled === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
      else process.env.PI_UNITY_HARNESS_ENABLED = prevEnabled;
    },
  };
}

test("dynamic refresh: offline startup then business success registers once, no extra list", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children);
  try {
    await startSession(env.pi, env.base);

    // 启动期发现失败（离线）
    await waitForFrames(children[0], 1);
    assert.deepEqual(children[0].frames[0].argv, ["list-commands", "--full"]);
    reply(children[0], 0, { ok: false, exitCode: 1, error: "broker offline" });

    // 业务成功（unity_ping）→ 首成功后立即补试注册（<3s 不受限速）
    const ping = env.pi.tools.find((t) => t.name === "unity_ping");
    assert.ok(ping);
    const pingP = ping.execute("id", {});
    await waitForFrames(children[0], 2); // ping 帧
    reply(children[0], 1, { ok: true, exitCode: 0, result: { n: 1, argv: ["ping"] } });
    const pingRes = await pingP;
    assert.equal(pingRes.details.n, 1);
    await waitForFrames(children[0], 3); // 补试 list-commands
    assert.deepEqual(children[0].frames[2].argv, ["list-commands", "--full"]);
    reply(children[0], 2, {
      ok: true,
      exitCode: 0,
      result: fakeCommandList(["uitree_snapshot", "input_drag", "input_drag_start"]),
    });
    await tick();
    const names = new Set(env.pi.tools.map((t) => t.name));
    assert.ok(names.has("unity_uitree_snapshot"), [...names].join(","));
    assert.ok(names.has("unity_input_drag"));
    assert.ok(names.has("unity_input_drag_start"));
    assert.equal(children[0].frames.filter((f) => f.argv?.[0] === "list-commands").length, 2);

    // 成功注册后：再次业务成功不再 list-commands
    const ping2P = ping.execute("id", {});
    await waitForFrames(children[0], 4);
    reply(children[0], 3, { ok: true, exitCode: 0, result: { n: 2 } });
    const pingRes2 = await ping2P;
    assert.equal(pingRes2.details.n, 2);
    await tick();
    assert.equal(children[0].frames.filter((f) => f.argv?.[0] === "list-commands").length, 2);
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});

test("dynamic refresh: simultaneous business successes dedup into one discovery", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children);
  try {
    await startSession(env.pi, env.base);
    await waitForFrames(children[0], 1);
    reply(children[0], 0, { ok: false, exitCode: 1, error: "offline" }); // 失败 → 下个业务成功补试

    const ping = env.pi.tools.find((t) => t.name === "unity_ping");
    assert.ok(ping);
    const pingPs = [ping.execute("id", {}), ping.execute("id", {})];
    await waitForFrames(children[0], 2); // pingA 帧（pingB 排队）
    reply(children[0], 1, { ok: true, exitCode: 0, result: { n: 1 } }); // 触发 pump → pingB 发出
    await waitForFrames(children[0], 3); // pingB 帧
    reply(children[0], 2, { ok: true, exitCode: 0, result: { n: 2 } });
    const [r1, r2] = await Promise.all(pingPs);
    assert.equal(r1.details.n, 1);
    assert.equal(r2.details.n, 2);
    await waitForFrames(children[0], 4); // 2 ping + 1 补试（同代 in-flight 去重，只补一次）
    assert.equal(children[0].frames.filter((f) => f.argv?.[0] === "list-commands").length, 2);
    reply(children[0], 3, { ok: true, exitCode: 0, result: fakeCommandList(["uitree_snapshot"]) });
    await tick();
    assert.ok(env.pi.tools.some((t) => t.name === "unity_uitree_snapshot"));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});

test("dynamic refresh: disable drops stale discovery, re-enable re-registers", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children);
  try {
    await startSession(env.pi, env.base);
    await waitForFrames(children[0], 1); // 启动期 list-commands 挂起（不回复）

    const settingsCmd = env.pi.commands.get("unity-harness-settings");
    assert.ok(settingsCmd);
    const ctx = commandCtx(env.base);
    await settingsCmd.handler("off", ctx); // 禁用：in-flight 发现随 stop 作废
    assert.equal(getActiveMux(), null);
    // 迟到的“成功”发现结果不会注册进已禁用会话
    reply(children[0], 0, { ok: true, exitCode: 0, result: fakeCommandList(["uitree_snapshot"]) });
    await tick();
    assert.equal(env.pi.tools.some((t) => t.name === "unity_uitree_snapshot"), false);

    await settingsCmd.handler("on", ctx); // 重新启用：重新发现并注册
    assert.ok(getActiveMux());
    await waitForFrames(children[1], 1);
    assert.deepEqual(children[1].frames[0].argv, ["list-commands", "--full"]);
    reply(children[1], 0, { ok: true, exitCode: 0, result: fakeCommandList(["uitree_snapshot"]) });
    await tick();
    assert.ok(env.pi.tools.some((t) => t.name === "unity_uitree_snapshot"));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});

test("dynamic refresh: drag guidance only for exact input_drag, split commands direct to input_drag", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children);
  try {
    await startSession(env.pi, env.base);
    await waitForFrames(children[0], 1);
    reply(children[0], 0, {
      ok: true,
      exitCode: 0,
      result: fakeCommandList(["input_drag", "input_drag_start", "input_drag_move", "uitree_snapshot"]),
    });
    await tick();
    const drag = env.pi.tools.find((t) => t.name === "unity_input_drag");
    const dragStart = env.pi.tools.find((t) => t.name === "unity_input_drag_start");
    const dragMove = env.pi.tools.find((t) => t.name === "unity_input_drag_move");
    assert.ok(drag && dragStart && dragMove);
    // 只有精确的 input_drag 拿到“拖拽优先”推荐
    assert.match(drag.promptSnippet, /一次调用内部插值/);
    // 分步命令改为指向 input_drag，自己不再被推荐
    assert.match(dragStart.promptSnippet, /unity_pipeline input_drag/);
    assert.match(dragMove.promptSnippet, /unity_pipeline input_drag/);
    assert.equal(dragStart.promptSnippet.includes("一次调用内部插值"), false);
    assert.equal(dragStart.promptSnippet.includes("拖拽优先 unity_input_drag_start"), false);
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});

test("dynamic refresh: immediate recovery is consumed once, later failures use backoff", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children, 30);
  try {
    await startSession(env.pi, env.base);
    await waitForFrames(children[0], 1);
    reply(children[0], 0, { ok: false, exitCode: 1, error: "startup offline" });

    const ping = env.pi.tools.find((t) => t.name === "unity_ping");
    const first = ping.execute("id", {});
    await waitForFrames(children[0], 2);
    reply(children[0], 1, { ok: true, exitCode: 0, result: { n: 1 } });
    await first;
    await waitForFrames(children[0], 3); // 首个成功业务立即补试
    reply(children[0], 2, { ok: false, exitCode: 1, error: "still offline" });

    const second = ping.execute("id", {});
    await waitForFrames(children[0], 4);
    reply(children[0], 3, { ok: true, exitCode: 0, result: { n: 2 } });
    await second;
    await tick();
    assert.equal(children[0].frames.length, 4, "补试失败后不得再次绕过退避");

    await tick(35);
    const third = ping.execute("id", {});
    await waitForFrames(children[0], 5);
    reply(children[0], 4, { ok: true, exitCode: 0, result: { n: 3 } });
    await third;
    await waitForFrames(children[0], 6);
    assert.deepEqual(children[0].frames[5].argv, ["list-commands", "--full"]);
    reply(children[0], 5, { ok: true, exitCode: 0, result: fakeCommandList(["uitree_snapshot"]) });
    await tick();
    assert.ok(env.pi.tools.some((t) => t.name === "unity_uitree_snapshot"));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});

test("dynamic refresh: project switch ignores delayed old discovery", async () => {
  const children: FakeChild[] = [];
  const env = fakeMuxSetup(children);
  try {
    await startSession(env.pi, env.base);
    await waitForFrames(children[0], 1);
    const discoverTool = env.pi.tools.find((t) => t.name === "unity_discover");
    assert.ok(discoverTool);
    const switchP = discoverTool.execute("id", { projectPath: join(env.base, "other") });
    await tick();
    reply(children[0], 0, { ok: true, exitCode: 0, result: fakeCommandList(["old_only"]) });
    await switchP;
    await waitForFrames(children[1], 1);
    reply(children[1], 0, { ok: true, exitCode: 0, result: fakeCommandList(["new_only"]) });
    await tick();
    assert.equal(env.pi.tools.some((t) => t.name === "unity_old_only"), false);
    assert.ok(env.pi.tools.some((t) => t.name === "unity_new_only"));
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    env.cleanup();
  }
});
