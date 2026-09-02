import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/** Nested key in pi settings.json */
export const SETTINGS_KEY = "pi-unity-harness";

const CONFIG_DIR_NAME = ".pi";

export interface UnityHarnessSettings {
  /**
   * When false (default), unity_* tools are not registered and auto-connect is skipped.
   * Must be turned on explicitly via /unity-harness on or settings.
   */
  enabled: boolean;
}

export const DEFAULT_SETTINGS: UnityHarnessSettings = {
  enabled: true,
};


function agentDir(): string {
  const envDir = process.env.PI_CODING_AGENT_DIR?.trim();
  if (envDir) {
    if (envDir.startsWith("~/") || envDir === "~") {
      return join(homedir(), envDir.slice(1));
    }
    return envDir;
  }
  return join(homedir(), CONFIG_DIR_NAME, "agent");
}

function parseJsonFile(path: string): Record<string, unknown> | null {
  if (!existsSync(path)) return null;
  try {
    let text = readFileSync(path, "utf8");
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    // Tolerate trailing commas commonly introduced by hand-editing
    text = text.replace(/,\s*([\]}])/g, "$1");
    const parsed = JSON.parse(text);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/** Coerce true/false from boolean, number, or common string forms. */
export function coerceEnabled(value: unknown): boolean | undefined {
  if (typeof value === "boolean") return value;
  if (typeof value === "number" && Number.isFinite(value)) return value !== 0;
  if (typeof value === "string") {
    const raw = value.trim().toLowerCase();
    if (raw === "0" || raw === "false" || raw === "off" || raw === "no" || raw === "disabled" || raw === "disable") return false;
    if (raw === "1" || raw === "true" || raw === "on" || raw === "yes" || raw === "enabled" || raw === "enable") return true;
  }
  return undefined;
}

function asSettings(raw: unknown): Partial<UnityHarnessSettings> {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return {};
  const obj = raw as Record<string, unknown>;
  const out: Partial<UnityHarnessSettings> = {};
  const enabled = coerceEnabled(obj.enabled);
  if (enabled !== undefined) out.enabled = enabled;
  return out;
}

function envEnabledOverride(): boolean | undefined {
  return coerceEnabled(process.env.PI_UNITY_HARNESS_ENABLED);
}

export function globalSettingsPath(): string {
  return join(agentDir(), "settings.json");
}

export function projectSettingsPath(cwd: string): string {
  return join(cwd, CONFIG_DIR_NAME, "settings.json");
}

/**
 * Merge defaults ← global settings ← project settings ← env override.
 * Project wins over global; env wins over both for the current process.
 */
export function loadUnityHarnessSettings(cwd?: string): UnityHarnessSettings {
  const globalPath = globalSettingsPath();
  const projectPath = cwd ? projectSettingsPath(cwd) : undefined;
  const globalRaw = parseJsonFile(globalPath);
  const projectRaw = projectPath ? parseJsonFile(projectPath) : null;

  const merged: UnityHarnessSettings = {
    ...DEFAULT_SETTINGS,
    ...asSettings(globalRaw?.[SETTINGS_KEY]),
    ...asSettings(projectRaw?.[SETTINGS_KEY]),
  };

  const env = envEnabledOverride();
  if (env !== undefined) merged.enabled = env;
  return merged;
}

/** Diagnostic details for /unity-harness status */
export function inspectUnityHarnessSettings(cwd?: string): {
  settings: UnityHarnessSettings;
  globalPath: string;
  projectPath: string | null;
  globalExists: boolean;
  projectExists: boolean;
  globalRaw: unknown;
  projectRaw: unknown;
  env: string | undefined;
} {
  const globalPath = globalSettingsPath();
  const projectPath = cwd ? projectSettingsPath(cwd) : null;
  const globalFile = parseJsonFile(globalPath);
  const projectFile = projectPath ? parseJsonFile(projectPath) : null;
  return {
    settings: loadUnityHarnessSettings(cwd),
    globalPath,
    projectPath,
    globalExists: existsSync(globalPath),
    projectExists: projectPath ? existsSync(projectPath) : false,
    globalRaw: globalFile?.[SETTINGS_KEY] ?? null,
    projectRaw: projectFile?.[SETTINGS_KEY] ?? null,
    env: process.env.PI_UNITY_HARNESS_ENABLED,
  };
}

/**
 * Persist enabled into a settings.json file (creates file or merges key).
 * scope "global" → ~/.pi/agent/settings.json
 * scope "project" → <cwd>/.pi/settings.json
 */
export function persistEnabled(
  enabled: boolean,
  scope: "global" | "project",
  cwd?: string,
): { path: string; settings: UnityHarnessSettings } {
  const path =
    scope === "global"
      ? globalSettingsPath()
      : projectSettingsPath(cwd ?? process.cwd());

  const existing = parseJsonFile(path) ?? {};
  const current = asSettings(existing[SETTINGS_KEY]);
  const nextBlock: UnityHarnessSettings = {
    ...DEFAULT_SETTINGS,
    ...current,
    enabled,
  };
  existing[SETTINGS_KEY] = nextBlock;

  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${JSON.stringify(existing, null, 2)}\n`, "utf8");
  return { path, settings: nextBlock };
}

/** Absolute path of this extension directory (for settings.extensions). */
export function extensionDirPath(): string {
  return dirname(fileURLToPath(import.meta.url));
}

export function describeSettingsPaths(cwd?: string): string {
  return [
    `global: ${globalSettingsPath()}`,
    cwd ? `project: ${projectSettingsPath(cwd)}` : `project: <cwd>/${CONFIG_DIR_NAME}/settings.json`,
    "env: PI_UNITY_HARNESS_ENABLED (optional process override)",
  ].join("\n");
}
