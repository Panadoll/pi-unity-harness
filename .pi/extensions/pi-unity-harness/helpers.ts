export interface PipelineParameterInfo {
  name: string;
  description?: string;
  type?: string;
  typeFullName?: string;
  required?: boolean;
  defaultValue?: unknown;
}

export interface TypeBoxLike {
  Unsafe(schema: any): any;
  Object(properties: Record<string, any>): any;
  Optional(schema: any): any;
  Boolean(options?: { description?: string }): any;
  Number(options?: { description?: string }): any;
  Integer(options?: { description?: string }): any;
  String(options?: { description?: string }): any;
}

export function parseEditorStatus(value: unknown): { editorStatus: string; focusState?: string; windowState?: string } {
  const raw = String(value ?? "unknown");
  const [editorStatus, ...parts] = raw.split(";").map((part) => part.trim()).filter(Boolean);
  let focusState: string | undefined;
  let windowState: string | undefined;

  for (const part of parts) {
    const [key, val] = part.split("=", 2).map((item) => item.trim());
    if (key === "focus" && val) focusState = val;
    if (key === "window" && val) windowState = val;
  }

  return { editorStatus: editorStatus || "unknown", focusState, windowState };
}

export function parseUnityMajorVersion(version: string | undefined): number | undefined {
  const match = String(version ?? "").match(/^(\d+)/);
  return match ? Number(match[1]) : undefined;
}

export function schemaToTypeBox(TypeApi: TypeBoxLike, schema: Record<string, unknown> | null | undefined, parameters: PipelineParameterInfo[] | undefined): any {
  if (schema && typeof schema === "object" && schema.type === "object" && schema.properties && typeof schema.properties === "object") {
    return TypeApi.Unsafe(schema as any);
  }

  const properties: Record<string, any> = {};
  for (const parameter of parameters ?? []) {
    const item = parameterToTypeBox(TypeApi, parameter);
    properties[parameter.name] = parameter.required ? item : TypeApi.Optional(item);
  }
  return TypeApi.Object(properties);
}

export function parameterToTypeBox(TypeApi: TypeBoxLike, parameter: PipelineParameterInfo): any {
  const options = { description: parameter.description };
  const typeName = String(parameter.type ?? parameter.typeFullName ?? "").toLowerCase();
  if (typeName.includes("boolean")) return TypeApi.Boolean(options);
  if (typeName.includes("int") || typeName.includes("long") || typeName.includes("short") || typeName.includes("byte")) return TypeApi.Integer(options);
  if (typeName.includes("single") || typeName.includes("double") || typeName.includes("decimal") || typeName.includes("float")) return TypeApi.Number(options);
  return TypeApi.String(options);
}

export function normalizePipelineToolName(commandName: string): string {
  return `unity_${commandName.replace(/[^a-zA-Z0-9_]/g, "_").toLowerCase()}`;
}

export function pipelineCommandTimeoutMs(commandName: string, params: Record<string, unknown>): number {
  if (commandName === "run_tests") {
    const timeoutSeconds = typeof params.timeout === "number" && Number.isFinite(params.timeout) && params.timeout > 0
      ? params.timeout
      : 300;
    return timeoutSeconds * 1000 + 30000;
  }

  const timeoutMs = params.timeoutMs ?? params.timeout_ms;
  if (typeof timeoutMs === "number" && Number.isFinite(timeoutMs) && timeoutMs > 0) {
    return timeoutMs + 5000;
  }
  const timeoutSeconds = params.timeout;
  if (typeof timeoutSeconds === "number" && Number.isFinite(timeoutSeconds) && timeoutSeconds > 0) {
    return timeoutSeconds * 1000 + 5000;
  }
  if (commandName === "list_tests") return 60000;
  if (commandName.startsWith("reload_file")) return 120000;
  return 30000;
}