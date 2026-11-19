# TS3AudioBot 部署与路径说明

本文件记录本机 TS3AudioBot 的部署方式、可执行路径与运维命令。

## 服务

- **服务名**：`ts3audiobot.service`
- **Unit 文件**：`/etc/systemd/system/ts3audiobot.service`
- **启动方式**：systemd，`Type=simple`
- **运行用户**：`teamspeak`（组 `teamspeak`）
- **停止信号**：`SIGINT`
- **重启策略**：`Restart=on-failure`，`RestartSec=10`

Unit 关键内容：

```ini
[Service]
Type=simple
User=teamspeak
Group=teamspeak
KillSignal=SIGINT
Restart=on-failure
RestartSec=10
WorkingDirectory=/mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/
ExecStart=/usr/bin/dotnet /mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/TS3AudioBot.dll
```

## 路径

- **运行目录（WorkingDirectory）**：`/mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/`
- **可执行入口**：`/usr/bin/dotnet /mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/TS3AudioBot.dll`
  （入口是 `TS3AudioBot.dll`，由系统 `dotnet` 运行，不是原生 ELF 可执行文件）
- **主程序集**：`/mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/TS3AudioBot.dll`
- **核心库**：`.../bin/Release/netcoreapp3.1/TSLib.dll`
- **源码仓库**：`/mystorage/git-repos/TS3AudioBot/`
- **主配置**：`.../bin/Release/netcoreapp3.1/ts3audiobot.toml`
- **权限配置**：`.../bin/Release/netcoreapp3.1/rights.toml`
- **插件目录**：`.../bin/Release/netcoreapp3.1/plugin/`（对应配置 `[plugins] path = "plugin"`）
- **日志目录**：`.../bin/Release/netcoreapp3.1/logs/`
- **Bot 实例配置**：`.../bin/Release/netcoreapp3.1/bots/<name>/bot.toml`
- **ffmpeg**：`.../bin/Release/netcoreapp3.1/ffmpeg-git-20240629-amd64-static/`

## 运行环境

- 目标框架：`netcoreapp3.1`（`TS3AudioBot.csproj`），并设置 `<RollForward>Major</RollForward>`
- 本机已安装运行时：**.NET 10**（`/usr/lib64/dotnet/shared/Microsoft.NETCore.App/10.0.9`）
- 即：主程序按 `netcoreapp3.1` 编译，实际跑在 .NET 10 运行时上（Major roll-forward）

## 常用运维命令

```bash
# 状态 / 启动 / 停止 / 重启
systemctl status  ts3audiobot.service
systemctl start   ts3audiobot.service
systemctl stop    ts3audiobot.service
systemctl restart ts3audiobot.service

# 实时日志（NLog 同时写 logs/ 目录）
journalctl -u ts3audiobot.service -f
tail -f /mystorage/git-repos/TS3AudioBot/TS3AudioBot/bin/Release/netcoreapp3.1/logs/*.log

# 修改 unit 后重载
systemctl daemon-reload
```

## 插件部署

插件目录：`.../bin/Release/netcoreapp3.1/plugin/`。

- 支持 `.cs`（启动时用内置 Roslyn 现场编译）与 `.dll`/`.exe`。
- 停用某个插件（非破坏性）：在同目录创建同名 `.ignore` 文件，例如 `Foo.dll.ignore`。
- 命令冲突：两个插件注册同一命令会导致其中一个加载失败，切换插件时必须先停用旧的。

### 当前已部署的自适应均衡器插件

- **插件源码（仓库内）**：`/mystorage/git-repos/TS3AudioBot/PluginSources/AdaptiveVolumeEqualizer/`
  - `AdaptiveVolumeEqualizerPlugin.cs` — 插件本体
  - `README.md` — 设计与部署说明
  - `AdaptiveVolumeEqualizer.csproj` — 可选的预编译项目
- **已部署到插件目录的文件**：`.../plugin/AdaptiveVolumeEqualizerPlugin.cs`
- **已停用的旧插件**：`.../plugin/VolumeEqualizerPlugin.dll.ignore`（原 `VolumeEqualizerPlugin.dll` 保留未删）
- **命令**：`!equalizer`（on/off/toggle/target/maxgain/adaptive/reset），权限前缀 `cmd.equalizer.*`（已在 `rights.toml`）

> 注意：改动插件后需 `systemctl restart ts3audiobot.service`，或在聊天中用 `!plugin load <文件名>` 即时加载。
