---
name: pi-unity-capture
description: >
  Capture single viewport screenshots of Unity GameView or SceneView via
  pi-unity capture. Use for quick visual checks without multi-frame analysis.
---

# pi-unity-capture

单张视口截图（速度模式），支持从 GameView 或 SceneView 实时截取画面并落地到磁盘。

## 基础用法

```bash
# 截取 GameView 画面（默认）
pi-unity capture --mode game

# 截取 SceneView 画面
pi-unity capture --mode scene

# 指定输出保存路径
pi-unity capture --mode scene --out "Temp/PiUnityHarness/Captures/scene_view.png"

# 结构化输出
pi-unity capture --json
```

## 输出信息

命令返回包含如下字段的摘要或 JSON：
- `status`：`succeeded`
- `path`：磁盘图片绝对/相对路径
- `width` / `height`：分辨率尺寸
- `bytes`：文件大小字节数
- 严禁向终端输出 Base64。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-capture --event used`。

