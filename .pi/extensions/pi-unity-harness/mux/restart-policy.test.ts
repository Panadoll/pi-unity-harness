import assert from "node:assert/strict";
import { test } from "node:test";
import { decide } from "./restart-policy.ts";

const exit = { code: 1, signal: null };
test("restart policy covers restart", () => assert.deepEqual(decide({ restartsSinceStable: 0, budget: 1, inflightDispatched: false, queueLength: 1, lastExit: exit }), { kind: "restart" }));
test("restart policy preserves queue when in-flight was dispatched", () => assert.deepEqual(decide({ restartsSinceStable: 0, budget: 1, inflightDispatched: true, queueLength: 1, lastExit: exit }), { kind: "fail_inflight_keep_queue" }));
test("restart policy fails all when budget is exhausted", () => assert.equal(decide({ restartsSinceStable: 1, budget: 1, inflightDispatched: false, queueLength: 1, lastExit: exit }).kind, "fail_all"));
