#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

import os from "node:os";

// Helper: Resolve Unity project path
function resolveUnityProject() {
  if (process.env.UNITY_PROJECT_PATH && fs.existsSync(process.env.UNITY_PROJECT_PATH)) {
    return path.resolve(process.env.UNITY_PROJECT_PATH);
  }

  // Check CWD
  const cwd = process.cwd();
  if (fs.existsSync(path.join(cwd, "Library", "PiUnityHarness", "bridge.json"))) {
    return cwd;
  }

  // Check parent directories
  let curr = cwd;
  for (let i = 0; i < 5; i++) {
    if (fs.existsSync(path.join(curr, "Library", "PiUnityHarness", "bridge.json"))) {
      return curr;
    }
    const parent = path.dirname(curr);
    if (parent === curr) break;
    curr = parent;
  }

  // Hardcoded default fallback
  if (fs.existsSync("F:/UnityProjects/GP1/Library/PiUnityHarness/bridge.json")) {
    return "F:/UnityProjects/GP1";
  }

  // Scan running Unity process
  if (process.platform === "win32") {
    try {
      const output = execFileSync(
        "powershell.exe",
        [
          "-NoProfile",
          "-NonInteractive",
          "-Command",
          `Get-CimInstance Win32_Process -Filter "name='Unity.exe'" | ForEach-Object { if ($_.CommandLine) { $_.CommandLine } }`,
        ],
        { encoding: "utf8", timeout: 5000, stdio: ["ignore", "pipe", "ignore"] }
      );
      for (const line of output.split("\n")) {
        const match = line.match(/-projectPath[\s"]+"?([^"\s]+)/i) || line.match(/-createproject[\s"]+"?([^"\s]+)/i);
        if (match && fs.existsSync(path.join(match[1], "Library", "PiUnityHarness", "bridge.json"))) {
          return path.resolve(match[1]);
        }
      }
    } catch {
      // Ignore process scan error
    }
  }

  return cwd;
}

const MAX_SAFE_RESPONSE_CHARS = 32768; // 32KB max response size per tool call
const MIN_BASE64_DETECT_LENGTH = 256;

function isBase64Data(str) {
  if (typeof str !== "string") return false;
  if (str.startsWith("data:image/") && str.includes(";base64,")) return true;
  if (str.length >= MIN_BASE64_DETECT_LENGTH) {
    const prefix = str.slice(0, 128);
    return /^[A-Za-z0-9+/=]+$/.test(prefix);
  }
  return false;
}

function stripLargeBase64(value, projectRoot, depth = 0) {
  if (depth > 20 || value === null || value === undefined) return value;

  if (typeof value === "string") {
    if (isBase64Data(value)) {
      const isDataUrl = value.startsWith("data:image/");
      const rawBase64 = isDataUrl ? value.slice(value.indexOf(";base64,") + 8) : value;
      const sizeBytes = Math.round(rawBase64.length * 0.75);
      const sizeKB = (sizeBytes / 1024).toFixed(1);

      if (projectRoot && fs.existsSync(projectRoot)) {
        try {
          const captureDir = path.join(projectRoot, "Temp", "PiUnityHarness", "Captures");
          if (!fs.existsSync(captureDir)) fs.mkdirSync(captureDir, { recursive: true });
          const fileName = `capture_${Date.now()}_${Math.random().toString(36).slice(2, 8)}.png`;
          const filePath = path.join(captureDir, fileName);
          fs.writeFileSync(filePath, Buffer.from(rawBase64, "base64"));
          const relPath = path.relative(projectRoot, filePath).replace(/\\/g, "/");
          return `[Base64 Image (${sizeKB} KB) stripped to prevent 413 Payload Too Large. Saved to: ${relPath}]`;
        } catch {
          // Fall back
        }
      }
      return `[Base64 Image data (${sizeKB} KB) stripped to prevent 413 Payload Too Large]`;
    }
    return value;
  }

  if (Array.isArray(value)) {
    return value.map((item) => stripLargeBase64(item, projectRoot, depth + 1));
  }

  if (typeof value === "object") {
    const result = {};
    const siblingSavedPath = typeof value.SavedPath === "string" ? value.SavedPath
      : typeof value.savedPath === "string" ? value.savedPath
      : typeof value.filePath === "string" ? value.filePath
      : typeof value.path === "string" ? value.path
      : undefined;

    for (const [key, val] of Object.entries(value)) {
      const isBase64Key = /^(base64|image_base64|rawimage|screenshot_base64)$/i.test(key);
      if (isBase64Key && typeof val === "string" && val.length > 64) {
        const sizeBytes = Math.round(val.length * 0.75);
        const sizeKB = (sizeBytes / 1024).toFixed(1);
        if (siblingSavedPath) {
          result[key] = `[Base64 Image (${sizeKB} KB) stripped to prevent 413 Payload Too Large. Image saved at: ${siblingSavedPath}]`;
        } else {
          result[key] = stripLargeBase64(val, projectRoot, depth + 1);
        }
      } else {
        result[key] = stripLargeBase64(val, projectRoot, depth + 1);
      }
    }
    return result;
  }

  return value;
}

function safeFormatToolResponse(rawResult, projectRoot, maxChars = MAX_SAFE_RESPONSE_CHARS) {
  const sanitized = stripLargeBase64(rawResult, projectRoot);

  let text;
  if (typeof sanitized === "string") {
    text = sanitized;
  } else if (
    sanitized &&
    typeof sanitized === "object" &&
    "output" in sanitized &&
    Object.keys(sanitized).length <= 3
  ) {
    text = typeof sanitized.output === "string" ? sanitized.output : JSON.stringify(sanitized.output ?? sanitized, null, 2);
  } else {
    text = JSON.stringify(sanitized, null, 2);
  }

  if (text.length <= maxChars) {
    return { text, details: sanitized };
  }

  let savedScratchPath;
  if (projectRoot && fs.existsSync(projectRoot)) {
    try {
      const scratchDir = path.join(projectRoot, "Temp", "PiUnityHarness", "AgentScratch");
      if (!fs.existsSync(scratchDir)) fs.mkdirSync(scratchDir, { recursive: true });
      const fileName = `large_tool_output_${Date.now()}.txt`;
      const fullPath = path.join(scratchDir, fileName);
      fs.writeFileSync(fullPath, text, "utf8");
      savedScratchPath = path.relative(projectRoot, fullPath).replace(/\\/g, "/");
    } catch {
      // Fallback
    }
  }

  if (!savedScratchPath) {
    try {
      const tempPath = path.join(os.tmpdir(), `pi_unity_large_output_${Date.now()}.txt`);
      fs.writeFileSync(tempPath, text, "utf8");
      savedScratchPath = tempPath;
    } catch {
      // Ignore
    }
  }

  const headSize = Math.floor(maxChars * 0.75);
  const tailSize = Math.floor(maxChars * 0.20);
  const head = text.slice(0, headSize);
  const tail = text.slice(-tailSize);
  const omittedCount = text.length - headSize - tailSize;

  const warningHeader = `[WARNING: Tool output was truncated from ${text.length.toLocaleString()} to ${maxChars.toLocaleString()} characters to prevent Error 413 (Payload Too Large).\nFull un-truncated output saved to: ${savedScratchPath ?? "Temp/PiUnityHarness/AgentScratch/"}]\n\n`;

  const truncatedText = `${warningHeader}--- Output (first ${headSize.toLocaleString()} chars) ---\n${head}\n\n... [${omittedCount.toLocaleString()} characters omitted] ...\n\n--- Output (last ${tailSize.toLocaleString()} chars) ---\n${tail}`;

  return { text: truncatedText, details: sanitized };
}

class UnityHarnessClient {
  constructor(projectPath) {
    this.projectPath = projectPath;
    this.bridge = null;
    this.socket = null;
    this.connected = false;
    this.pending = new Map();
    this.buffer = "";
    this.reqIdCounter = 0;
    this.pingInterval = null;
  }

  getProjectPath() {
    return this.projectPath;
  }

  loadBridge() {
    const bridgePath = path.join(this.projectPath, "Library", "PiUnityHarness", "bridge.json");
    if (!fs.existsSync(bridgePath)) {
      throw new Error(`Unity bridge.json not found at ${bridgePath}. Is Unity Editor running with com.pi.unity-harness installed?`);
    }
    let text = fs.readFileSync(bridgePath, "utf8");
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    this.bridge = JSON.parse(text);
    return this.bridge;
  }

  async connect() {
    if (this.socket && this.connected && !this.socket.destroyed) {
      return this.socket;
    }

    const bridge = this.loadBridge();

    return new Promise((resolve, reject) => {
      const socket = net.createConnection(bridge.pipe);
      this.socket = socket;
      this.buffer = "";

      const connectTimer = setTimeout(() => {
        socket.destroy();
        reject(new Error(`Connecting to Unity Named Pipe (${bridge.pipe}) timed out.`));
      }, 5000);

      socket.on("connect", () => {
        clearTimeout(connectTimer);
        this.connected = true;

        if (this.pingInterval) clearInterval(this.pingInterval);
        this.pingInterval = setInterval(() => {
          if (this.connected && this.pending.size === 0) {
            this.rawRequest("ping", {}, 4000).catch(() => {});
          }
        }, 5000);

        resolve(socket);
      });

      socket.on("data", (chunk) => {
        this.buffer += chunk.toString("utf8");
        let newlineIndex;
        while ((newlineIndex = this.buffer.indexOf("\n")) >= 0) {
          const line = this.buffer.slice(0, newlineIndex).trim();
          this.buffer = this.buffer.slice(newlineIndex + 1);
          if (!line) continue;

          try {
            const msg = JSON.parse(line);
            const reqId = msg.reply_to;
            if (reqId && this.pending.has(reqId)) {
              const { resolve, reject, timer } = this.pending.get(reqId);
              clearTimeout(timer);
              this.pending.delete(reqId);

              if (msg.ok) {
                resolve(msg.result);
              } else {
                reject(new Error(msg.error || "Unity harness request failed"));
              }
            }
          } catch (err) {
            console.error("Failed to parse message from Unity bridge:", err, line);
          }
        }
      });

      socket.on("error", (err) => {
        this.connected = false;
        for (const [id, req] of this.pending) {
          clearTimeout(req.timer);
          req.reject(new Error(`Socket error: ${err.message}`));
        }
        this.pending.clear();
      });

      socket.on("close", () => {
        this.connected = false;
        if (this.pingInterval) {
          clearInterval(this.pingInterval);
          this.pingInterval = null;
        }
      });
    });
  }

  async rawRequest(type, payload = {}, timeoutMs = 30000) {
    const socket = await this.connect();
    const bridge = this.bridge;
    const id = `req-${++this.reqIdCounter}-${Date.now()}`;

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Request ${type} (id: ${id}) timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });

      const frame = JSON.stringify({
        id,
        type,
        token: bridge.token,
        timeoutMs,
        payload,
      }) + "\n";

      socket.write(frame);
    });
  }
}

// Instantiate client
const projectRoot = resolveUnityProject();
const client = new UnityHarnessClient(projectRoot);

// Create MCP Server
const server = new Server(
  {
    name: "pi-unity-harness",
    version: "1.0.0",
  },
  {
    capabilities: {
      tools: {},
    },
  }
);

// Register Tools
server.setRequestHandler(ListToolsRequestSchema, async () => {
  return {
    tools: [
      {
        name: "unity_ping",
        description: "Ping the Unity native broker to verify connection and broker responsiveness.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "unity_status",
        description: "Get status of the Unity Editor, broker connection, and domain reload state.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "unity_snapshot",
        description: "Get a comprehensive context snapshot of the Unity Editor (active scenes, hierarchy, selection, console logs).",
        inputSchema: {
          type: "object",
          properties: {
            maxDepth: {
              type: "integer",
              description: "Max hierarchy depth to traverse (default: 3)",
            },
            maxNodes: {
              type: "integer",
              description: "Max GameObjects to include in hierarchy (default: 500)",
            },
            logLimit: {
              type: "integer",
              description: "Max number of recent logs to include (default: 50)",
            },
            logLevel: {
              type: "string",
              description: "Minimum log level: 'error', 'warning', or 'all' (default: 'error')",
              enum: ["error", "warning", "all"],
            },
          },
        },
      },
      {
        name: "unity_eval",
        description: "Execute C# code or expression in the Unity Editor main thread and return the evaluated result.",
        inputSchema: {
          type: "object",
          properties: {
            code: {
              type: "string",
              description: "The C# code or expression to execute in Unity Editor main thread.",
            },
          },
          required: ["code"],
        },
      },
      {
        name: "unity_eval_file",
        description: "Execute a C# script file or .repl file in the Unity Editor main thread.",
        inputSchema: {
          type: "object",
          properties: {
            filePath: {
              type: "string",
              description: "Path to .cs or .repl file (relative to Unity project root or absolute).",
            },
          },
          required: ["filePath"],
        },
      },
      {
        name: "unity_recompile",
        description: "Trigger Unity script compilation and wait for compilation to complete.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "unity_timeline",
        description: "Query recent operations timeline and audit history from the broker.",
        inputSchema: {
          type: "object",
          properties: {
            limit: {
              type: "integer",
              description: "Number of timeline entries to return (1-200, default: 20)",
            },
            success: {
              type: "string",
              description: "Filter by success status ('all', 'success', 'failure')",
              enum: ["all", "success", "failure"],
            },
          },
        },
      },
      {
        name: "unity_list_commands",
        description: "List all Unity Pipeline [CliCommand] registered in the Unity Editor.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "unity_pipeline",
        description: "Execute a Unity Pipeline command registered via [CliCommand].",
        inputSchema: {
          type: "object",
          properties: {
            name: {
              type: "string",
              description: "Name of the pipeline command to execute (e.g. 'run_tests', 'uitree_snapshot', 'vision_capture').",
            },
            parameters: {
              type: "object",
              description: "Key-value parameters to pass to the pipeline command.",
            },
          },
          required: ["name"],
        },
      },
    ],
  };
});

// Handle Tool Calls
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;
  const projectRoot = client.getProjectPath();

  try {
    switch (name) {
      case "unity_ping": {
        const result = await client.rawRequest("ping", {});
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_status": {
        const result = await client.rawRequest("status", {});
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_snapshot": {
        const payload = {
          maxDepth: args.maxDepth ?? 3,
          maxNodes: args.maxNodes ?? 500,
          logLimit: args.logLimit ?? 50,
          logLevel: args.logLevel ?? "error",
        };
        const result = await client.rawRequest("context_snapshot", payload, 20000);
        let rawContent = result;
        if (result && result.channel === "file" && result.filePath) {
          const fullPath = path.isAbsolute(result.filePath)
            ? result.filePath
            : path.join(projectRoot, result.filePath);
          if (fs.existsSync(fullPath)) {
            try {
              const fileText = fs.readFileSync(fullPath, "utf8");
              rawContent = JSON.parse(fileText);
            } catch {
              rawContent = fs.readFileSync(fullPath, "utf8");
            }
          }
        }
        const formatted = safeFormatToolResponse(rawContent, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_eval": {
        const code = String(args.code || "");
        const result = await client.rawRequest("validate_execute_code", { code }, 30000);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_eval_file": {
        const filePath = String(args.filePath || "");
        const result = await client.rawRequest("validate_execute_file", { filePath }, 30000);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_recompile": {
        const result = await client.rawRequest("recompile", {}, 120000);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_timeline": {
        const payload = {
          limit: args.limit ?? 20,
          success: args.success ?? "all",
        };
        const result = await client.rawRequest("timeline", payload);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_list_commands": {
        const result = await client.rawRequest("list_commands", {}, 15000);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      case "unity_pipeline": {
        const commandName = String(args.name || "");
        const paramsJson = JSON.stringify(args.parameters || {});
        const result = await client.rawRequest("command", { name: commandName, parametersJson: paramsJson }, 30000);
        const formatted = safeFormatToolResponse(result, projectRoot);
        return {
          content: [{ type: "text", text: formatted.text }],
        };
      }

      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  } catch (error) {
    const errorMsg = `Error: ${error.message || String(error)}`;
    const formatted = safeFormatToolResponse(errorMsg, projectRoot);
    return {
      isError: true,
      content: [{ type: "text", text: formatted.text }],
    };
  }
});

// Start STDIO Transport
const transport = new StdioServerTransport();
await server.connect(transport);
