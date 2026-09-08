import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { test } from "node:test";
import { findPiUnityBinary, runPiUnityCli } from "./index.ts";

function hasPiUnityBin(): boolean {
  const bin = findPiUnityBinary();
  return existsSync(bin);
}

test("findPiUnityBinary finds an existing binary or fallback", () => {
  const bin = findPiUnityBinary();
  assert.ok(bin.length > 0);
  assert.match(bin, /pi-unity(\.exe)?$/i);
});

test("runPiUnityCli ping: usage/connect semantics without pretending Unity is up", async (t) => {
  if (!hasPiUnityBin()) {
    t.skip("无 pi-unity 二进制");
    return;
  }
  const res = await runPiUnityCli(["ping"]);
  if (res.ok) {
    assert.equal((res.result as { pong?: boolean } | undefined)?.pong, true);
    return;
  }
  assert.equal(res.exitCode, 1);
});

test("runPiUnityCli status uses shaped state, not managedState", async (t) => {
  if (!hasPiUnityBin()) {
    t.skip("无 pi-unity 二进制");
    return;
  }
  const res = await runPiUnityCli(["status"]);
  if (res.ok) {
    const result = res.result as { state?: string; editor?: string } | undefined;
    assert.ok(result?.state || result?.editor);
    return;
  }
  assert.equal(res.exitCode, 1);
});

test("runPiUnityCli eval offline is not success", async (t) => {
  if (!hasPiUnityBin()) {
    t.skip("无 pi-unity 二进制");
    return;
  }
  const res = await runPiUnityCli(["eval", "2 + 3"]);
  if (res.ok) {
    t.skip("需要真实 Unity Editor");
    return;
  }
  assert.equal(res.ok, false);
  assert.equal(res.exitCode, 1);
});

test("runPiUnityCli unknown command is usage 2", async (t) => {
  if (!hasPiUnityBin()) {
    t.skip("无 pi-unity 二进制");
    return;
  }
  const res = await runPiUnityCli(["nonexistent-command-xyz"]);
  assert.equal(res.ok, false);
  assert.equal(res.exitCode, 2);
});

test("runPiUnityCli missing project path is connect 1", async (t) => {
  if (!hasPiUnityBin()) {
    t.skip("无 pi-unity 二进制");
    return;
  }
  const res = await runPiUnityCli(["status"], { projectPath: "Z:/NonExistentPath_XYZ_123" });
  assert.equal(res.ok, false);
  assert.equal(res.exitCode, 1);
});
