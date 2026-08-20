# STS2 LLM Agent

这是一个面向 Slay the Spire 2 `v0.107.1` 的最小可用 C# 模组。它只在已经开始的单人 run 中工作，把当前可执行动作和紧凑观察发送给 OpenAI-compatible Chat Completions API，然后只执行返回的合法 opaque action ID。Manifest 明确要求游戏版本 `0.107.1`；其他版本没有兼容性承诺。

## 安装

需要 .NET 9 SDK、已安装的游戏本体和可加载 C# 模组的游戏版本。项目不会包含或重新分发游戏 DLL、反编译代码或 API key。

```sh
chmod +x build-install.sh
./build-install.sh
```

如果游戏安装位置不同，可设置 `STS2_MANAGED_DIR` 和 `STS2_MODS_DIR`。公开的 `Sts2LlmAgent.csproj` 不包含本机游戏路径，直接构建时必须传 `-p:Sts2ManagedDir=/path/to/data_sts2_macos_arm64`；缺少该参数或其中没有 `sts2.dll`/`GodotSharp.dll` 会立即报错。脚本按 macOS/Steam 默认位置提供默认值，也接受这两个变量覆盖。脚本只有在 Release build 成功后才复制 DLL 和 `Sts2LlmAgent.json`。

## 配置

在启动游戏的同一 shell 中设置环境变量。默认关闭，必须同时设置 enabled 和 API key 才会启动：

```sh
export STS2_LLM_AGENT_ENABLED=true
export STS2_LLM_API_KEY='your-key'
export STS2_LLM_BASE_URL='https://api.deepseek.com'
export STS2_LLM_MODEL='deepseek-chat'
export STS2_LLM_TIMEOUT_SECONDS=45
export STS2_LLM_VERBOSE=false
```

请求 endpoint 会自动规范化：`https://api.deepseek.com`、`https://api.deepseek.com/` 和已经带 `/v1` 的 `https://api.deepseek.com/v1` 都只会请求一次 `/v1/chat/completions`，也接受已经完整写到 `/v1/chat/completions` 的地址。API key 只放在内存中的 Authorization header，不写入日志、观察或磁盘。模型必须严格返回 `{"action_id":"...","reason":"..."}`；reason 只用于可选的 verbose 日志。

macOS 从 Steam 图形界面启动游戏时，任意 Terminal 中后来设置的环境变量通常不会传给已经运行的 Steam/game 进程。可以从带变量的终端直接启动游戏本体：

```sh
env STS2_LLM_AGENT_ENABLED=true STS2_LLM_API_KEY='your-key' \
  '/Applications/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2'
```

也可以把同样的变量放进 Steam 的游戏启动选项，或使用一个只负责 `export` 变量后 `exec` 游戏本体的本地 wrapper。不要把 key 写进仓库、README 或共享脚本。

## 已支持

- 战斗：可播放卡牌、普通敌人目标、药水动作和结束回合。
- 地图：选择当前启用的路径点。
- 卡牌奖励、通用卡牌选择和奖励继续按钮。
- 事件选项、休息处、宝箱，以及商店购买或离开。

每次等待网络或游戏动画后都会重新读取并验证节点和动作标签；请求失败或无效响应重试一次，随后优先选择结束回合、跳过、继续或当前列表中的第一个保守选项。不会自动开始新 run、修改存档、注入作弊数值或使用 Harmony。

## 限制

模组拒绝 `RunState.Players.Count != 1` 的 run，不参与多人同步。常见未知 overlay 会枚举当前可见且启用的 clickable control，但只能提供控件类型而不一定能提供完整语义；API 失败时不会点击这类未知控件。需要复杂二次选目标的卡牌、部分多选/升级/变换/附魔屏幕和特殊事件尚未接入专用动作模型，可能需要手动处理。战斗中触发额外玩家选择的卡牌使用 AutoSlay 风格的阻塞上下文，运行时仍需观察这类卡牌是否会打开未覆盖的选择屏幕。

这是社区实验模组，不是 Mega Crit 官方产品。建议先使用非关键 run，并在 API 超时、断网和模型输出异常时确认游戏仍能由回退动作继续。

## 开发

```sh
dotnet run --project tests/Sts2LlmAgent.Core.Tests.csproj
dotnet build Sts2LlmAgent.csproj -p:Sts2ManagedDir='/path/to/data_sts2_macos_arm64'
```

游戏引用是本机专有文件，因此仓库不提供 CI build。`Sts2LlmAgent.Core.csproj` 只用于无游戏依赖的核心测试；发布时这些源码会编译进单一的 `Sts2LlmAgent.dll`，安装目录不需要额外的 Core DLL。

Release 安装目录只需要三个文件：`Sts2LlmAgent.dll` 是 manifest 指定的主 mod，`Sts2LlmAgent.Core.dll` 是同目录的 BCL-only 依赖，`Sts2LlmAgent.json` 是 manifest。游戏专有 DLL 不复制到 mod 目录，也不进入仓库。
