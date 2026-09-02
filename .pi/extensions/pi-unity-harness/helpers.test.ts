import assert from "node:assert/strict";
import { test } from "node:test";
import {
  filterPipelineCommands,
  normalizePipelineToolName,
  parameterToTypeBox,
  pipelineCommandSummary,
  schemaToTypeBox,
  type TypeBoxLike,
} from "./helpers.ts";

const mockTypeBox: TypeBoxLike = {
  Unsafe: (schema) => ({ kind: "unsafe", schema }),
  Object: (properties) => ({ kind: "object", properties }),
  Optional: (schema) => ({ kind: "optional", schema }),
  Boolean: (options) => ({ kind: "boolean", ...options }),
  Number: (options) => ({ kind: "number", ...options }),
  Integer: (options) => ({ kind: "integer", ...options }),
  String: (options) => ({ kind: "string", ...options }),
};

test("normalizePipelineToolName normalizes mixed characters", () => {
  assert.equal(normalizePipelineToolName("get_game_object"), "unity_get_game_object");
  assert.equal(normalizePipelineToolName("assets.find-all"), "unity_assets_find_all");
});

test("schemaToTypeBox uses unsafe path for valid object schema", () => {
  const schema = {
    type: "object",
    properties: {
      id: { type: "string" },
    },
  };
  const result = schemaToTypeBox(mockTypeBox, schema, undefined);
  assert.deepEqual(result, {
    kind: "unsafe",
    schema,
  });
});

test("schemaToTypeBox builds object from parameters when schema is absent", () => {
  const result = schemaToTypeBox(mockTypeBox, null, [
    { name: "id", required: true, type: "String", description: "Target ID" },
    { name: "count", required: false, type: "Int32", description: "Count" },
  ]);

  assert.deepEqual(result, {
    kind: "object",
    properties: {
      id: { kind: "string", description: "Target ID" },
      count: { kind: "optional", schema: { kind: "integer", description: "Count" } },
    },
  });
});

test("parameterToTypeBox maps scalar types", () => {
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "flag", type: "Boolean" }).kind, "boolean");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "num", type: "Int32" }).kind, "integer");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "val", type: "Single" }).kind, "number");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "txt", type: "String" }).kind, "string");
});

test("filterPipelineCommands hides runtime and excluded commands", () => {
  const list = {
    pipelineAvailable: true,
    commands: [
      { name: "good_cmd", runtimeOnly: false },
      { name: "runtime_cmd", runtimeOnly: true },
      { name: "eval", runtimeOnly: false },
      null,
    ],
  };

  const filtered = filterPipelineCommands(list);
  assert.equal(filtered.length, 1);
  assert.equal(filtered[0]?.name, "good_cmd");
});

test("pipelineCommandSummary exposes shortcut info", () => {
  const cmd = {
    name: "list_tests",
    description: "List tests",
    parameters: [],
  };

  const summary = pipelineCommandSummary(cmd, false);
  assert.equal(summary.name, "list_tests");
  assert.equal(summary.shortcut, true);
  assert.equal(summary.shortcutTool, "unity_list_tests");
});
