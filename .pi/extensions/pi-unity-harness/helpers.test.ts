import assert from "node:assert/strict";
import { test } from "node:test";
import {
  filterPipelineCommands,
  normalizePipelineToolName,
  parameterToTypeBox,
  pipelineCommandSummary,
  pipelineDynamicToolName,
  schemaToTypeBox,
  type TypeBoxLike,
} from "./helpers.ts";

const mockTypeBox: TypeBoxLike = {
  Unsafe: (schema) => schema,
  Object: (properties, options) => ({ type: "object", properties, ...(options?.required ? { required: options.required } : {}) }),
  Optional: (schema) => schema,
  Boolean: (options) => ({ type: "boolean", ...options }),
  Number: (options) => ({ type: "number", ...options }),
  Integer: (options) => ({ type: "integer", ...options }),
  String: (options) => ({ type: "string", ...options }),
};

test("normalizePipelineToolName normalizes mixed characters", () => {
  assert.equal(normalizePipelineToolName("get_game_object"), "unity_get_game_object");
  assert.equal(normalizePipelineToolName("assets.find-all"), "unity_assets_find_all");
});

test("schemaToTypeBox keeps an upstream required list", () => {
  const schema = {
    type: "object",
    properties: { file: { type: "string" } },
    required: ["file"],
  };
  const result = schemaToTypeBox(mockTypeBox, schema, [{ name: "file", required: true, type: "String" }]);
  assert.deepEqual(result.required, ["file"]);
});

test("schemaToTypeBox writes required when parameters say so and schema omits it", () => {
  const result = schemaToTypeBox(mockTypeBox, {
    type: "object",
    properties: { file: { type: "string" } },
  }, [{ name: "file", required: true, type: "String" }]);
  assert.deepEqual(result.required, ["file"]);
});

test("schemaToTypeBox puts required names on the object when schema is absent", () => {
  const result = schemaToTypeBox(mockTypeBox, null, [
    { name: "id", required: true, type: "String", description: "Target ID" },
    { name: "count", required: false, type: "Int32", description: "Count" },
  ]);
  assert.deepEqual(result.required, ["id"]);
  assert.equal(result.properties.id.type, "string");
  assert.equal(result.properties.count.type, "integer");
});

test("official eval_file does not take the Harness tool name", () => {
  assert.equal(pipelineDynamicToolName("eval_file"), "unity_pipeline_eval_file");
  assert.equal(pipelineDynamicToolName("run_script"), "unity_run_script");
  assert.equal(pipelineDynamicToolName("reload_file_override"), "unity_reload_file_override");
});

test("parameterToTypeBox prefers jsonType over misleading CLR names", () => {
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "flag", jsonType: "boolean", typeFullName: "System.String" }).type, "boolean");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "count", jsonType: "integer", typeFullName: "System.String" }).type, "integer");
});

test("parameterToTypeBox maps scalar types", () => {
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "flag", type: "Boolean" }).type, "boolean");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "num", type: "Int32" }).type, "integer");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "val", type: "Single" }).type, "number");
  assert.equal(parameterToTypeBox(mockTypeBox, { name: "txt", type: "String" }).type, "string");
});

test("filterPipelineCommands hides runtime and excluded commands", () => {
  const list = {
    pipelineAvailable: true,
    commands: [
      { name: "good_cmd", runtimeOnly: false },
      { name: "runtime_cmd", runtimeOnly: true },
      { name: "eval", runtimeOnly: false },
      { name: "danger", policy: { mutability: "destructive" } },
      null,
    ],
  };

  const filtered = filterPipelineCommands(list);
  assert.equal(filtered.length, 1);
  assert.equal(filtered[0]?.name, "good_cmd");
  assert.equal(filterPipelineCommands(list, new Set(), true).some((command) => command.name === "danger"), true);
});

test("pipelineCommandSummary shortcut follows the official name, not the override alias", () => {
  const official = pipelineCommandSummary({ name: "codereload_status", parameters: [] }, false);
  assert.equal(official.shortcut, true);
  assert.equal(official.shortcutTool, "unity_codereload_status");
  assert.equal(pipelineCommandSummary({ name: "reload_file_override", parameters: [] }).shortcut, false);
  assert.equal(pipelineCommandSummary({ name: "hotreload_status", parameters: [] }).shortcut, false);
});

