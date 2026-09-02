import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

import {
  coerceEnabled,
  describeSettingsPaths,
  inspectUnityHarnessSettings,
  loadUnityHarnessSettings,
  persistEnabled,
} from "./config.ts";

import {
  filterPipelineCommands,
  normalizePipelineToolName,
  schemaToTypeBox,
  type PipelineCommandList,
  type TypeBoxLike,
} from "./helpers.ts";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

export const Type: TypeBoxLike = {
  Object: (properties: Record<string, any>) => ({ type: "object", properties }),
  String: (options?: { description?: string }) => ({ type: "string", ...options }),
  Number: (options?: { description?: string }) => ({ type: "number", ...options }),
  Integer: (options?: { description?: string }) => ({ type: "integer", ...options }),
  Boolean: (options?: { description?: string }) => ({ type: "boolean", ...options }),
  Optional: (schema: any) => schema,
  Unsafe: (schema: any) => schema,
};

/** Injected into system prompt while harness tools are active. */
const UNITY_VERIFY_WORKFLOW_PROMPT = `
## Unity verify loop (required)
When changing Unity scripts, scenes, assets, or runtime behavior, close the loop — do not stop after edits alone:

1. **Observe**: call unity_snapshot (or a targeted unity_eval / unity_eval_file probe) before acting when context is unclear.
2. **Act**: apply the change (files, eval, pipeline commands).
3. **Compile**: after C# script edits, call unity_recompile and fix compile errors before claiming success.
4. **Verify**: confirm with unity_snapshot logs, unity_run_tests / unity_pipeline list_tests+run_tests, PlayMode (editor_play/stop), or vision/input probes as appropriate.
5. **Re-observe**: take a post-change unity_snapshot (or equivalent probe) and compare against the expected outcome.

Prefer unity_eval_file over unity_eval for multi-line or non-trivial C# (write a .repl under Temp/PiUnityHarness/AgentScratch/, then pass that path). Never call AssetDatabase.Refresh or other Domain Reload triggers inside eval — use unity_recompile instead.
`.trim();

/** Find pi-unity CLI binary path */
export function findPiUnityBinary(): string {
  if (process.env.PI_UNITY_BIN && existsSync(process.env.PI_UNITY_BIN)) {
    return resolve(process.env.PI_UNITY_BIN);
  }

  const workspaceRoot = resolve(__dirname, "../../../");
  const isWindows = process.platform === "win32";
  const binName = isWindows ? "pi-unity.exe" : "pi-unity";

  const candidates = [
    join(workspaceRoot, "bin", binName),
    join(workspaceRoot, "dist", binName),
    join(workspaceRoot, "native", "target", "release", binName),
    join(workspaceRoot, "native", "target", "debug", binName),
  ];

  for (const c of candidates) {
    if (existsSync(c)) {
      return resolve(c);
    }
  }

  return binName;
}

export interface CliExecutionResult {
  ok: boolean;
  result?: any;
  error?: string;
  exitCode?: number;
  truncated?: boolean;
  savedScratchPath?: string;
}

/** Execute pi-unity CLI asynchronously and return parsed JSON */
export async function runPiUnityCli(
  args: string[],
  options: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal } = {},
): Promise<CliExecutionResult> {
  const bin = findPiUnityBinary();
  const cliArgs = [...args, "--json"];

  if (options.projectPath) {
    cliArgs.push("--project-path", options.projectPath);
  }

  try {
    const { stdout } = await execFileAsync(bin, cliArgs, {
      encoding: "utf8",
      timeout: options.timeoutMs ?? 120000,
      maxBuffer: 32 * 1024 * 1024,
      windowsHide: true,
      signal: options.signal,
      env: {
        ...process.env,
        PI_UNITY_CLIENT: "pi-ext",
      },
    });


    const trimmed = stdout.trim();
    if (!trimmed) {
      return { ok: true, result: null };
    }
    return JSON.parse(trimmed);
  } catch (err: any) {
    if (err.stdout) {
      try {
        return JSON.parse(err.stdout.trim());
      } catch {}
    }
    const msg = err.stderr ? err.stderr.trim() : (err.message || String(err));
    return { ok: false, error: msg, exitCode: err.status ?? err.code ?? 1 };
  }
}

function formatResult(cliRes: CliExecutionResult): { content: Array<{ type: "text"; text: string }>; details: any } {
  if (!cliRes.ok) {
    throw new Error(cliRes.error || "pi-unity command failed");
  }

  const text = typeof cliRes.result === "string"
    ? cliRes.result
    : JSON.stringify(cliRes.result ?? {}, null, 2);

  return {
    content: [{ type: "text", text }],
    details: cliRes.result,
  };
}

/** Push `--flag <value>` when the value is present (empty string is skipped). */
function pushArg(args: string[], flag: string, value: string | number | undefined) {
  if (value === undefined || value === "") return;
  args.push(flag, String(value));
}

/** Tool parameter schema. Names in `required` stay required; the rest are optional. */
function toolSchema(fields: Record<string, any>, required: string[] = []): any {
  return Type.Object(
    Object.fromEntries(
      Object.entries(fields).map(([name, def]) => [
        name,
        required.includes(name) ? def : Type.Optional(def),
      ]),
    ),
  );
}

export default function (pi: ExtensionAPI) {
  let settings = loadUnityHarnessSettings();
  const dynamicallyRegisteredTools = new Set<string>();

  function assertEnabled() {
    settings = loadUnityHarnessSettings();
    if (!settings.enabled) {
      throw new Error(
        `pi-unity-harness is disabled in settings. Enable it with /unity-harness-settings enable or set PI_UNITY_HARNESS_ENABLED=1. Paths: ${describeSettingsPaths()}`,
      );
    }
  }

  async function runTool(
    args: string[],
    options?: { projectPath?: string; timeoutMs?: number; signal?: AbortSignal },
  ) {
    assertEnabled();
    return formatResult(await runPiUnityCli(args, options));
  }

  async function refreshDynamicPipelineTools() {
    try {
      const res = await runPiUnityCli(["list-commands"], { timeoutMs: 15000 });
      if (!res.ok || !res.result) return;
      const list = res.result as PipelineCommandList;
      const filtered = filterPipelineCommands(list);

      for (const cmd of filtered) {
        const toolName = normalizePipelineToolName(cmd.name);
        if (dynamicallyRegisteredTools.has(toolName)) continue;

        try {
          const schema = schemaToTypeBox(Type, cmd.schema, cmd.parameters);
          pi.registerTool({
            name: toolName,
            label: `Unity ${cmd.name}`,
            description: cmd.description || `Execute Unity Pipeline command: ${cmd.name}`,
            promptSnippet: `Use ${toolName} to execute the ${cmd.name} pipeline command.`,
            parameters: schema,
            async execute(_toolCallId, params) {
              return runTool(["pipeline", cmd.name, "--params-json", JSON.stringify(params ?? {})]);
            },
          });
          dynamicallyRegisteredTools.add(toolName);
        } catch {}
      }
    } catch {
      // Ignore if Unity is offline during startup
    }
  }

  pi.on("session_start", async (_event, ctx) => {
    settings = loadUnityHarnessSettings();
    if (settings.enabled) {
      ctx.systemPrompt += `\n\n${UNITY_VERIFY_WORKFLOW_PROMPT}`;
      void refreshDynamicPipelineTools();
    }
  });

  // ---- /unity-harness-settings command ----
  pi.registerCommand("unity-harness-settings", {
    description: "Inspect or toggle pi-unity-harness settings (enabled/disabled)",
    handler: (args, ctx) => {
      const enable = coerceEnabled(args.trim());
      if (enable === undefined) {
        ctx.ui.notify(inspectUnityHarnessSettings(), "info");
        return;
      }
      persistEnabled(enable);
      settings = loadUnityHarnessSettings();
      if (enable) void refreshDynamicPipelineTools();
      ctx.ui.notify(`pi-unity-harness ${enable ? "enabled" : "disabled"}.`, "info");
    },
  });

  // ---- unity_ping ----
  pi.registerTool({
    name: "unity_ping",
    label: "Unity Ping",
    description: "Ping the Unity native broker via pi-unity CLI to verify connection responsiveness.",
    promptSnippet: "Use unity_ping to probe whether the Unity Editor is running and responding.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 5000" }),
    }),
    async execute(_toolCallId, params) {
      return runTool(["ping", "--timeout", String(params.timeoutMs ?? 5000)]);
    },
  });

  // ---- unity_status ----
  pi.registerTool({
    name: "unity_status",
    label: "Unity Status",
    description: "Get Unity Editor and broker status, domain reload state, and modal window probe via pi-unity status.",
    promptSnippet: "Use unity_status to check Editor state, domain reload generation, and modal dialogs.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 5000" }),
    }),
    async execute(_toolCallId, params) {
      return runTool(["status", "--timeout", String(params.timeoutMs ?? 5000)]);
    },
  });

  // ---- unity_snapshot ----
  pi.registerTool({
    name: "unity_snapshot",
    label: "Unity Snapshot",
    description: "Get context snapshot of active scene hierarchy, selection, components, and console logs via pi-unity snapshot.",
    promptSnippet: "Use unity_snapshot to observe scene hierarchy, selection, and error logs before and after changes.",
    parameters: toolSchema({
      depth: Type.Integer({ description: "Max hierarchy depth to traverse (default: 3)" }),
      maxNodes: Type.Integer({ description: "Max GameObjects to include (default: 500)" }),
      logLimit: Type.Integer({ description: "Max recent logs to include (default: 50)" }),
      logLevel: Type.String({ description: "Log level filter: error, warning, or all (default: error)" }),
      noComponents: Type.Boolean({ description: "Omit component details for smaller output" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 20000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["snapshot"];
      pushArg(args, "--depth", params.depth);
      pushArg(args, "--max-nodes", params.maxNodes);
      pushArg(args, "--log-limit", params.logLimit);
      pushArg(args, "--log-level", params.logLevel);
      if (params.noComponents) args.push("--no-components");
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_eval ----
  pi.registerTool({
    name: "unity_eval",
    label: "Unity Eval",
    description: "Execute C# code or expression in the Unity Editor main thread via pi-unity eval.",
    promptSnippet: "Use unity_eval for short C# probes on the Unity main thread; prefer unity_eval_file for multi-line scripts.",
    parameters: toolSchema({
      code: Type.String({ description: "C# code or expression to execute in Unity Editor." }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
    }, ["code"]),
    async execute(_toolCallId, params) {
      const args = ["eval", params.code];
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_eval_file ----
  pi.registerTool({
    name: "unity_eval_file",
    label: "Unity Eval File",
    description: "Execute a C# script file or .repl file in the Unity Editor main thread via pi-unity eval -f.",
    promptSnippet: "Use unity_eval_file to run multi-line C# from a file (e.g. Temp/PiUnityHarness/AgentScratch/*.repl).",
    parameters: toolSchema({
      filePath: Type.String({ description: "Path to .cs or .repl file (relative to project root or absolute)." }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
    }, ["filePath"]),
    async execute(_toolCallId, params) {
      const args = ["eval", "-f", params.filePath];
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_recompile ----
  pi.registerTool({
    name: "unity_recompile",
    label: "Unity Recompile",
    description: "Trigger Unity script compilation and wait for domain reload to complete via pi-unity compile.",
    promptSnippet: "Use unity_recompile after C# edits to trigger compilation and await domain reload.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 120000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["compile"];
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args, { timeoutMs: (params.timeoutMs ?? 120000) + 10000 });
    },
  });

  // ---- unity_list_commands ----
  pi.registerTool({
    name: "unity_list_commands",
    label: "Unity List Commands",
    description: "List all registered Unity Pipeline [CliCommand] handlers via pi-unity list-commands.",
    promptSnippet: "Use unity_list_commands to discover available pipeline commands and parameter schemas.",
    parameters: toolSchema({
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 15000" }),
    }),
    async execute(_toolCallId, params) {
      assertEnabled();
      const args = ["list-commands"];
      pushArg(args, "--timeout", params.timeoutMs);
      void refreshDynamicPipelineTools();
      return runTool(args);
    },
  });

  // ---- unity_pipeline ----
  pi.registerTool({
    name: "unity_pipeline",
    label: "Unity Pipeline",
    description: "Execute a registered Unity Pipeline [CliCommand] via pi-unity pipeline. Omit command/name to list available commands.",
    promptSnippet: "Use unity_pipeline to run pipeline commands like uitree_*, assets_*, input_*, etc.",
    parameters: toolSchema({
      command: Type.String({ description: "Name of the pipeline command to execute (alias for name)." }),
      name: Type.String({ description: "Name of the pipeline command to execute." }),
      params: Type.Unsafe({
        type: "object",
        description: "Parameters object passed to the pipeline command (alias for parameters).",
        additionalProperties: true,
      }),
      parameters: Type.Unsafe({
        type: "object",
        description: "Parameters object passed to the pipeline command.",
        additionalProperties: true,
      }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
    }),
    async execute(_toolCallId, params) {
      const cmdName = (params.name || params.command || "").trim();
      if (!cmdName) return runTool(["list-commands"]);
      const args = ["pipeline", cmdName];
      const paramObj = params.parameters || params.params;
      if (paramObj) args.push("--params-json", JSON.stringify(paramObj));
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_run_tests ----
  pi.registerTool({
    name: "unity_run_tests",
    label: "Unity Run Tests",
    description: "Run Unity UTF test suite (EditMode or PlayMode) via pi-unity run-tests.",
    promptSnippet: "Use unity_run_tests to execute EditMode or PlayMode tests and verify code changes.",
    parameters: toolSchema({
      mode: Type.String({ description: "Test mode: edit or play (default: edit)" }),
      filter: Type.String({ description: "Optional test name or namespace filter pattern" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 330000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["run-tests"];
      pushArg(args, "--mode", params.mode);
      pushArg(args, "--filter", params.filter);
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args, { timeoutMs: (params.timeoutMs ?? 330000) + 10000 });
    },
  });

  // ---- unity_observe ----
  pi.registerTool({
    name: "unity_observe",
    label: "Unity Observe",
    description: "Multi-frame visual observation in PlayMode via pi-unity observe.",
    promptSnippet: "Use unity_observe for multi-frame perceptual verification and change detection in PlayMode.",
    parameters: toolSchema({
      frames: Type.Integer({ description: "Number of frames to capture (default: 3)" }),
      intervalMs: Type.Number({ description: "Interval between frames in ms (default: 160)" }),
      overlay: Type.String({ description: "Overlay mode: grid, annotations, both, or none (default: both)" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["observe"];
      pushArg(args, "--frames", params.frames);
      pushArg(args, "--interval", params.intervalMs);
      pushArg(args, "--overlay", params.overlay);
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_capture ----
  pi.registerTool({
    name: "unity_capture",
    label: "Unity Capture",
    description: "Capture a single GameView or SceneView screenshot via pi-unity capture.",
    promptSnippet: "Use unity_capture for fast single-frame viewport screenshot capture.",
    parameters: toolSchema({
      mode: Type.String({ description: "Viewport mode: game or scene (default: game)" }),
      outPath: Type.String({ description: "Output path for the saved image file" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 30000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["capture"];
      pushArg(args, "--mode", params.mode);
      pushArg(args, "--out", params.outPath);
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });

  // ---- unity_timeline ----
  pi.registerTool({
    name: "unity_timeline",
    label: "Unity Timeline",
    description: "Query recent operations timeline and audit history from the broker via pi-unity timeline.",
    promptSnippet: "Use unity_timeline to review recent command audit history and execution times.",
    parameters: toolSchema({
      limit: Type.Integer({ description: "Number of timeline entries to return (default: 20)" }),
      success: Type.String({ description: "Filter status: all, success, or failure (default: all)" }),
      timeoutMs: Type.Number({ description: "Timeout in milliseconds, default 15000" }),
    }),
    async execute(_toolCallId, params) {
      const args = ["timeline"];
      pushArg(args, "--limit", params.limit);
      pushArg(args, "--success", params.success);
      pushArg(args, "--timeout", params.timeoutMs);
      return runTool(args);
    },
  });
}
