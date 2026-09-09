import assert from "node:assert/strict";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import createExtension, { getActiveMux } from "./index.ts";

function mockPi() {
  const tools: Array<{ name: string; parameters: { properties?: Record<string, unknown> } }> = [];
  const commands = new Map<string, { handler: (args: string, ctx: unknown) => unknown }>();
  const handlers = new Map<string, (...args: unknown[]) => unknown>();
  return {
    tools,
    commands,
    handlers,
    registerTool(def: { name: string; parameters: { properties?: Record<string, unknown> } }) {
      tools.push(def);
    },
    registerCommand(name: string, def: { handler: (args: string, ctx: unknown) => unknown }) {
      commands.set(name, def);
    },
    on(event: string, handler: (...args: unknown[]) => unknown) {
      handlers.set(event, handler);
    },
  };
}

function commandCtx(cwd: string) {
  const notes: string[] = [];
  return {
    cwd,
    ui: {
      notify(message: unknown) {
        notes.push(typeof message === "string" ? message : JSON.stringify(message));
      },
    },
    notes,
  };
}

test("extension factory shuts mux on disable and recreates on enable", async () => {
  const prevDir = process.env.PI_CODING_AGENT_DIR;
  const prevEnabled = process.env.PI_UNITY_HARNESS_ENABLED;
  const cwd = mkdtempSync(join(tmpdir(), "pi-unity-ext-"));
  process.env.PI_CODING_AGENT_DIR = cwd;
  delete process.env.PI_UNITY_HARNESS_ENABLED;
  const pi = mockPi();
  try {
    createExtension(pi as never);
    const start = pi.handlers.get("session_start");
    assert.ok(start);
    await start({}, { cwd });
    assert.ok(getActiveMux());

    const snapshot = pi.tools.find((t) => t.name === "unity_snapshot");
    assert.ok(snapshot?.parameters.properties?.fields);
    assert.ok(snapshot?.parameters.properties?.full);
    assert.equal(snapshot?.parameters.properties?.noComponents, undefined);

    const settings = pi.commands.get("unity-harness-settings");
    assert.ok(settings);
    const ctx = commandCtx(cwd);
    await settings.handler("disable", ctx);
    assert.equal(getActiveMux(), null);
    await settings.handler("enable", ctx);
    assert.ok(getActiveMux());
  } finally {
    const mux = getActiveMux();
    if (mux) await mux.shutdown();
    if (prevDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = prevDir;
    if (prevEnabled === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prevEnabled;
  }
});
