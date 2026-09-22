import { execFileSync } from "node:child_process";
import { release } from "node:os";
import { posix, win32 } from "node:path";

export class PathConversionError extends Error {
  readonly code = "path_conversion_failed";
  constructor() {
    super("Cannot translate the path between WSL and Windows. Check wslpath and the drive/share mount, then retry unity_discover with an accessible project path.");
  }
}

export function isWindowsAbsolute(path: string): boolean {
  return /^[a-z]:[\\/]/i.test(path) || /^(?:\\\\|\/\/)[^\\/]+[\\/][^\\/]+/.test(path);
}

export function detectWsl(): boolean {
  return process.platform === "linux"
    && Boolean(process.env.WSL_DISTRO_NAME || process.env.WSL_INTEROP || /microsoft/i.test(release()));
}

export class PathBoundary {
  readonly platform: NodeJS.Platform;
  readonly wsl: boolean;
  private readonly translate: (direction: "-u" | "-w", path: string) => string;
  private readonly cache = new Map<string, string>();

  constructor(options: {
    platform?: NodeJS.Platform;
    wsl?: boolean;
    translate?: (direction: "-u" | "-w", path: string) => string;
  } = {}) {
    this.platform = options.platform ?? process.platform;
    this.wsl = this.platform === "linux" && (options.wsl ?? detectWsl());
    this.translate = options.translate ?? ((direction, path) => execFileSync(
      "wslpath", [direction, path],
      { encoding: "utf8", timeout: 3000, maxBuffer: 64 * 1024, stdio: ["ignore", "pipe", "pipe"] },
    ).replace(/\r?\n$/, ""));
  }

  private convert(direction: "-u" | "-w", path: string): string {
    const key = `${direction}:${path}`;
    const cached = this.cache.get(key);
    if (cached !== undefined) return cached;
    try {
      const converted = this.translate(direction, path);
      if (!converted || (direction === "-u" ? !posix.isAbsolute(converted) : !isWindowsAbsolute(converted))) {
        throw new PathConversionError();
      }
      // Bound cache growth for long-lived sessions with many scratch/output paths.
      if (this.cache.size >= 256) this.cache.clear();
      this.cache.set(key, converted);
      return converted;
    } catch {
      // Never leak the converter command line or raw stderr into a tool result.
      throw new PathConversionError();
    }
  }

  host(path: string, cwd = process.cwd()): string {
    if (this.platform === "win32") return win32.resolve(cwd, path);
    if (isWindowsAbsolute(path)) {
      if (!this.wsl) throw new PathConversionError();
      return posix.resolve(this.convert("-u", win32.normalize(path)));
    }
    if (/^[a-z]:/i.test(path) || path.startsWith("\\")) throw new PathConversionError();
    return posix.resolve(cwd, path);
  }

  windowsCli(bin: string): boolean {
    return this.wsl && /\.exe$/i.test(bin);
  }

  cliPath(path: string, bin: string): string {
    if (!this.windowsCli(bin)) return path;
    if (isWindowsAbsolute(path)) return win32.normalize(path);
    // Relative paths remain project-relative, not extension/session-cwd-relative.
    if (posix.isAbsolute(path)) return this.convert("-w", path);
    return path;
  }

  sameProject(a: string, b: string): boolean {
    if (this.platform === "win32") return this.host(a).toLowerCase() === this.host(b).toLowerCase();
    if (isWindowsAbsolute(a) && isWindowsAbsolute(b)) {
      // Normalize drive spelling, but do not fold directory case on WSL case-sensitive mounts.
      const normalize = (p: string) => win32.resolve(p).replace(/^[a-z]:/i, (drive) => drive.toUpperCase());
      return normalize(a) === normalize(b);
    }
    return this.host(a) === this.host(b);
  }

  argv(args: string[], bin: string): string[] {
    if (!this.windowsCli(bin)) return [...args];
    // Only typed CLI path slots: never rewrite C# code, pipeline JSON or arbitrary values.
    const paths = args[0] === "eval" ? new Set(["-f", "--file"])
      : args[0] === "capture" ? new Set(["--out"]) : new Set<string>();
    const takesValue = new Set(["--timeout", "--fields", "--mode", "--depth", "--max-nodes", "--log-limit", "--log-level"]);
    const out = [...args];
    for (let i = 1; i < out.length; i++) {
      if (out[i] === "--") break;
      const eq = out[i].indexOf("=");
      const flag = eq < 0 ? out[i] : out[i].slice(0, eq);
      if (paths.has(flag)) {
        if (eq >= 0) out[i] = `${flag}=${this.cliPath(out[i].slice(eq + 1), bin)}`;
        else if (i + 1 < out.length) out[++i] = this.cliPath(out[i], bin);
      } else if (eq < 0 && takesValue.has(flag)) {
        i++;
      }
    }
    return out;
  }

  env(bin: string, env: NodeJS.ProcessEnv, project?: string): NodeJS.ProcessEnv {
    const result: NodeJS.ProcessEnv = {
      ...env,
      PI_UNITY_CLIENT: "pi-ext",
      PI_UNITY_AGENT_ID: env.PI_UNITY_AGENT_ID?.trim() || "default",
    };
    const path = project ?? env.UNITY_PROJECT_PATH;
    if (path) result.UNITY_PROJECT_PATH = this.cliPath(path, bin);
    return result;
  }
}

export const paths = new PathBoundary();
