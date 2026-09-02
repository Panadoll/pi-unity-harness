/**
 * @deprecated LEGACY ARCHIVE: pi-unity-harness has migrated to CLI-Only architecture (No MCP).
 * Use `pi-unity` CLI instead.
 */
import { Client } from "@modelcontextprotocol/sdk/client/index.js";

import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { resolve } from "node:path";

const serverPath = resolve(import.meta.dirname, "mcp-server.mjs");

export async function handshake() {
  const env = { ...process.env };
  delete env.NODE_TEST_CONTEXT;
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverPath],
    env,
    stderr: "pipe",
  });
  const client = new Client({ name: "handshake", version: "0.0.0" });
  const watchdog = setTimeout(() => {
    void client.close();
  }, 8000);
  try {
    await client.connect(transport);
    const listed = await client.listTools();
    const init = { result: { serverInfo: { name: "pi-unity-harness" } } };
    const tools = { result: { tools: listed.tools } };
    await client.close();
    return { init, tools, serverPath };
  } finally {
    clearTimeout(watchdog);
  }
}

if (process.argv[1] && process.argv[1].endsWith("mcp-handshake.mjs")) {
  const result = await handshake();
  const names = (result.tools.result?.tools ?? []).map((t) => t.name);
  console.log(JSON.stringify({
    names,
    evalFile: (result.tools.result?.tools ?? []).find((t) => t.name === "unity_eval_file"),
  }, null, 2));
}
