# Contributing

[中文](CONTRIBUTING.md) | English

Issues and pull requests are welcome. Please read the [README](README.en.md) first to understand the project structure.

## Development environment

- Windows (the native broker only implements the named pipe transport)
- Rust toolchain (builds `native/`)
- Node.js 18+ (runs extension tests)
- Unity Editor 2021.3+ (verifies the Editor package and pipeline integration)

## Building and testing locally

```bash
# native broker
./scripts/build-native.sh          # or PowerShell: ./scripts/build-native.ps1

# extension unit tests (node:test)
node --test .pi/extensions/pi-unity-harness/*.test.ts

# Rust unit tests
cd native && cargo test
```

Unity-side verification: add `unity/com.pi.unity-harness` to a project as a local UPM
package and run EditMode / PlayMode tests (filter `Pi.UnityHarness`).

## Commit conventions

- Commit messages follow Conventional Commits (`feat:` / `fix:` / `docs:` / `chore:` / `refactor:` / `test:`)
- Never reference internal projects, personal paths, or credentials
- When changing Unity package assets, commit the matching `.meta` files as well

## Pull request process

1. Fork the repository and create a feature branch
2. Add tests for your change (extension logic with node:test, C# logic with EditMode/PlayMode tests)
3. Run the local tests and confirm they pass
4. Open a PR describing the motivation and how you verified the change
