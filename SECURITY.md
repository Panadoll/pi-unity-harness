# Security Policy

## Reporting a Vulnerability

请通过 GitHub 的 [Security Advisories](https://github.com/Panadoll/pi-unity-harness/security/advisories/new)
私有渠道报告漏洞，不要在公开 issue 中披露。

请在报告中包含：

- 受影响版本与复现步骤
- 漏洞类型与潜在影响
- 如已知，附上修复建议

## 安全边界说明

本工具会在 Unity Editor 进程内执行代理提供的 C# 代码，并暴露 named pipe
端点。请只在可信环境中启用：

- 不要将 `bridge.json`（含 pipe token）暴露给不可信进程
- `unity_eval` / `unity_eval_file` 拥有 Editor 的全部权限，等价于本地管理员操作
- 文档中描述的状态面与审计数据不包含原始 eval 代码与凭据，但请勿将日志外泄
