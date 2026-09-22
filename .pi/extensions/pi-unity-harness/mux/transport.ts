import type { ChildProcess } from "node:child_process";

export interface MuxTransport {
  child: ChildProcess;
  stdoutBuffer: string;
  stderrTail: string;
}

export function appendFramedOutput(state: MuxTransport, chunk: string, onLine: (line: string) => void, maxChars: number): void {
  state.stdoutBuffer += chunk;
  if (state.stdoutBuffer.length > maxChars) throw new Error("mux stdout 过大");
  let newline = state.stdoutBuffer.indexOf("\n");
  while (newline >= 0) {
    const line = state.stdoutBuffer.slice(0, newline).replace(/\r$/, "");
    state.stdoutBuffer = state.stdoutBuffer.slice(newline + 1);
    if (line.trim()) onLine(line);
    newline = state.stdoutBuffer.indexOf("\n");
  }
}
