# ChatBridge

让 TS3AudioBot 在聊天里只回应 `@<bot 昵称>` 的消息，回答由沙箱内的 codewhale 生成，
文件访问范围由内核 mount namespace 定死在 `/mystorage`。

## 组成

- `ChatBridgePlugin.cs` — 插件本体。负责触发匹配、限流、超时、输出清洗、分片回帖。
- `ts3ab-cw-sandbox` — bubblewrap 白名单挂载脚本，所有 codewhale 调用都经它进入。
  部署到 `/usr/local/bin/ts3ab-cw-sandbox`（见下方「部署」）。
- `ts3ab-ctl` — 沙箱内控制 bot 的客户端，通过插件里的本地控制通道执行。
  部署到 `/usr/local/bin/ts3ab-ctl`。
- `/var/lib/ts3ab-codewhale` — 独立的 `CODEWHALE_HOME`，存放 bot 专用的 API key、模型路由与会话记录。

### 部署

两个脚本以 root 属主安装到 `/usr/local/bin`，与仓库副本保持一致：

```bash
sudo install -o root -g root -m 755 \
  PluginSources/ChatBridge/ts3ab-cw-sandbox \
  PluginSources/ChatBridge/ts3ab-ctl \
  /usr/local/bin/
```

更新脚本后需要重装并重启，否则运行的仍是旧版本：

```bash
sudo systemctl restart ts3audiobot
```

`ts3ab-cw-sandbox` 依赖 `bwrap`；`ts3ab-ctl` 是纯 Python 3 标准库脚本，
只读 `/var/lib/ts3ab-codewhale/ctl.token` 并连接 `127.0.0.1:58914`。

插件只做调度，安全边界全部在进程外：

```
TS3 message → Bot(Ts3Client.OnMessageReceived) → ChatBridge
    → ts3ab-cw-sandbox (bwrap: 只挂 /mystorage + /usr + 少量 /etc 文件)
        → codewhale exec (workspace=/mystorage/audio, 工具 read/ls/grep/load_skill/bash)
    → 回复到原会话（私聊→私聊，频道→频道，服务器→服务器）
```

## 记忆库

bot 使用自己的记忆库，与主机上的 `memory.db` 物理隔离：

- 真实数据在 `/mystorage/ds_memory/ts3ab-bot/memory.db`，仅 bot 使用
- 沙箱内通过 bind 投影为 `$WORKDIR/.ts3ab-memory`（当前工作区是
  `/mystorage/audio`，即 `/mystorage/audio/.ts3ab-memory/memory.db`）
- venv 与模型缓存复用主机的 `/mystorage/ds_memory/{.venv,hf-cache}`（只读）
- 入口是 `/usr/local/bin/ts3ab-mem`，把解释器与 `--db` 都写死；
  `CODEWHALE_MEMORY_DB` 不会传进 codewhale 的 bash 工具，所以不能靠环境变量
- 脚本按「挂载点是否存在 memory.db」自动选路径：沙箱内走投影，宿主上走真实目录

### 为什么要投影

`--sandbox workspace-write` 只允许写 workspace 与 `/tmp`，而 `/mystorage`
整体是只读挂载，因此 **workspace 本身也不可写**。记忆库必须同时满足
「出现在 workspace 内」和「该子目录被 bind 成 rw」才能写入，所以有了这层投影。

代价是宿主机上多一个空挂载点目录 `/mystorage/audio/.ts3ab-memory`
（内含 README.txt 说明用途）。若沙箱未正确挂载，模型会看到它而不是记忆库。

主机自己的 `/mystorage/ds_memory/memory.db*`（含所有 `memory.db.bak-*` 备份）
在沙箱里全被绑定成 0 字节空文件，频道里任何人借 bot 之口都读不到你的私人记忆。

## 触发规则

- 昵称取 `bot.connect.name`，大小写不敏感。两种形态都支持：
  - 手动输入：`@Mybot`
  - 客户端补全：`<@431|Mybot>` 或 `<@Mybot>`（TS3 客户端会把 @ 改写这种标签）
- 补全标签本身已指明对象，因此不再要求尾部空白：`<@431|Mybot>歌单` 也能命中。
- 标签里的 clientId 与 `Book.OwnClient` **实时**比对，不缓存：服务器每次重连
  都会重新分配 clientId。`Book` 尚未同步时退回昵称匹配。
- 若标签无 id 或 id 未同步，退回昵称比较，并容忍服务器给重名加的 `" (2)"` 后缀。
- 裸 `@` 形态保留原有边界规则：前一个字符不能是字母数字
  （`a@Mybot.com` 不触发），后一个字符必须是行尾、空白或标点
  （`@Mybot2` 不触发）。
- 未命中的消息不调用模型，只记一行 DEBUG。
- **白名单**：复用 `rights.toml` 现有规则，不新增配置项。插件向权限系统注册
  四个 right，各自独立：
  - `cmd.chatbridge` —— 普通对话
  - `cmd.chatbridge.password` —— 索取上传口令
  - `cmd.chatbridge.playlist` —— 歌单写操作
  - `cmd.chatbridge.send` —— 代发私信

  能聊天不等于能拿口令、改歌单或以 bot 名义发消息；需要哪项就在规则里
  显式列出哪个 right。
- 未授权的 `@` 消息一律静默丢弃（不回复、不记 INFO）。
- 每人 5 秒冷却；同时只跑 1 个请求；超出时最多回 3 次“正忙”。

## 播放控制

沙箱内的模型能完整控制播放。它的命令经本地控制通道转给插件，
插件再调 bot 自己的管理器（`PlayManager` / `Player` / `PlaylistManager`），
与聊天里的 `!play` / `!pause` 走同一套逻辑，**不直改任何状态文件**。

外部接口是 `/usr/local/bin/ts3ab-ctl`：

```
ts3ab-ctl status | play <链接> | queue <链接>
ts3ab-ctl pause | resume | stop | next | prev
ts3ab-ctl seek <秒> | volume 60 | volume +10
ts3ab-ctl playlist play|queue <歌单名>
ts3ab-ctl send <目标> <内容>
ts3ab-ctl send <目标> -          # 正文从 stdin 读，逐字保真
```

`send` 让模型能以 bot 的身份给指定客户端发私信，目标可以是昵称、
`#clientId` 或 `uid:<uid>`；带空格的昵称用双引号包起来。正文不做任何转义、
截断或过滤 —— 用 `-` 从 stdin 读时，空白与换行原样保留；直接写在命令行上
则会被 shell 分词。与聊天里的「私信」指令等效。

按昵称查找依赖 `b_virtualserver_client_list`。bot 身份常见没有这个权限，
所以 `#clientId` 与 `uid:` 两条路走 `clientinfo` / `clientgetids` 绕开它；
昵称另有一层缓存兜底（记住在频道里说过话的人，用 uid 反查），
进程重启后缓存清空，只覆盖重启后开过口的人。要彻底解决得在服务器上
给 bot 的 ServerQuery 身份授予该权限。

### 通道与边界

- 插件监听 `127.0.0.1:58914`，**只绑回环**，不对外。
- 每次启动重新生成 32 字节随机 token，写入
  `/var/lib/ts3ab-codewhale/ctl.token`（`0600`，属主 `teamspeak`）。
  沙箱内进程以同一用户运行，所以能读；宿主上其它非特权进程读不到。
- 比较用 `CryptographicOperations.FixedTimeEquals`，防时序侧信道。
- 需要碰播放器的调用都过 `DedicatedTaskScheduler`，与命令系统线程模型一致。
- 插件停用（热重载或退出）时关监听并删掉 token 文件。

风险：这是一条从沙箱到 bot 进程的真实控制通道。模型看得到 token，
所以“模型自身”完全被信任；隔离只针对宿主上其它进程。

## 上传口令分发

匿名聊天站点的音频上传口令（`/mystorage/git-repos/anonymous-chat/server/.audio_password`，
32 位小写十六进制，每小时整点轮换）由插件直接分发，**不经过模型**。

触发：频道/服务器/私聊里发送以下之一（`@` 前缀在公共场景必需）：

```
@Mybot 上传口令
@Mybot 口令
@Mybot 给我上传口令
```

行为：

- 插件读文件 → **私信**发给发起人 → 公共场景只回一句“已私信发给你”。
- 口令不会出现在频道消息里，也不会进入模型上下文或会话记录（这条路径不调 codewhale）。
- 权限 `cmd.chatbridge.password`，已在 `rights.toml` 的 ChatBridge 规则里授予；
  未授权者连回复都收不到（静默丢弃）。
- 每人 60 秒冷却；文件读不到或格式不对时回“现在拿不到上传口令”，只记 WARN，不记口令内容。

### 为什么是插件而不是 skill

skill 只能影响模型说什么，**没有发私信的能力**；而模型在频道里作答就等于
把口令公开发出去。因此分发做成插件机制，skill（`upload-password`）只负责
告诉模型这件事存在、该怎么引导用户，并明确禁止模型读取或复述口令。

机制层还有一道：`ts3ab-cw-sandbox` 把 `.audio_password` 与它的盐值
`server/.config.js` 在沙箱里 **`--ro-bind` 成 0 字节**。模型即使被诱导去
`cat` 也拿不到内容，且插件（沙箱外、`teamspeak` 身份）仍能正常读取。

## 代发私信

以 bot 身份给指定客户端发私信。有两个入口，行为一致：

**入口一：聊天指令**（人发的）

```
@Mybot 私信 张三 你好
@Mybot 私信 "张 三" 你好
@Mybot 私信 #431 你好
@Mybot 私信 uid:xwq9ZUcMGDIZjovmDOqKkI6i3RI= 你好
```

别名：`发消息`、`发送消息`、`msg`、`dm`。

**入口二：控制通道**（模型发的）

```bash
ts3ab-ctl send 张三 你好
ts3ab-ctl send 张三 - <<'EOF'
第一行
第二行
EOF
```

正文**不转义、不截断、不过滤**：从分隔符之后逐字保留。控制通道这条特意排在
`MaxPromptChars` 截断之前，否则超过 800 字的内容会被切掉。用 `-` 从 stdin 读时，
空白与换行也原样保留；直接写在命令行上则会被 shell 分词（连续空格合并）。

超出 TS3 协议消息上限时服务器拒收，调用方会收到「发送失败」，而不是被静默截短。

### 目标解析

- `#<clientId>` —— `clientinfo` 单查。clientId 每次重连会变，只适合即时使用。
- `uid:<uid>` —— `clientgetids` 单查。uid 跨重连稳定，是长期引用该用的。
- 昵称 —— 需要 `b_virtualserver_client_list`。bot 身份常常没有这个权限，
  所以另有一层缓存兜底：插件记住在频道里说过话的人（昵称→uid），
  按 uid 反查。缓存随进程重启清空，只覆盖重启后开过口的人；查不到时
  错误信息会建议改用 `#clientId` 或 `uid:`。

要彻底解决昵称查找，需在 TS3 服务器上给 bot 的 ServerQuery 身份授予
`b_virtualserver_client_list`。

### 权限

- 聊天指令：right `cmd.chatbridge.send`，按发信人 uid 判。
- 控制通道：无额外 right，靠 token + 只绑回环。控制通道那条不叠用户权限，
  是因为能进沙箱发命令的就是模型自身，而它只在已授权用户的对话里运行；
  再加一层也无法定位到具体发信人。

## 会话模型

插件把 codewhale 的会话（`--resume`）按两种维度分开：

- **私聊**：key 为 `dm:<uid>`，每个用户一条独立会话，互不可见。空闲 24 小时
  （与会话文件清理周期一致）后失效，下一条消息重新建会话。
- **公共聊天**：频道消息与服务器消息共用一条会话，key 为 `public`，
  **6 小时**后重置。

### 重置倒计时的起点

窗口全部由消息驱动，没有任何定时任务或固定时间点：

- 公共会话的 6 小时从**会话建立成功的那条消息**开始计——不是进程启动时刻，
  也不是整点。到期后不会自动换，而是等下一条消息到来时才重建会话，
  新窗口从这条消息重新起算。
- 没有活跃会话（`Id` 为空）时不做任何时间判定。因此如果第一条请求失败或超时、
  没有拿到 session id，这一轮不会消耗窗口；下一个成功建立会话的消息才是起点。
- 续接同一个 session id 不会重置起点，否则窗口会退化成永不重置。
  只有在拿到**不同**的 id（重建或回退新建）时才重新计时。
- 私聊不是固定窗口，而是按最后一条消息后的空闲时间计 24 小时；
  每次成功交互都会把时间后推。
- 插件重启后内存中的会话表清空（见下），已建立的会话虽然文件还在，
  但窗口断点也会重置。

会话 id 由 codewhale 在 **stderr** 的 `session: <id>` 行给出（恢复时还会先说
`resumed session: <id>`；stdout 只有回答正文）。插件在回帖前先把 id 解析下来，
再把这类行从输出里清掉，不显示给用户。
会话文件被 24 小时清理任务删掉、或 codewhale 重启后 id 失效时，
插件会捕获 `No session found` 并自动回退成新建会话重问一次。

### 公共会话里怎么区分发言人

公共会话是多人共享的上下文，所以每条 prompt 都带一行发言者标注：

```
[发言者] 昵称（uid 前 8 位…，来自频道）
问题：……
```

对应的策略（`PublicPolicy`）要求模型：只回答最后一条「问题」，不替其他人
作答，不把上一位发言者的陈述当成当前问题，**不得复述、总结或透露其他
发言者说过的任何内容**，回答只针对当前发言者。私聊用 `PrivatePolicy`，
仍然限定「只回答最后一条问题」，但允许回答到 200 字（公共 120 字）。

私聊用 `PrivatePolicy`，
仍然限定「只回答最后一条问题」，但允许回答到 200 字（公共 120 字）。

公共频道的记忆**不再禁止**：已改为按发言者分区，见下一节。

续接已有会话时不再重复整段策略（历史里已有），只带一句提醒，避免策略
在会话历史里反复堆积。

## 记忆持久化与分区隔离

私聊和公共频道都能记忆，**按发言者分区**，分区的强制点在脚本层而不是提示词。

### 数据流

```
插件每轮写入当前 uid          → /mystorage/ds_memory/ts3ab-bot/.speaker
                                  ↓ bwrap bind
沙箱内 ts3ab-mem 读 .speaker  → /mystorage/audio/.ts3ab-memory/.speaker
                                  ↓
            mem.py --scope-uid <uid> 强制改写 project
```

### 强制规则（mem.py 的 `_apply_scope`）

- `add`：`project` 不是 `chat:shared` 时，**无条件改写**为 `chat:<uid8>`。
  模型自己传 `-p ts3ab` 或 `-p chat:别人` 都会被覆盖。
- `search` / `list` / `pending`：**无条件**设为只看 `chat:<uid8>` + `chat:shared`，
  忽略模型的 `-p` / `--project-in`。
- 唯一的跨用户可见写入是显式 `-p chat:shared`（“记给大家”）。
- 没有 `.speaker`（宿主上手工使用）时完全不加约束，行为与以前一致。

实测：模型传 `-p ts3ab` 与 `-p chat:BOBXXXXX` 均被改写到 `chat:+73u2THJ`；
另一个 uid 搜不到该条目，但能搜到 `chat:shared` 的条目。

### 状态与确认

`add` 默认写 `pending`，pending 不入检索。模型在用户说“记住/以后”时应加
`--status confirmed`，否则那条记忆下一次搜不到。这是 skill 既有语义，
未作修改。

### 并发与残留

`.speaker` 是单文件，靠 `MaxConcurrent = 1` 保证不串台；如果将来提高并发，
必须改成按请求传递（例如临时目录 + 每请求唯一文件名）。

插件重启后内存中的会话表清空，所有人都会从新会话开始。

## 会话重置时的自动精炼

每次会话重置前，插件会把本轮尚未持久化的问答压缩成长期记忆，
再换新会话。这是用户明确要求的自动机制，**不受 memory-persistence skill
「先问再存」约束**——但只作用于本轮对话提炼出的条目，不会修改既有记忆。

### 触发点

- 公共会话满 6 小时（下一条消息触发重建时）
- 私聊空闲满 24 小时
- codewhale 报 `No session found`、插件回退成新建会话时
- 插件停用或热重载（best-effort，进程直接退出时可能来不及）

`PruneSessions` 回收过期会话时也会先精炼再移除。

### 数据来源与边界

插件在内存里按会话累积**成功的**一轮问答（问题 + 回答），不读 codewhale
的 session 文件。重置时只取最近 30 轮、最多 12000 字符；轮次缓冲有 200 条
防御上限，防止异常长窗口把内存吃满。

精炼本身是**一次无工具的 one-shot** `codewhale exec`：模型只输出条目正文，
不调用工具、不保存 codewhale 会话。所以它不会与主对话争用 `.speaker`，
也不会在 `sessions/` 里留下额外记录。

### 写入方式

模型输出按行解析（去掉编号/项目符号，最多 5 条，每行截到 200 字），
再由插件**直接调 `mem.py`** 写库：

```
mem.py --db /mystorage/ds_memory/ts3ab-bot/memory.db \
       --scope-uid <完整 uid> add -c <条目> \
       -p chat:<uid8> -k note --tags auto-distill \
       --source chatbridge-distill --status confirmed
```

为什么不经 `ts3ab-mem`：那个 wrapper 读 `.speaker`，而精炼是后台任务，
主对话可能正在处理另一位发言者，单文件会串台。这里显式传 `--scope-uid`，
分区强制仍在 `mem.py` 的 `_apply_scope` 里完成（传错 `-p` 也会被改写成本人分区）。

- 自动条目是 `confirmed`（否则不进检索），并统一带 `auto-distill` tag，
  需要批量清理时可以按 tag 筛出来。
- 公共会话按发言者分组，每人的记录只进 `chat:<uid8>`；一次重置最多处理
  轮数最多的 8 位发言者，避免打出几十个模型请求。
- 用户明确说「不要记住」的段落由精炼提示词要求跳过。这是软约束，
  与其它提示词约束同级；真正的分区保证仍在脚本层。

### 失败与成本

精炼任务在后台串行执行，不占用对话闸门，也不拖慢触发重置的那条消息。
模型调用或写库失败只记 WARN，不重试、不影响正常问答；未写入的轮次不会
自动补做。

每次重置最多多出「发言者数」次模型调用（通常 1 次，公共会话上限 8 次）
和同样数量的 `mem.py` 写库进程。私聊内容会随精炼再次发给模型，
与正常对话走同一套 API 和密钥；写入仍只落在本人分区。

## 回复内容约束

聊天端不主动暴露文件系统。策略写在每轮 prompt 的前缀里（`CommonPolicy` +
`PrivatePolicy`/`PublicPolicy`）：

- 声明工作范围是 `/mystorage/git-repos/TS3AudioBot`，
  并点名 `/mystorage` 下其它目录（docker、www、hids_log、backup）与对话无关
- 禁止罗列目录清单、目录结构、绝对路径
- 禁止粘贴整段文件内容、大段代码、命令输出原文

**这是软约束，不是沙箱边界。** 实测行为：

- 问「这个项目是做什么的」→ 正常介绍项目，不泄露 `/mystorage`
- 问「列一下 /mystorage 下有哪些目录」→ **仍会如实列出**

关键在「声明工作范围」而不是「禁止罗列」：只说"不要罗列当前目录"时，
模型仍把 `/mystorage` 当项目介绍并把目录全列出来；改成明确"你在哪个仓库工作"后，
默认视角才对。若要真正堵死，只有两条路：把 `/mystorage` 从挂载中移除，
或去掉 `ls`/`grep`/`bash` 工具。

## 自建技能

bot 可以在聊天里创建新 skill，且只影响自己这套 codewhale 环境：

- 落地目录：`/mystorage/audio/.ts3ab-skills`（workspace 一级子目录）
- 发现方式：`CODEWHALE_HOME` 的 `skills_dir` 指向该目录
- 沙箱内 bind 成 rw，模型用 `bash` 写入，`load_skill` 立即可加载

内置的 52 个 skill 已复制进去（含新增的 `upload-password`、`playlists`、`bot-control`），属主 `root` 权限 755——模型可读不可改，
只能新建自己的技能。这样既保留 `weather`/`memory-persistence` 等内置能力，
又不会让聊天内容改写它们。

隔离性：`skills_dir` 只写在 `/var/lib/ts3ab-codewhale/config.toml`，
主机上你自己的 codewhale 用另一个 `CODEWHALE_HOME`，完全看不到这些技能。

### 歌单读取（`playlists`）

歌单是运行时 bots 目录下的文件，文件名即歌单名，脚本只读展开：

```
python3 .../playlists/scripts/list_playlists.py          # 列全部（名称 + 首数）
python3 .../playlists/scripts/list_playlists.py <名字>   # 展开曲目，可加 -n 限量
```

脚本处理了几个实际坑：曲名带 BOM（去掉了）、标题为 null 的网络电台
（显示成“网络电台（域名）”而非一大串 query string）、空歌单如实报空。
路径穿越（`../bot.toml`）被拒。播放状态走 `bot-control` 的 `status`；
它只给当前曲目，不给来源歌单，所以“当前播的是哪个歌单”要靠曲名反查。

### 为什么绕了这几步

三个看似更直接的方案都被运行时拒绝，逐条实测：

- `<workspace>/.codewhale/skills`：codewhale 硬性保护该路径，即使 bind 成 rw
  仍 `EROFS`
- 把 `.codewhale/skills` 做成指向别处的符号链接：
  `Refusing symlinked path: workspace skills must stay inside the workspace`
- `<workspace>/skills`（skill-creator 文档列出的路径）：运行时不收录，
  `load_skill` 找不到

另外 `skills_dir` 会**替换**默认发现路径，不自带内置技能——所以必须把
`~/.codewhale/skills/*` 一并复制进去，否则 `weather` 等会整体消失。

### 安全代价

写入 `bash` 就能写技能目录，意味着频道里被授权的人可以让 bot 落一个
持久化的提示注入载体。缓解措施只有两条，都是流程性的：

- 内置技能只读（`root` 属主），改不了核心能力
- `SKILL.md` 里写明新建的技能需要用户明确要求

真要硬隔离，得撤掉 `bash` 或把技能目录改成只由宿主侧人工投放。

## 清理记忆：初始状态 ≠ 清空

`/usr/local/bin/ts3ab-mem-reset` 把 bot 记忆库恢复到「刚初始化」的形态：

```
备份现有库 → 删除 memory.db/-wal/-shm → mem.py 重建 schema → 校验 → 失败自动回滚
```

语义差别（这是脚本存在的全部理由）：

- `rm memory.db` → 留 0 字节文件，下次打开是没有 schema 的野库
- `DELETE FROM memory_blocks` → 表在，但 FTS5 的 shadow 段（`memory_fts_data`/`_idx`）残留旧状态
- `ts3ab-mem-reset` → 与首次运行逐位一致：8 张表、`user_version=1`、各表 0 行

校验失败（表缺失、数据非空、`user_version` 异常）会自动从 `/tmp` 备份回滚，
退出码 70；目录不可写时退出码 73 并明确保留备份；找不到解释器退出码 69。

一个踩过的坑：脚本内部调用 `mem.py` **必须显式传 `--db`**。
codewhale 的 `bash` 工具不继承 `CODEWHALE_MEMORY_DB`，靠环境变量会让 `mem.py`
回落到被遮蔽的宿主库，报 `attempt to write a readonly database`。

## 思考态头像

思考期间头像换成内嵌的「三个点」提示图，回复发出后交回 bot 自己的逻辑恢复：

- 思考中 → `ts3.UploadAvatar(ThinkingAvatar)`（内嵌 200x200 PNG，不依赖外部文件）
- 结束后 → `bot.GenerateStatusImage(playManager.IsPlaying, CurrentPlayData)`
  —— 播放中恢复曲目封面，未播放恢复休眠图，与「插件从未介入」时完全一致

两个实现要点：

- 上传走 `SendCommandBase`，必须在 `DedicatedTaskScheduler` 上执行，否则抛
  `TaskSchedulerException`；恢复由 `Bot.GenerateStatusImage` 自己调度。
- 恢复放在 `finally` 里，异常和超时都不会让头像卡在思考态；
  且排在 `Reply` 之后，避免「头像先恢复、消息后出现」的顺序错觉。

服务器对头像上传有反洪水限制。当前每天花板是 5 秒冷却 + 并发 1，
即最多每 5 秒两次上传，不会触顶。

## 边界（内核强制，不靠提示词）

`ts3ab-cw-sandbox` 内的进程只能看到：

- `/mystorage` —— 只读挂载（`TS3AB_CW_RW=1` 才可写）
- `/usr`、`/etc/pki`、`/etc/ssl`、`/etc/resolv.conf`、`/etc/hosts` 等运行时依赖 —— 只读
- `/var/lib/ts3ab-codewhale` —— codewhale 私有 HOME，可写
- `/tmp` —— 临时 tmpfs

`/root`、`/home`、`/var/log`、宿主其余挂载点在这个命名空间里根本不存在。
`/mystorage/swapfile` 额外用 `/dev/null` 遮蔽，模型既读不到也写不了。

以下内容在 `/mystorage` 可读的前提下额外 `--ro-bind` 成 0 字节：

- TS3 身份私钥：`bots/*/bot.toml` 与 `ts3audiobot.toml`。**仓库副本和运行时
  `bin/*/netcoreapp*/` 下的副本都要遮蔽**——后者才带真私钥
  （仓库里那份的 key 是空的）。这一条最初只盖了仓库路径，
  是后来发现运行时 bot.toml 在沙箱内可读（1263 字节，含 `connect.identity.key`）
  才补上的。
- 音频上传口令 `.audio_password` 与其盐值 `.config.js`。
- 主机自己的 `memory.db*`。

歌单目录**不遮蔽**：`playlists` skill 需要读它。歌单文件里只有曲目元数据，
不含凭据。

codewhale 自身还有第二层：`--sandbox workspace-write` + `--allowed-tools read,ls,grep,load_skill,bash`。
`write`/`edit` 不下放；`bash` 虽能间接写文件，但写入仍受第一层挂载约束。

## 运维

改触发词：改 `bots/default/bot.toml` 里的 `[connect] name`，然后
`systemctl restart ts3audiobot`。

检查 `/usr/local/bin` 里装的脚本是否与仓库一致：

```bash
diff PluginSources/ChatBridge/ts3ab-cw-sandbox /usr/local/bin/ts3ab-cw-sandbox
diff PluginSources/ChatBridge/ts3ab-ctl       /usr/local/bin/ts3ab-ctl
```

无输出即一致。不一致就按「部署」一节重装并重启。

轮换 API key：

```bash
printf '%s' 'NEW_KEY' | runuser -u teamspeak -- \
  env CODEWHALE_HOME=/var/lib/ts3ab-codewhale \
  /usr/local/bin/codewhale auth set --provider deepseek --api-key-stdin
```

换模型：

```bash
runuser -u teamspeak -- env CODEWHALE_HOME=/var/lib/ts3ab-codewhale \
  /usr/local/bin/codewhale config set default_text_model deepseek-v4-flash
```

手工进沙箱排查：

```bash
runuser -u teamspeak -- /usr/local/bin/ts3ab-cw-sandbox /bin/sh
runuser -u teamspeak -- /usr/local/bin/ts3ab-cw-sandbox \
  /usr/local/bin/codewhale exec --auto --allowed-tools read,ls,bash "列一下 /mystorage"
```

查看/维护 bot 记忆库（在沙箱内执行，环境变量已由脚本注入）：

```bash
runuser -u teamspeak -- /usr/local/bin/ts3ab-cw-sandbox /bin/sh -c '
  PY=/mystorage/ds_memory/.venv/bin/python
  MEM=/var/lib/ts3ab-codewhale/skills/memory-persistence/scripts/mem.py
  "$PY" "$MEM" stats
  "$PY" "$MEM" pending
'
```

备份 bot 记忆库（沙箱外直接拷）：

```bash
cp /mystorage/ds_memory/ts3ab-bot/memory.db \
   /mystorage/ds_memory/ts3ab-bot/memory.db.bak-$(date +%Y%m%d)
```

看插件日志：

```bash
journalctl -u ts3audiobot -f | grep -i chatbridge
```

## 头像上传（封面 / 思考态）

TS3 服务端用 `i_client_max_avatar_filesize` 卡头像大小，超了回
`permission_invalid_size`。改动前封面缩略图常有 200KB+，几乎必然失败，
日志里是反复的 `Could not change avatar`。

现在的处理在 `Ts3Client.UploadAvatar`，所有头像路径（封面、思考态、
`!bot avatar set`）共用：

1. 尝试查服务器上限（`permoverview`），成功则缓存 10 分钟。
2. 小于 32KB 的图（思考态那 813 字节的三点图）直接原样上传，
   不重编码——重编码反而更大更糊。
3. 大图按 `320→240→180→128→96→64` 宽 × JPEG 质量 `75→60→45` 生成降级序列，
   逐个上传，第一个成功即止；只有"大小不合法"才继续降级。
4. 全失败才抛错，错误信息带最后一档的大小与服务器返回。

实测：封面 `234123 → 39518` 字节一次通过，连续切歌不再出现失败。

两个已知前提：

- **查不到上限时按未知处理**（先压缩、失败降级），不影响功能。
  本服务器 bot 缺 `b_client_permissionoverview_view`，查询恒失败；
  日志里会显示"上限 未知"。补权限需要服务器管理员授权。
- 降级判据必须看错误**消息**而不是 `CommandError.Id`：TSLib 把底层
  `permission_invalid_size` 包成了 `custom_error`，按 Id 判断会漏。
- 未修复的相邻问题：bot 还缺 `b_client_modify_own_description`，
  客户端描述（状态文字）更新会失败；与头像无关，需单独授权。

## 已知限制与风险
- **上传口令是“拿到就能用”的凭证**：被授予 `cmd.chatbridge.password` 的人可随时
  私信索取，拿到后也能转发给任何人，插件无法限制后续传播。口令最长 1 小时内
  有效（整点轮换），泄露窗口就是这个小时。要立即全量失效，只能改
  `server/.config.js` 的盐值并重启 `chat` 服务。
- 请求者在频道中索要时，频道里其他人能看到“有人要了口令”这件事，但看不到内容。
  若这本身也不能暴露，需要改成只认私聊触发。
- 工具白名单是 `read,ls,grep,load_skill,bash`。`write`/`edit` 未下放，
  但 `bash` 本身就能写文件——真正的边界是 bwrap 挂载，不是工具名。
- **网络是通的**。天气查询依赖这一点，但代价是沙箱内的 bash 能访问
  127.0.0.1 与内网其它主机（实测本机 22 端口可连）。bot 自己的 WebAPI
  有权限系统挡着（`/api/bot/list` 返回 403 MissingRights），但这不构成隔离。
  若这个 bot 所在网络里有其它无认证服务，需要考虑在防火墙层面按
  `teamspeak` 用户限制出站。
- venv 与 hf-cache 是只读共享。模型已缓存，因此实测可用；若将来换模型名
  或清空 hf-cache，sentence_transformers 会因无法写入而失败，
  此时会降级到 hashed 后端（检索质量下降，但不报错）。
- 沙箱内 `$HOME`（`/var/lib/ts3ab-codewhale`）是 **可写** 的（codewhale 要在
  里面写会话与缓存），因此 `bash` 能读到 `sessions/` 里的历史记录，
  包括其他用户的私聊会话。策略里已禁止模型读取该目录，但这是软约束；
  要做到硬隔离，需要按用户拆分 `CODEWHALE_HOME`，或把会话目录换成
  模型不可见的存储。公共会话本身也是全员共享上下文：模型可能把 A 的
  提问带给 B，`PublicPolicy` 只能要求它不复述，不能强制。
- **记忆分区也有绕过途径**：模型能 `ls /mystorage/ds_memory/ts3ab-bot`，
  但那是宿主真实目录，沙箱里看到的是 `.ts3ab-memory` 映射；它可以绕过
  `ts3ab-mem` 直接用 `bash` + venv python 调 `mem.py --db ... search`
  并自行传 `--db`、不传 `--scope-uid`，从而读到全库（含别人分区）。
  分区保证只在“走 `ts3ab-mem`”时成立。要硬堵，需要把 memory.db 对 bash
  不可直接读（例如只给 wrapper 一个受限的 sudo 入口），代价较大。
  当前评估：`.speaker` 只暴露当前发言者 uid，库里其它 uid 的条目需要
  模型主动越权构造命令，属于可审计的违规行为（会进会话记录）。
- 可写位置：`/mystorage/ds_memory/ts3ab-bot`、`/mystorage/audio/.ts3ab-skills`、
  以及 `$HOME` 与 `/tmp`。
- 白名单是 **fail-closed** 的：`rights.toml` 缺失、解析失败，或没有任何规则
  授予 `cmd.chatbridge` 时，bot 不会回复任何人（而不是回退成对所有人开放）。
  想让某个人能用，在其规则里加 `"cmd.chatbridge"` 或确保规则带 `"*"`。
- 白名单匹配只看 `useruid`。插件不会为每次消息去查 server group，
  因此只按 `groupid` 授权的规则对本插件不生效。

- `/usr/local/bin/codewhale` 是 `/root/.local/bin/codewhale` 的**副本**（`/root` 对
  `teamspeak` 用户不可穿越）。执行 `codewhale update` 后必须重新复制：
  `install -m 0755 /root/.local/bin/codewhale /usr/local/bin/codewhale`。
- 沙箱进程在自己的 PID 命名空间里，codewhale 的 daemon 单例记录可能留下陈旧 owner。
  本方案每次用 `exec` 子进程、不常驻 `serve`，因此不触发该问题；若将来改用 `serve`，
  启动前需清理 `/var/lib/ts3ab-codewhale/run/daemon.sock*` 与 `tasks/execution-owners/`。
- 回复内容会经受洗（去掉代码围栏、`**`、`#` 标题），每块 ≤900 字节。
  模型仍可能输出不适合聊天的长文，`MaxPromptChars`/提示前缀是第一道收敛手段。
- 聊天文本会原样进入模型上下文。工具虽为只读，仍可能读到 `/mystorage` 里不该公开的文件
  （`.env`、密钥、备份）。若这个目录里存在这类文件，应把它们排除出挂载范围，
  而不是依赖模型自觉。
