import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { spawn } from "node:child_process";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import { MuxClient, pushViewArgs, runPiUnityCli, setActiveMux } from "./index.ts";

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
};

function makeFakeChild(opts?: { holdWriteCb?: boolean }): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.pid = 4242;
  child.exitCode = null;
  child.pendingWriteCbs = [];
  const origWrite = child.stdin.write.bind(child.stdin);
  child.stdin.write = ((chunk: unknown, encodingOrCb?: unknown, cb?: unknown) => {
    const callback =
      typeof encodingOrCb === "function"
        ? (encodingOrCb as (err?: Error | null) => void)
        : typeof cb === "function"
          ? (cb as (err?: Error | null) => void)
          : undefined;
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
  return child;
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
  const spawnImpl = (() => {
    throw new Error("spawn failed");
  }) as typeof spawn;
  const client = new MuxClient("Z:/definitely-missing-pi-unity.exe", undefined, spawnImpl);
  setActiveMux(client);
  try {
    const res = await runPiUnityCli(["nonexistent-command-xyz"]);
    assert.equal(res.ok, false);
    assert.ok(res.exitCode === 1 || res.exitCode === 2);
  } finally {
    setActiveMux(null);
    await client.shutdown();
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
