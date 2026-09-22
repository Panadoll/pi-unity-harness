/**
 * Pipeline command schema helpers and TypeBox converter for pi-unity-harness extension.
 * Used by index.ts during session startup to dynamically register 280+ unity_* pipeline tools.
 */

export interface PipelineParameterInfo {
  name: string;
  description?: string;
  type?: string;
  typeFullName?: string;
  jsonType?: "boolean" | "integer" | "number" | "string" | "array" | "object";
  required?: boolean;
  defaultValue?: unknown;
}

export interface PipelineCommandInfo {
  name: string;
  description?: string;
  mainThreadRequired?: boolean;
  runtimeOnly?: boolean;
  policy?: { mutability?: "read" | "write" | "destructive"; thread?: "main" | "any"; runtime?: "editor" | "runtime" | "both"; source?: "attribute" | "sidecar" | "default" };
  schema?: Record<string, unknown> | null;
  parameters?: PipelineParameterInfo[];
}

export interface PipelineCommandList {
  pipelineAvailable: boolean;
  commands: Array<PipelineCommandInfo | null | undefined>;
}

export const PIPELINE_TOOL_EXCLUDE = new Set([
  "eval",
  "recompile",
  "recompile_status",
  "editor_status",
  "run_tests",
  "vision_observe",
  "vision_capture",
]);

export const PIPELINE_SHORTCUT_COMMANDS = new Set([
  "list_tests",
  "reload_file",
  "reload_file_override",
]);

export interface TypeBoxLike {
  Unsafe(schema: any): any;
  Object(properties: Record<string, any>): any;
  Optional(schema: any): any;
  Boolean(options?: { description?: string }): any;
  Number(options?: { description?: string }): any;
  Integer(options?: { description?: string }): any;
  String(options?: { description?: string }): any;
}

export function normalizePipelineToolName(commandName: string): string {
  return `unity_${commandName.replace(/[^a-zA-Z0-9_]/g, "_").toLowerCase()}`;
}

export function parameterToTypeBox(TypeApi: TypeBoxLike, parameter: PipelineParameterInfo): any {
  const options = { description: parameter.description };
  const jsonType = parameter.jsonType;
  if (jsonType === "boolean") return TypeApi.Boolean(options);
  if (jsonType === "integer") return TypeApi.Integer(options);
  if (jsonType === "number") return TypeApi.Number(options);
  if (jsonType === "string" || jsonType === "array" || jsonType === "object") return TypeApi.String(options);
  const typeName = String(parameter.type ?? parameter.typeFullName ?? "").toLowerCase();
  if (typeName.includes("boolean")) return TypeApi.Boolean(options);
  if (typeName.includes("int") || typeName.includes("long") || typeName.includes("short") || typeName.includes("byte")) return TypeApi.Integer(options);
  if (typeName.includes("single") || typeName.includes("double") || typeName.includes("decimal") || typeName.includes("float")) return TypeApi.Number(options);
  return TypeApi.String(options);
}

export function schemaToTypeBox(
  TypeApi: TypeBoxLike,
  schema: Record<string, unknown> | null | undefined,
  parameters: PipelineParameterInfo[] | undefined,
): any {
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

export function isVisiblePipelineCommand(
  command: PipelineCommandInfo | null | undefined,
  excludedCommands = PIPELINE_TOOL_EXCLUDE,
  allowDestructive = process.env.PIPELINE_TOOL_ALLOW_DESTRUCTIVE === "1",
): command is PipelineCommandInfo {
  return Boolean(command?.name) && command?.runtimeOnly !== true && !excludedCommands.has(command.name)
    && (command.policy?.mutability !== "destructive" || allowDestructive);
}

export function filterPipelineCommands(
  list: PipelineCommandList | null | undefined,
  excludedCommands = PIPELINE_TOOL_EXCLUDE,
  allowDestructive = process.env.PIPELINE_TOOL_ALLOW_DESTRUCTIVE === "1",
): PipelineCommandInfo[] {
  const commands = Array.isArray(list?.commands) ? list.commands : [];
  return commands.filter((command): command is PipelineCommandInfo => isVisiblePipelineCommand(command, excludedCommands, allowDestructive));
}

export function pipelineCommandSummary(
  command: PipelineCommandInfo,
  includeSchema = false,
  shortcutCommands = PIPELINE_SHORTCUT_COMMANDS,
) {
  const shortcut = shortcutCommands.has(command.name);
  return {
    name: command.name,
    policy: command.policy ?? { mutability: "write", source: "default" },
    shortcut,
    shortcutTool: shortcut ? normalizePipelineToolName(command.name) : null,
    description: command.description,
    mainThreadRequired: command.mainThreadRequired,
    parameters: command.parameters ?? [],
    ...(includeSchema ? { schema: command.schema ?? null } : {}),
  };
}
