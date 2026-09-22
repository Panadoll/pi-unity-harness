import assert from "node:assert/strict";
import { test } from "node:test";
import { PathBoundary } from "./paths.ts";

test("PathBoundary converts WSL and Windows project paths", () => {
  const boundary = new PathBoundary({
    platform: "linux",
    wsl: true,
    translate(direction, value) {
      if (direction === "-u" && value === "F:\\UnityProjects\\ctest") return "/mnt/f/UnityProjects/ctest";
      if (direction === "-w" && value === "/mnt/f/UnityProjects/ctest") return "F:\\UnityProjects\\ctest";
      if (direction === "-u" && value === "F:\\UnityProjects\\other") return "/mnt/f/UnityProjects/other";
      throw new Error("unexpected path");
    },
  });

  assert.equal(boundary.host("F:\\UnityProjects\\ctest"), "/mnt/f/UnityProjects/ctest");
  assert.equal(boundary.cliPath("/mnt/f/UnityProjects/ctest", "pi-unity.exe"), "F:\\UnityProjects\\ctest");
  assert.equal(boundary.sameProject("/mnt/f/UnityProjects/ctest", "F:\\UnityProjects\\ctest"), true);
  assert.equal(boundary.sameProject("/mnt/f/UnityProjects/ctest", "F:\\UnityProjects\\other"), false);
});

test("PathBoundary injects the default agent id for mux processes", () => {
  const boundary = new PathBoundary({ platform: "linux", wsl: false });
  assert.equal(boundary.env("pi-unity", {}, "/tmp/project").PI_UNITY_AGENT_ID, "default");
  assert.equal(boundary.env("pi-unity", { PI_UNITY_AGENT_ID: "agent-a" }, "/tmp/project").PI_UNITY_AGENT_ID, "agent-a");
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
