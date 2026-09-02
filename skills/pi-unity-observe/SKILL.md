---
name: pi-unity-observe
description: >
  Perform multi-frame visual observation in Unity PlayMode via pi-unity observe.
  Use in GUI mode for UI alignment, animation feedback, dHash change detection,
  and grid overlay visualization.
---

# pi-unity-observe

在 Unity PlayMode 下进行多帧感知跑测：连续采集视口帧序列、进行内存级像素去重、计算 dHash 亮度感知指纹、合成 0-1000 网格标注叠图，并在检测到画面变化时生成时间序列图。

## 基础用法

```bash
# 默认 3 帧观察，间隔 160ms，叠加网格与标注
pi-unity observe

# 自定义帧数与采样间隔
pi-unity observe --frames 4 --interval 200

# 纯画面观察（不叠加网格与标注）
pi-unity observe --overlay none

# 仅网格叠图
pi-unity observe --overlay grid
```

## 使用准则

- **仅在 GUI 模式下使用**：日常重构请保持在速度模式（`snapshot` + `eval` + `uitree_*`）；仅当需要验证肉眼可见效果或 UI 树无法体现的视觉反馈时才触发 `observe`。
- **前置要求**：GameView 必须处于 PlayMode。
- **大图落地**：所有捕获生成的图片自动保存至 `Temp/PiUnityHarness/Captures/`，CLI 输出仅返回文件路径与摘要，防止 Agent 上下文被 Base64 淹没。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-observe --event used`。

