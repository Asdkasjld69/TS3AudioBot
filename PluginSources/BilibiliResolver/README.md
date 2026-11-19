# BilibiliResolver

让 TS3AudioBot 直接播放 B 站视频的**音频轨**：把
`https://www.bilibili.com/video/BV1pM4y1v71j` 这样的地址丢给 `!play` 即可。

## 原理

不走网页解析，全部走 B 站 API（与 [snowflake-hangdudu/bili-downloader]
的做法一致，但只取音频、不合并视频）：

1. `GET /x/web-interface/view?bvid=` → `bvid` / `aid` / `pages[].cid` / `title`
2. `GET /x/player/playurl?avid=&cid=&qn=80&fnval=16&...` → DASH `dash.audio[]`
3. 选 AAC 轨里 id 最大的（`30280` 192k > `30232` 132k > `30216` 64k），
   `dash` 缺失时回退 `durl`
4. 得到带 `deadline` 签名的 `.m4s` 直链，交给 `FfmpegProducer` 用 ffmpeg 解码

`.m4s` 是单轨 fMP4（AAC-LC）封装，ffmpeg 直接识别为 `mov,mp4,m4a`，
**不需要**把视频轨合并回来。

## 为什么是 BotPlugin 而不是 Factory

TS3AudioBot 的 Factory 插件只被 `Load` 成 `Ready`，而 `Bot.cs` 启动时只自动
`Start` 三类：`BotPlugin` / `CorePlugin` / `Commands`。纯 Factory 因此必须每次
`!plugin load` 手动注册，**重启即失效**；`[plugins.load]` 表目前是空 TODO，不能用于
自动加载。

所以本插件是一个 `IBotPlugin`，在 `Initialize()` 里把内部解析器挂到
`ResourceResolver` 上，随 bot 自动装载；`Dispose()` 时摘除。

内部实现类 `BilibiliResolverImpl` 是 `internal`：`GetExportedTypes()` 只返回 public
类型，这样插件管理器的 Factory 计数保持为 0，不会因「一个文件含多个插件」被拒载。

启动日志应出现：

```
BilibiliResolverPlugin.Initialize Bilibili 解析器已注册：ResolverFor = bilibili
Bot..ctor Auto-started BotPlugin "BilibiliResolverPlugin (BotPlugin)"
```

> 日志能被写盘的前提：`NLog.config` 只放行 `TS3AudioBot.*` 与 `TSLib.*` 命名空间，
> 所以代码里用 `LogManager.GetLogger("TS3AudioBot.Plugins.BilibiliResolver")`，
> 而不是 `GetCurrentClassLogger()`。

## 关键前提：CDN 要求 Referer

B 站 CDN 对无 `Referer` 的请求返回 **403**（实测：仅带 Referer、无 Cookie 即 206）。
所以必须同时配置 ffmpeg 输入参数：

```toml
[tools.ffmpeg]
extra_input_args = "-headers \"Referer: https://www.bilibili.com/\" -user_agent \"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36\""
```

该参数会作用于所有网络输入；对本地文件与其它流无副作用（ffmpeg 忽略多余请求头）。
`ffmpeg` 收到的 argv 正确分割为 `-headers` / `Referer: https://www.bilibili.com/` 两个参数。

## 为什么不能让 MediaResolver 兜底

内置 `MediaResolver` 用**不带 Referer** 的 GET 验流，遇到 B 站直链必然 403。
本插件对 `bilibili.com/video/*` 返回 `MatchCertainty.Always`，在解析器竞争中
优先于 `OnlyIfLast` 的 `MediaResolver`。

## 设计要点

- **直链不入库**：URL 约 2 小时过期，`ResourceId` 只存 `bvid|cid`，每次播放重新解析。
- **游客 cookie 自动获取**：`/x/web-interface/view` 在缺少 `buvid3` 时会返回 412，
  插件首次调用时抓一次首页的 `Set-Cookie`，之后复用。
- **分 P**：`?p=5` 映射到 `pages[4].cid`，越界自动夹到最后一 P。
- **av 号**：`/video/av911998091` 同样支持，内部会归一成返回的 `bvid`。
- **封面**：实现 `IThumbnailResolver`，从 `view.pic` 取。

## 不支持

- 番剧 / PGC（需 `ep_id` 与 `/pgc/player/web/playurl`，且依赖账号权限）
- 收藏夹、UP 主空间批量（需要额外的分页接口）
- 杜比 / Hi-Res 音轨（需大会员，且最终都要重采样到 TS3 的 48kHz 双声道）

## 部署

```bash
cp BilibiliResolverPlugin.cs \
   ../../TS3AudioBot/bin/Release/netcoreapp3.1/plugin/
sudo chown teamspeak:teamspeak ../../TS3AudioBot/bin/Release/netcoreapp3.1/plugin/BilibiliResolverPlugin.cs
sudo systemctl restart ts3audiobot
```

插件目录由 `ts3audiobot.toml` 的 `[plugins] path` 指定（本机为 `plugin`，单数）。

## 让 codewhale 调用

`ts3ab-ctl` 与控制通道（ChatBridge 提供）已经能把播放请求转给 bot，
与聊天里的 `!play` 完全等效，因此 codewhale 用 bash 调用即可：

```bash
ts3ab-ctl play "https://www.bilibili.com/video/BV1vih16PEHZ"
ts3ab-ctl status
```

沙箱内同样可用：`ts3ab-cw-sandbox` 只读挂载 `/usr`（含 `/usr/local/bin/ts3ab-ctl`）、
读写挂载 `CODEWHALE_HOME`（含 `ctl.token`），且不隔离网络命名空间，
所以 `127.0.0.1:58914` 可达。以服务用户身份验证：

```bash
runuser -u teamspeak -- ts3ab-cw-sandbox /usr/local/bin/ts3ab-ctl status
```

## 验证

```bash
# 解析是否正常（不经过 TS3）
dotnet bin/Debug/net10.0/Test.dll "https://www.bilibili.com/video/BV1vih16PEHZ"

# 直链是否能解码
ffmpeg -headers "Referer: https://www.bilibili.com/" -i "<PlayUri>" -t 10 -f s16le -ac 2 -ar 48000 /tmp/x.pcm
```
