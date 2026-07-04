import assert from "node:assert/strict";
import { test } from "node:test";
import {
  filterPipelineCommands,
  normalizePipelineCommandParams,
  normalizePipelineRequestedCommand,
  normalizePipelineToolName,
  parameterToTypeBox,
  parseEditorStatus,
  parseUnityMajorVersion,
  pipelineCommandTimeoutMs,
  pipelineCommandSummary,
  schemaToTypeBox,
  shouldRefreshPipelineCommands,
} from "./helpers.ts";

const FakeType = {
  Unsafe: (schema: any) => ({ kind: "unsafe", schema }),
  Object: (properties: Record<string, any>) => ({ kind: "object", properties }),
  Optional: (schema: any) => ({ kind: "optional", schema }),
  Boolean: (options?: { description?: string }) => ({ kind: "boolean", options }),
  Number: (options?: { description?: string }) => ({ kind: "number", options }),
  Integer: (options?: { description?: string }) => ({ kind: "integer", options }),
  String: (options?: { description?: string }) => ({ kind: "string", options }),
};

test("parseEditorStatus parses plain status", () => {
  assert.deepEqual(parseEditorStatus("editing"), { editorStatus: "editing", focusState: undefined, windowState: undefined });
});

test("parseEditorStatus parses focus and window suffixes", () => {
  assert.deepEqual(parseEditorStatus("editing;focus=focused;window=normal"), {
    editorStatus: "editing",
    focusState: "focused",
    windowState: "normal",
  });
});

test("parseEditorStatus handles empty and non-string input", () => {
  assert.deepEqual(parseEditorStatus(""), { editorStatus: "unknown", focusState: undefined, windowState: undefined });
  assert.deepEqual(parseEditorStatus(undefined), { editorStatus: "unknown", focusState: undefined, windowState: undefined });
  assert.deepEqual(parseEditorStatus(123), { editorStatus: "123", focusState: undefined, windowState: undefined });
});

test("parseUnityMajorVersion extracts major version", () => {
  assert.equal(parseUnityMajorVersion("6000.5.0f1"), 6000);
  assert.equal(parseUnityMajorVersion("2021.3.44f1"), 2021);
});

test("parseUnityMajorVersion returns undefined for missing or garbage input", () => {
  assert.equal(parseUnityMajorVersion(undefined), undefined);
  assert.equal(parseUnityMajorVersion("garbage"), undefined);
});

test("normalizePipelineToolName normalizes mixed characters", () => {
  assert.equal(normalizePipelineToolName("Run Tests.Now-Please"), "unity_run_tests_now_please");
});

test("normalizePipelineToolName preserves already clean names", () => {
  assert.equal(normalizePipelineToolName("already_clean"), "unity_already_clean");
});

test("pipelineCommandTimeoutMs uses run_tests default and timeout seconds", () => {
  assert.equal(pipelineCommandTimeoutMs("run_tests", {}), 330000);
  assert.equal(pipelineCommandTimeoutMs("run_tests", { timeout: 12 }), 42000);
});

test("pipelineCommandTimeoutMs honors explicit timeoutMs and timeout_ms for non-run_tests", () => {
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeoutMs: 2000 }), 7000);
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeout_ms: 2500 }), 7500);
});

test("pipelineCommandTimeoutMs handles non-run_tests timeout seconds and command defaults", () => {
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeout: 20 }), 25000);
  assert.equal(pipelineCommandTimeoutMs("list_tests", {}), 60000);
  assert.equal(pipelineCommandTimeoutMs("reload_file", {}), 120000);
  assert.equal(pipelineCommandTimeoutMs("reload_file_meta", {}), 120000);
  assert.equal(pipelineCommandTimeoutMs("unknown", {}), 30000);
});

test("pipelineCommandTimeoutMs falls back on invalid values", () => {
  assert.equal(pipelineCommandTimeoutMs("run_tests", { timeout: -1 }), 330000);
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeoutMs: Number.NaN }), 30000);
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeout: -5 }), 30000);
  assert.equal(pipelineCommandTimeoutMs("build_player", { timeout: "5" }), 30000);
});

test("schemaToTypeBox uses unsafe path for valid object schema", () => {
  const schema = { type: "object", properties: { enabled: { type: "boolean" } } };
  assert.deepEqual(schemaToTypeBox(FakeType, schema, undefined), { kind: "unsafe", schema });
});

test("schemaToTypeBox builds object from parameters when schema is absent", () => {
  assert.deepEqual(schemaToTypeBox(FakeType, undefined, [
    { name: "enabled", type: "Boolean", description: "Enable feature", required: true },
    { name: "count", type: "Int32", description: "Item count", required: true },
    { name: "ratio", typeFullName: "System.Single", description: "Ratio", required: false },
    { name: "label", type: "String", description: "Label", required: false },
  ]), {
    kind: "object",
    properties: {
      enabled: { kind: "boolean", options: { description: "Enable feature" } },
      count: { kind: "integer", options: { description: "Item count" } },
      ratio: { kind: "optional", schema: { kind: "number", options: { description: "Ratio" } } },
      label: { kind: "optional", schema: { kind: "string", options: { description: "Label" } } },
    },
  });
});

test("schemaToTypeBox returns empty object when schema and params are absent", () => {
  assert.deepEqual(schemaToTypeBox(FakeType, undefined, undefined), { kind: "object", properties: {} });
});

test("parameterToTypeBox maps scalar types", () => {
  assert.deepEqual(parameterToTypeBox(FakeType, { name: "flag", type: "Boolean", description: "Flag" }), {
    kind: "boolean",
    options: { description: "Flag" },
  });
  assert.deepEqual(parameterToTypeBox(FakeType, { name: "count", typeFullName: "System.Int64", description: "Count" }), {
    kind: "integer",
    options: { description: "Count" },
  });
  assert.deepEqual(parameterToTypeBox(FakeType, { name: "ratio", type: "float", description: "Ratio" }), {
    kind: "number",
    options: { description: "Ratio" },
  });
  assert.deepEqual(parameterToTypeBox(FakeType, { name: "text", type: "String", description: "Text" }), {
    kind: "string",
    options: { description: "Text" },
  });
});

test("filterPipelineCommands hides runtime and excluded commands", () => {
  assert.deepEqual(filterPipelineCommands({
    pipelineAvailable: true,
    commands: [
      { name: "run_tests" },
      { name: "eval" },
      { name: "play", runtimeOnly: true },
      null,
      undefined,
    ],
  }).map((command) => command.name), ["run_tests"]);
});

test("normalizePipelineRequestedCommand trims strings and ignores non strings", () => {
  assert.equal(normalizePipelineRequestedCommand(" run_tests "), "run_tests");
  assert.equal(normalizePipelineRequestedCommand(123), "");
  assert.equal(normalizePipelineRequestedCommand(undefined), "");
});

test("shouldRefreshPipelineCommands refreshes only for requested missing commands or unavailable pipeline", () => {
  const commands = [{ name: "run_tests" }];
  assert.equal(shouldRefreshPipelineCommands({ pipelineAvailable: true }, "", commands), false);
  assert.equal(shouldRefreshPipelineCommands({ pipelineAvailable: true }, "run_tests", commands), false);
  assert.equal(shouldRefreshPipelineCommands({ pipelineAvailable: true }, "list_tests", commands), true);
  assert.equal(shouldRefreshPipelineCommands({ pipelineAvailable: false }, "run_tests", commands), true);
});

test("pipelineCommandSummary exposes shortcut tool only for shortcut commands and schema only when requested", () => {
  const command = {
    name: "run_tests",
    description: "Run tests",
    mainThreadRequired: true,
    schema: { type: "object" },
    parameters: [{ name: "mode", type: "String" }],
  };
  assert.deepEqual(pipelineCommandSummary(command), {
    name: "run_tests",
    shortcut: true,
    shortcutTool: "unity_run_tests",
    description: "Run tests",
    mainThreadRequired: true,
    parameters: [{ name: "mode", type: "String" }],
  });
  assert.deepEqual(pipelineCommandSummary({ name: "editor_focus" }, true), {
    name: "editor_focus",
    shortcut: false,
    shortcutTool: null,
    description: undefined,
    mainThreadRequired: undefined,
    parameters: [],
    schema: null,
  });
});

test("normalizePipelineCommandParams accepts objects and rejects non objects", () => {
  const input = { mode: "editor" };
  assert.equal(normalizePipelineCommandParams(input), input);
  assert.deepEqual(normalizePipelineCommandParams(undefined), {});
  assert.throws(() => normalizePipelineCommandParams([]), /params must be a JSON object, got array/);
  assert.throws(() => normalizePipelineCommandParams(null), /params must be a JSON object, got null/);
  assert.throws(() => normalizePipelineCommandParams("bad"), /params must be a JSON object, got string/);
});
