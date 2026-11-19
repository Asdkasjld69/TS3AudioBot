# Adaptive Volume Equalizer

替代原 `VolumeEqualizerPlugin` 的自适应音量均衡器插件。

单个文件 `AdaptiveVolumeEqualizerPlugin.cs` 即可部署：TS3AudioBot 会在启动扫描插件目录时用内置
Roslyn 现场编译 `.cs` 插件，因此**不需要单独构建**，也不会因为运行环境框架版本不同而失配。

## 它做什么

一个单段 RMS 响度归一化器（AGC），输出响度趋向一个**固定目标**，同时让**约束/控制类参数**
根据素材慢速自适应。

- **固定不变（规范）**：`target`（目标响度）。它永远不参与自适应。
- **可自适应（约束/控制）**：
  - 提升上限 `maxgain`：根据实测噪声底调整。噪声底越高，允许的提升越小，避免把底噪一起抬起来。
  - 释放时间 `release`：根据动态范围调整。动态范围越大，释放越慢，避免抽吸（pumping）。
- **每曲重置**：订阅 `PlayManager.BeforeResourceStarted`，每首曲目开始时清空自适应统计。

### 为什么 target 不参与自适应

归一化的本质是一个不变量：「无论输入是什么，输出都趋近 T」。如果 T 本身依赖输入，参照系就消失
了，系统在数学上不再是归一化器，甚至可能正反馈发散。而 `maxgain`/时间常数只改变「如何逼近
T」，不改变 T 本身，所以自适应它们是安全的——前提是**变化足够慢**，否则会从响度归一化漂移成
动态压缩（那会抹平曲内动态，同样违背初衷）。本实现把自适应的更新限速在每秒一次、带迟滞
（0.5 dB）和逐步逼近（0.25 系数），并把时间常数限制在 0.3–2.5 s。

## 命令

与原插件同名同权限（`cmd.equalizer.*`，已存在于 `rights.toml`）：

- `!equalizer` — 显示状态（开关、自适应、target、maxgain/有效 maxgain、当前增益）
- `!equalizer on` / `off` / `toggle`
- `!equalizer target <dBFS>` — 目标响度，范围 -30..0，默认 -16
- `!equalizer maxgain <dB>` — 提升上限（用户设的硬上限），范围 3..30，默认 15
- `!equalizer adaptive on|off|toggle` — 自适应参数开关
- `!equalizer reset` — 手动清空自适应统计

## 在管线中的位置

插在 `Player.StallCheckPipe` 与 `Player.VolumePipe` **之间**：

```
MergePipe -> TimePipe -> CheckActivePipe -> StallCheckPipe -> [Equalizer] -> VolumePipe -> EncoderPipe
```

必须在 `VolumePipe` 之前：否则归一化会把用户用 `!volume` 设的音量一并归一化掉。`Dispose` 时会把
管线接回原样。

## 部署（替代旧插件）

插件目录由 `ts3audiobot.toml` 的 `plugins.path` 指定（当前为 `plugin`，相对运行目录，
即 `TS3AudioBot/bin/Release/netcoreapp3.1/plugin/`）。

1. 复制源码到插件目录：

   ```
   cp AdaptiveVolumeEqualizerPlugin.cs /mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/plugin/
   ```

2. 停用旧插件（**非破坏性**，不删除文件）。`PluginManager` 会忽略带有 `<文件名>.ignore` 的插件：

   ```
   touch /mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/plugin/VolumeEqualizerPlugin.dll.ignore
   ```

   必须停用旧的：两个插件都会注册 `equalizer` 命令，同时启用会因命令冲突导致其中一个加载失败。

3. 让新插件生效：重启服务，或在聊天里 `!plugin load AdaptiveVolumeEqualizerPlugin.cs`。

## 可选：预编译成 DLL

若运行环境里 `.cs` 运行时编译不可用（例如内嵌 Roslyn 无法读取过新的程序集元数据），可用附带的
`AdaptiveVolumeEqualizer.csproj` 预编译：

```
dotnet build AdaptiveVolumeEqualizer.csproj -c Release -o out
cp out/AdaptiveVolumeEqualizerPlugin.dll <plugin 目录>/
```

该 csproj 通过 `BinDir` 属性引用已部署的 `TS3AudioBot.dll` / `TSLib.dll`，需要把 `BinDir`
指向实际的运行目录。

## 已验证 / 未验证

- 已验证：源码用 .NET SDK 针对已部署的 `TS3AudioBot.dll`/`TSLib.dll` 编译通过（0 警告 0 错误），
  即所有类型引用、方法签名、命令属性与 C# 8 语法均正确。
- 未验证：实际音频听感与自适应收敛效果（需要启动 bot 播放真实曲目）；`.cs` 运行时编译在本机
  环境是否可用（本机未启动 bot 实测）。
