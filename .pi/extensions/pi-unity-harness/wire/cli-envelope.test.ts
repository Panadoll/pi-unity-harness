import assert from "node:assert/strict";
import { test } from "node:test";
import { parseCliJson, parseMuxReply } from "./cli-envelope.ts";

test("exec envelope ignores unknown fields and keeps null result", () => {
  const parsed = parseCliJson(JSON.stringify({
    ok: true,
    result: null,
    reply_to: "pipe-1",
    extra: { nested: true },
  }));
  assert.equal(parsed.ok, true);
  assert.equal(parsed.result, null);
  assert.equal(parsed.exitCode, 0);
  assert.equal("reply_to" in parsed, false);
  assert.equal("extra" in parsed, false);
});

test("exec envelope maps empty, invalid, and array JSON without classifying them", () => {
  assert.deepEqual(parseCliJson("   "), { ok: true, result: null });
  assert.deepEqual(parseCliJson("{"), { ok: false, error: "{", exitCode: 1 });
  assert.deepEqual(parseCliJson("[1,2]"), { ok: false, error: "[1,2]", exitCode: 1 });
});

test("exec envelope prefers exitCode and drops non-string help", () => {
  const parsed = parseCliJson(JSON.stringify({
    ok: false,
    exitCode: 7,
    exit_code: 3,
    help: ["keep", 2, null, "also"],
    result: null,
  }));
  assert.equal(parsed.exitCode, 7);
  assert.deepEqual(parsed.help, ["keep", "also"]);
  assert.equal(parsed.result, null);
  assert.equal(parsed.ok, false);
});

test("missing ok and exit_code stay direction-specific", () => {
  const wire = { error: "down", exit_code: 9, result: null };
  const exec = parseCliJson(JSON.stringify(wire));
  const mux = parseMuxReply({ id: "m1", ...wire });
  assert.equal(exec.ok, true);
  assert.equal(exec.exitCode, 9);
  assert.equal(mux?.ok, false);
  assert.equal(mux?.exitCode, 1);
  assert.equal(parseMuxReply(wire), null);
  assert.equal(parseMuxReply({ reply_to: "m1", ...wire }), null);
});
