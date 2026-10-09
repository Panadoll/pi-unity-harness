import assert from "node:assert/strict";
import { test } from "node:test";
import { PathBoundary } from "./paths.ts";

test("PathBoundary converts WSL and Windows project paths", () => {
  const boundary = new PathBoundary({
    platform: "linux",
    wsl: true,
    translate(direction, value) {
      if (direction === "-u" && value === "F:\\SampleProjects\\ctest") return "/mnt/f/SampleProjects/ctest";
      if (direction === "-w" && value === "/mnt/f/SampleProjects/ctest") return "F:\\SampleProjects\\ctest";
      if (direction === "-u" && value === "F:\\SampleProjects\\other") return "/mnt/f/SampleProjects/other";
      throw new Error("unexpected path");
    },
  });

  assert.equal(boundary.host("F:\\SampleProjects\\ctest"), "/mnt/f/SampleProjects/ctest");
  assert.equal(boundary.cliPath("/mnt/f/SampleProjects/ctest", "pi-unity.exe"), "F:\\SampleProjects\\ctest");
  assert.equal(boundary.sameProject("/mnt/f/SampleProjects/ctest", "F:\\SampleProjects\\ctest"), true);
  assert.equal(boundary.sameProject("/mnt/f/SampleProjects/ctest", "F:\\SampleProjects\\other"), false);
});

test("PathBoundary injects the default agent id for mux processes", () => {
  const boundary = new PathBoundary({ platform: "linux", wsl: false });
  assert.equal(boundary.env("pi-unity", {}, "/tmp/project").PI_UNITY_AGENT_ID, "default");
  assert.equal(boundary.env("pi-unity", { PI_UNITY_AGENT_ID: "agent-a" }, "/tmp/project").PI_UNITY_AGENT_ID, "agent-a");
});

test("PathBoundary forwards session env across the WSL boundary only for Windows CLIs", () => {
  const boundary = new PathBoundary({ platform: "linux", wsl: true, translate: () => "F:\\p" });
  const env = boundary.env("pi-unity.exe", { WSLENV: "WT_SESSION:PI_UNITY_TRACE/u:", PI_UNITY_HOST_SESSION_ID: "s1", PI_UNITY_TRACE: "1" }, "/mnt/f/p");
  assert.equal(env.WSLENV, "WT_SESSION:PI_UNITY_TRACE/u:PI_UNITY_CLIENT:PI_UNITY_AGENT_ID:PI_UNITY_HOST_SESSION_ID:UNITY_PROJECT_PATH");
  assert.equal(env.UNITY_PROJECT_PATH, "F:\\p");
  assert.equal(boundary.env("pi-unity", { WSLENV: "WT_SESSION" }).WSLENV, "WT_SESSION");
});

test("PathBoundary rewrites only typed path arguments", () => {
  const boundary = new PathBoundary({
    platform: "linux",
    wsl: true,
    translate: (_direction, value) => value === "/mnt/f/shot.png" ? "F:\\shot.png" : value,
  });
  const args = boundary.argv(["capture", "--out", "/mnt/f/shot.png", "--fields", "path"], "pi-unity.exe");
  assert.deepEqual(args, ["capture", "--out", "F:\\shot.png", "--fields", "path"]);
});
