export interface RestartContext {
  restartsSinceStable: number;
  budget: number;
  inflightDispatched: boolean;
  queueLength: number;
  lastExit: { code: number | null; signal: string | null };
}

export type RestartDecision =
  | { kind: "restart" }
  | { kind: "fail_inflight_keep_queue" }
  | { kind: "fail_all"; reason: string };

export function decide(context: RestartContext): RestartDecision {
  if (context.inflightDispatched) return { kind: "fail_inflight_keep_queue" };
  if (context.queueLength > 0 && context.restartsSinceStable < context.budget) return { kind: "restart" };
  return { kind: "fail_all", reason: "restart budget exhausted or queue empty" };
}
