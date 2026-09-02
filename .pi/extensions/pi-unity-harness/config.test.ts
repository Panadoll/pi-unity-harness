import assert from "node:assert/strict";
import { test } from "node:test";
import {
  coerceEnabled,
  DEFAULT_SETTINGS,
  loadUnityHarnessSettings,
  SETTINGS_KEY,
} from "./config.ts";

test("loadUnityHarnessSettings defaults to enabled when no env override", () => {
  const prev = process.env.PI_UNITY_HARNESS_ENABLED;
  delete process.env.PI_UNITY_HARNESS_ENABLED;
  try {
    assert.equal(DEFAULT_SETTINGS.enabled, true);
    assert.equal(SETTINGS_KEY, "pi-unity-harness");
    const settings = loadUnityHarnessSettings();
    assert.equal(typeof settings.enabled, "boolean");
  } finally {
    if (prev === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prev;
  }
});

test("DEFAULT_SETTINGS.enabled is true", () => {
  assert.equal(DEFAULT_SETTINGS.enabled, true);
});


test("PI_UNITY_HARNESS_ENABLED env overrides settings", () => {
  const prev = process.env.PI_UNITY_HARNESS_ENABLED;
  try {
    process.env.PI_UNITY_HARNESS_ENABLED = "0";
    assert.equal(loadUnityHarnessSettings().enabled, false);
    process.env.PI_UNITY_HARNESS_ENABLED = "true";
    assert.equal(loadUnityHarnessSettings().enabled, true);
  } finally {
    if (prev === undefined) delete process.env.PI_UNITY_HARNESS_ENABLED;
    else process.env.PI_UNITY_HARNESS_ENABLED = prev;
  }
});

test("coerceEnabled accepts string/number forms", () => {
  assert.equal(coerceEnabled(false), false);
  assert.equal(coerceEnabled("false"), false);
  assert.equal(coerceEnabled("off"), false);
  assert.equal(coerceEnabled(0), false);
  assert.equal(coerceEnabled(true), true);
  assert.equal(coerceEnabled("true"), true);
  assert.equal(coerceEnabled(1), true);
  assert.equal(coerceEnabled("maybe"), undefined);
});
