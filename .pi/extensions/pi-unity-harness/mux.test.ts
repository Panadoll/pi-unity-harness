import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { test } from "node:test";
import { MuxClient, runPiUnityCli, setActiveMux } from "./index.ts";

function fakeMuxScript(): string {
  return [
    "const readline = require('node:readline');",
    "let n = 0;",
    "const rl = readline.createInterface({ input: process.stdin });",
    "rl.on('line', (line) => {",
    "  const msg = JSON.parse(line);",
    "  if (msg.quit) { process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { quit: true } }) + '\\n'); process.exit(0); }",
    "  n += 1;",
    "  process.stdout.write(JSON.stringify({ id: msg.id, ok: true, exitCode: 0, result: { n, argv: msg.argv } }) + '\\n');",
    "});",
  ].join("");
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
    assert.match(res.error ?? "", /mux/);
  } finally {
    setActiveMux(null);
    await client.shutdown();
  }
});
