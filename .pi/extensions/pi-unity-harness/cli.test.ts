import assert from "node:assert/strict";
import { test } from "node:test";
import { findPiUnityBinary, runPiUnityCli } from "./index.ts";

test("findPiUnityBinary finds an existing binary or fallback", () => {
  const bin = findPiUnityBinary();
  assert.ok(bin.length > 0);
  assert.match(bin, /pi-unity(\.exe)?$/i);
});

test("runPiUnityCli executes ping command asynchronously", async () => {
  const res = await runPiUnityCli(["ping"]);
  if (res.ok) {
    assert.equal(res.result?.pong, true);
  } else {
    assert.equal(res.exitCode, 2);
    assert.match(res.error ?? "", /bridge\.json|Unity/);
  }
});

test("runPiUnityCli executes status command asynchronously", async () => {
  const res = await runPiUnityCli(["status"]);
  if (res.ok) {
    assert.ok(res.result?.managedState);
    assert.ok(res.result?.pipe);
  } else {
    assert.equal(res.exitCode, 2);
  }
});

test("runPiUnityCli executes eval command when Unity is running", async () => {
  const res = await runPiUnityCli(["eval", "2 + 3"]);
  if (res.ok) {
    const val = typeof res.result === "object" && res.result !== null && "output" in res.result
      ? res.result.output
      : res.result;
    assert.equal(String(val).trim(), "5");
  } else {
    assert.equal(res.exitCode, 2);
  }
});

test("runPiUnityCli returns exitCode 1 on invalid command arguments", async () => {
  const res = await runPiUnityCli(["nonexistent-command-xyz"]);
  assert.equal(res.ok, false);
  assert.ok(res.exitCode === 1 || res.exitCode === 2);
});

test("runPiUnityCli returns exitCode 2 on nonexistent project path", async () => {
  const res = await runPiUnityCli(["status"], { projectPath: "Z:/NonExistentPath_XYZ_123" });
  assert.equal(res.ok, false);
  assert.equal(res.exitCode, 2);
  assert.match(res.error ?? "", /Specified project path does not exist|bridge\.json/);
});
