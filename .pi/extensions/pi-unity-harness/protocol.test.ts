import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../protocol/fixtures/", import.meta.url));

type Fixture = {
  direction: string;
  wire: Record<string, unknown>;
  expect: Record<string, unknown>;
};

function load(dir: string): Array<{ name: string; value: Fixture }> {
  const path = join(root, dir);
  return readdirSync(path)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => ({
      name,
      value: JSON.parse(readFileSync(join(path, name), "utf8")) as Fixture,
    }));
}

function assertSurface(dir: string, direction: string): void {
  const fixtures = load(dir);
  assert.ok(fixtures.length > 0, `${dir} fixtures must not be empty`);
  assert.ok(fixtures.some(({ value }) => typeof (value as Fixture & { description?: unknown }).description === "string"), `${dir} fixtures must include descriptions`);
  for (const { name, value } of fixtures) {
    assert.equal(value.direction, direction, name);
    assert.equal(typeof value.wire, "object", name);
    assert.equal(typeof value.expect, "object", name);
    if (dir === "response" && name !== "missing_reply_to.json") {
      assert.equal(typeof value.wire.reply_to, "string", `${name} missing reply_to`);
      assert.equal("id" in value.wire, false, `${name} uses id on pipe response`);
    }
    if (dir === "mux" && name !== "err_missing_id.json") {
      assert.equal(typeof value.wire.id, "string", `${name} missing id`);
      assert.equal("reply_to" in value.wire, false, `${name} uses reply_to on mux line`);
    }
  }
}

test("shared fixtures keep pipe and mux correlation fields separate", () => {
  assertSurface("request", "request");
  assertSurface("response", "response");
  assertSurface("status", "status");
  assertSurface("mux", "mux");
});

test("required eval fixture remains available", () => {
  assert.equal(load("request").some(({ name }) => name === "eval_minimal.json"), true);
});

test("protocol_mismatch exposes expected and actual versions", () => {
  const fixture = load("response").find(({ name }) => name === "err_protocol_mismatch.json")!.value;
  assert.equal(fixture.wire.error_type, "protocol_mismatch");
  assert.deepEqual(fixture.wire.result, { expected: 1, actual: 2 });
});
