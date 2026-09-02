---
name: pi-unity-pipeline
description: >
  Discover and execute registered Unity Pipeline [CliCommand] handlers via
  pi-unity list-commands and pi-unity pipeline. Use when interacting with
  standard pipeline commands (assets_*, gameobject_*, uitree_*, input_*, etc.).
---

# pi-unity-pipeline

发现并执行 Unity Editor 中通过 `[CliCommand]` 注册的 Pipeline 模块化命令。

## 发现命令与参数 Schema

```bash
# 列举全部已注册的 Pipeline 命令与参数定义
pi-unity list-commands

# 配合 jq 查找特定前缀的命令
pi-unity list-commands --json
```

## 执行命令

```bash
# 使用 -p / --param 传递键值对
pi-unity pipeline find_gameobjects -p name="Main Camera"

# 传递多个参数
pi-unity pipeline create_gameobject -p name="Player" -p parent="WorldRoot"

# 使用 --params-json 传递复杂 JSON 参数
pi-unity pipeline uitree_snapshot --params-json "{\"interactiveOnly\": true}"

# 常用 UI 树与输入命令
pi-unity pipeline uitree_find -p text="Start Game"
pi-unity pipeline input_click -p x=640 -p y=360
```

## 参数类型自动推断

- 布尔值：`true` / `false` 自动转为 JSON boolean。
- 数字：整数 / 浮点数自动转为 JSON number。
- 嵌套 JSON：`{...}` 或 `[...]` 自动解析为对应 JSON 结构。
- 其它内容作为字符串处理。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-pipeline --event used`。

