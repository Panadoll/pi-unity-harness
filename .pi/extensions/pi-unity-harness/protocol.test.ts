import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { MuxClient } from "./mux/client.ts";
import { parseCliJson, parseMuxReply, type CliExecutionResult } from "./wire/cli-envelope.ts";

const root = fileURLToPath(new URL("../../../protocol/fixtures/", import.meta.url));

type Fixture = {
  direction: string;
  wire: Record<string, unknown>;
  expect: {
    ok: boolean | null;
    errorType: string | null;
    message: string | null;
    exitCode: number;
  };
};

function load(dir: string): Array<{ name: string; value: Fixture }> {
  return readdirSync(join(root, dir))
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => ({
      name,
      value: JSON.parse(readFileSync(join(root, dir, name), "utf8")) as Fixture,
    }));
}

type FakeChild = EventEmitter & {
  stdin: PassThrough;
  stdout: PassThrough;
  stderr: PassThrough;
  exitCode: number | null;
  kill: () => boolean;
  frames: Array<{ id?: string }>;
};

function fakeChild(): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.exitCode = null;
  child.frames = [];
  child.kill = () => true;
  child.stdin.on("data", (chunk: Buffer | string) => {
    const text = Buffer.isBuffer(chunk) ? chunk.toString("utf8") : String(chunk);
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      child.frames.push(JSON.parse(line) as { id?: string });
      child.emit("frame");
    }
  });
  queueMicrotask(() => child.emit("spawn"));
  return child;
}

function waitForFrame(child: FakeChild): Promise<{ id?: string }> {
  const { promise, resolve } = Promise.withResolvers<{ id?: string }>();
  const finish = () => {
    if (child.frames.length > 0) resolve(child.frames[0]);
  };
  child.on("frame", finish);
  finish();
  return promise;
}

function assertMuxParsed(name: string, observed: CliExecutionResult, wire: Record<string, unknown>): void {
  assert.equal(observed.ok, wire.ok === true, name);
  assert.equal(observed.error_type, typeof wire.error_type === "string" ? wire.error_type : undefined, name);
  assert.equal(observed.error, typeof wire.error === "string" ? wire.error : undefined, name);
  assert.equal(observed.exitCode, typeof wire.exitCode === "number" ? wire.exitCode : wire.ok === true ? 0 : 1, name);
  if ("result" in wire) assert.deepEqual(observed.result, wire.result, name);
  else assert.equal(observed.result, undefined, name);
  assert.equal(observed.truncated, wire.truncated === true, name);
  assert.equal(observed.savedScratchPath, typeof wire.savedScratchPath === "string" ? wire.savedScratchPath : undefined, name);
  assert.equal(observed.text, typeof wire.text === "string" ? wire.text : undefined, name);
  if (Array.isArray(wire.help)) assert.deepEqual(observed.help, wire.help, name);
  else assert.equal(observed.help, undefined, name);
}

test("mux fixtures complete the in-flight request only when ids match", async () => {
  for (const { name, value } of load("mux")) {
    const child = fakeChild();
    const client = new MuxClient("protocol-fixture", undefined, (() => child) as never);
    const pending = client.request(["status"]);
    const sent = await waitForFrame(child);
    assert.equal(typeof sent.id, "string", name);

    if (typeof value.wire.id !== "string") {
      child.stdout.write(`${JSON.stringify(value.wire)}\n`);
      const lost = await pending;

      assert.equal(lost.status, "in-flight-lost", name);
      assert.equal(lost.result.ok, false, name);
      assert.equal(parseMuxReply(value.wire), null, name);
      assert.equal(parseMuxReply({ ...value.wire, reply_to: sent.id }), null, name);
      continue;
    }

    child.stdout.write(`${JSON.stringify({ ...value.wire, id: `${sent.id}-other` })}\n`);
    child.stdout.write(`${JSON.stringify({ ...value.wire, id: sent.id })}\n`);
    const completed = await pending;
    assert.equal(completed.status, "completed", name);
    assertMuxParsed(name, completed.result, value.wire);
  }
});

test("exec JSON parser passes typed fields through and does not classify bare broker codes", () => {
  for (const { name, value } of load("response")) {
    const parsed = parseCliJson(JSON.stringify(value.wire));
    const wireOk = value.wire.ok !== false;
    assert.equal(parsed.ok, wireOk, name);
    assert.equal(parsed.error_type, typeof value.wire.error_type === "string" ? value.wire.error_type : undefined, name);
    assert.equal(parsed.error, typeof value.wire.error === "string" ? value.wire.error : undefined, name);
    assert.equal(parsed.exitCode, typeof value.wire.exitCode === "number" ? value.wire.exitCode : wireOk ? 0 : 1, name);
    if ("result" in value.wire) assert.deepEqual(parsed.result, value.wire.result, name);
    else assert.equal(parsed.result, undefined, name);
  }

  const typed = parseCliJson(JSON.stringify({
    ok: false,
    error: "managed_not_ready",
    error_type: "compile_error",
  }));
  assert.equal(typed.ok, false);
  assert.equal(typed.error_type, "compile_error");
  assert.equal(typed.error, "managed_not_ready");
  assert.equal(typed.exitCode, 1);
});
