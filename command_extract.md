# TS3AudioBot 命令速查表

- 来源: `TS3AudioBot/MainCommands.cs`（共 168 条命令）
- 插件命令: 由插件在运行时注册（如 `equalizer` 来自 `VolumeEqualizerPlugin`），不在上述 168 条内
- 符号约定: `[...]` 可选参数；`<...>` 必填参数；`(a|b)` 二选一；`...` 可变参数
- 使用方式: 在聊天中发送 `!` 前缀的命令，例如 `!list add <listId> <url>`

## add
- `!add <url>` — 将新歌曲添加到播放队列

## alias
- `!alias add <commandName> <command>` — 创建新的命令别名
- `!alias remove <commandName>` — 删除已有的别名
- `!alias list` — 列出所有已注册的别名
- `!alias show <commandName>` — 显示别名的实现

## api
- `!api token [<duration>]` — 生成一个 API 令牌（duration 为令牌有效时长，单位小时）

## bot
- `!bot avatar set <url>` — 为机器人设置头像
- `!bot avatar clear` — 清除机器人的头像
- `!bot badges <badges>` — 为机器人设置徽章（徽章字符串以 `overwolf=0:badges=` 开头）
- `!bot description set <description>` — 设置机器人的描述
- `!bot diagnose` — *猜测*：运行机器人自检
- `!bot disconnect` — 停止当前机器人实例
- `!bot commander` — 查看频道指挥官模式的状态
- `!bot commander on` — 启用频道指挥官
- `!bot commander off` — 禁用频道指挥官
- `!bot come [<password>]` — 将机器人移动到你的频道
- `!bot connect template <name>` — 使用指定模板连接一个新机器人
- `!bot connect to <address> [<password>]` — 将一个新机器人连接到指定地址
- `!bot info [<template>]` — 获取机器人相关信息（可指定模板名）
- `!bot list` — 获取所有活动机器人的列表
- `!bot move <channel> [<password>]` — 将机器人移动到指定频道
- `!bot name <name>` — 为机器人设置新名称
- `!bot save <name>` — 保存新连接机器人的配置
- `!bot setup [<adminToken>]` — 设置机器人的全部 TeamSpeak 权限
- `!bot template <botName> <command>` — 将命令上下文切换到指定模板机器人
- `!bot use <botId> <command>` — 将命令上下文切换到指定机器人

## clear
- `!clear` — 清空当前播放列表中的所有歌曲

## command
- `!command parse <command>` — 显示指定命令的 AST（语法树）
- `!command tree` — *猜测*：显示命令树

## data
- `!data song cover get` — *猜测*：获取当前歌曲封面数据

## equalizer
- `!equalizer` — 显示当前均衡器状态（开关、目标响度、最大增益、当前增益）
- `!equalizer on` — 启用音量均衡
- `!equalizer off` — 禁用音量均衡
- `!equalizer toggle` — 切换音量均衡的开/关
- `!equalizer target <dBFS>` — 设置目标响度（范围 -30 到 0，默认 -16）
- `!equalizer maxgain <dB>` — 设置最大增益调整（范围 3 到 30，默认 15）

## eval
- `!eval <command> <arguments...>` — 执行给定的命令或拼接字符串后交给命令系统执行

## from
- `!from <factoryName> <url>` — *猜测*：使用指定资源工厂解析并播放链接

## get
- `!get <index> <list...>` — *猜测*：从列表中取出指定索引的元素

## getmy
- `!getmy id` — 获取你的 ID
- `!getmy uid` — 获取你的唯一 ID
- `!getmy name` — 获取你的昵称
- `!getmy dbid` — 获取你的数据库 ID
- `!getmy channel` — 获取你当前所在频道的 ID
- `!getmy all` — 获取关于你的全部信息

## getuser
- `!getuser uid byid <id>` — 通过用户 ID 获取其唯一 ID
- `!getuser name byid <id>` — 通过用户 ID 获取其昵称
- `!getuser dbid byid <id>` — 通过用户 ID 获取其数据库 ID
- `!getuser channel byid <id>` — 通过用户 ID 获取其当前所在频道的 ID
- `!getuser all byid <id>` — 通过用户 ID 获取其全部信息
- `!getuser id byname <name>` — 通过用户名获取其 ID
- `!getuser all byname <name>` — 通过用户名获取其全部信息
- `!getuser name bydbid <dbid>` — 通过数据库 ID 获取用户名
- `!getuser uid bydbid <dbid>` — 通过数据库 ID 获取其唯一 ID

## help
- `!help [<command>]` — 显示所有命令，或某个命令的详细帮助
- `!help all` — *猜测*：显示所有命令的详细帮助
- `!help command [<filter>] <command...>` — *猜测*：显示指定命令的帮助
- `!help play` — *猜测*：显示播放命令的帮助

## history
- `!history add <id>` — 将指定 ID 的歌曲添加到播放队列
- `!history clean` — 清理历史记录文件以提高启动性能
- `!history clean removedefective` — 检查并移除历史记录中已无法打开的链接
- `!history clean upgrade` — *猜测*：升级历史记录格式
- `!history delete <id>` — 从历史记录中删除指定 ID 的条目
- `!history from <uid> [<count>]` — 获取指定用户最近播放的歌曲（可按数量限制）
- `!history id <id>` — 显示指定 ID 歌曲的全部已保存信息
- `!history id (last|next)` — 获取最高/下一个歌曲 ID
- `!history last` — 重新播放最近播放过的歌曲
- `!history last <count>` — 获取最近播放的 <count> 首歌曲
- `!history play <id>` — 播放指定 ID 的歌曲
- `!history rename <id> <name>` — 将指定 ID 的歌曲重命名为 <name>
- `!history till <date>` — 获取截至指定日期播放过的所有歌曲
- `!history till <hour|today|yesterday|week>` — 按时间描述符获取歌曲
- `!history title <text>` — 获取标题包含指定文本的所有歌曲

## if
- `!if <arg0> <比较符> <arg1> <then> [<else>]` — 比较两个参数，条件成立则执行 then，否则可选执行 else

## info
- `!info [<offset> [<count>]]` — 显示当前播放列表与队列状态的摘要

## json
- `!json merge <arguments...>` — 将多个 JsonResult 合并为一个
- `!json api` — *猜测*：生成 API 接口的 JSON 描述

## jump
- `!jump <offset>` — 跳转到当前队列中的指定索引并开始播放

## kickme
- `!kickme` — 猜猜看？（把自己踢出服务器）
- `!kickme far` — 同 `!kickme`，把自己踢得更远

## list
- `!list add <listId> <url>` — 将一个链接添加到你的私人播放列表
- `!list clear <listId>` — 清空一个播放列表
- `!list create <listId> [<title>]` — *猜测*：创建一个新的播放列表
- `!list delete <listId>` — 删除指定名称的播放列表（只能删除自己创建的，管理员可删除任意）
- `!list from <factoryName> <listId> <url>` — *猜测*：使用指定资源工厂将链接加载到播放列表
- `!list import <listId> <url>` — 从其他平台（如 YouTube 等）导入播放列表
- `!list insert <listId> <index> <url>` — *猜测*：在播放列表的指定位置插入链接
- `!list item get <listId> <index>` — *猜测*：获取播放列表中指定位置的条目
- `!list item move <listId> <from> <to>` — 将播放列表中的条目从 <from> 移动到 <to>
- `!list item delete <listId> <index>` — 删除播放列表中指定位置的条目
- `!list item name <listId> <index> <name>` — 重命名播放列表中的条目
- `!list list [<pattern>]` — 显示所有用户的所有播放列表（可按模式过滤）
- `!list merge <baseListId> <mergeListId>` — 将另一个播放列表追加到你的播放列表
- `!list name <listId> <title>` — 设置播放列表的名称
- `!list play <listId> [<index>]` — 用你的工作列表替换当前播放列表并从指定位置开始播放
- `!list queue <listId>` — 将你的播放列表追加到播放队列
- `!list show <listId> [<offset> [<count>]]` — 显示播放列表中的所有歌曲

## next
- `!next` — 播放播放列表中的下一首歌曲

## param
- `!param <index>` — *猜测*：获取命令参数列表中指定索引的参数

## pm
- `!pm` — 与机器人建立私密会话
- `!pm channel <message>` — *猜测*：向频道发送私密消息
- `!pm server <message>` — *猜测*：向服务器发送私密消息
- `!pm user <clientId> <message>` — 向指定用户发送消息

## pause
- `!pause` — 暂停当前歌曲，用 `!play` 恢复

## play
- `!play [<url>]` — 自动判断链接类型并播放（如 YouTube 等特殊资源，或本地文件等直接资源）

## plugin
- `!plugin list` — 列出所有已发现的插件
- `!plugin unload <identifier>` — 卸载一个插件
- `!plugin load <identifier>` — 加载一个插件

## previous
- `!previous` — 播放播放列表中的上一首歌曲

## print
- `!print <parameter...>` — 将多个参数格式化为一个

## quiz
- `!quiz` — 显示问答模式状态
- `!quiz on` — 开启问答模式（隐藏歌名，让朋友猜歌名）
- `!quiz off` — 关闭问答模式（恢复显示歌名）

## random
- `!random` — 查看播放列表是否随机播放
- `!random on` — 启用随机播放
- `!random off` — 禁用随机播放
- `!random seed [<seed>]` — 获取或设置随机播放顺序的种子

## repeat
- `!repeat` — 获取当前重复模式
- `!repeat off` — 禁用重复播放
- `!repeat one` — 单曲循环
- `!repeat all` — 循环播放整个播放列表

## rights
- `!rights can <rights...>` — 返回调用者（你）可以执行的命令权限子集
- `!rights reload` — 从文件重新加载权限配置

## rng
- `!rng [<max>]` — 获取一个 0 到 <max>（默认 100）之间的随机数
- `!rng <min> <max>` — 获取一个 <min> 到 <max> 之间的随机数

## seek
- `!seek <time>` — 跳转到当前歌曲的指定时间点（支持 `<sec>`、`<min:sec>`、`<0h0m0s>` 格式）

## search
- `!search add <index>` — *猜测*：将指定索引的搜索结果添加到播放队列
- `!search from <factoryName> <query>` — *猜测*：使用指定资源工厂搜索
- `!search get <index>` — *猜测*：获取指定索引的搜索结果
- `!search play <index>` — *猜测*：播放指定索引的搜索结果
- `!search show` — *猜测*：显示当前的搜索结果

## server
- `!server tree` — *猜测*：显示服务器结构树

## settings
- `!settings` — 提供获取/设置机器人配置的方法
- `!settings copy <from> <to>` — 复制机器人模板并命名为 <to>
- `!settings create <name>` — 创建新的机器人模板
- `!settings delete <name>` — 删除机器人模板
- `!settings get [<path>]` — 获取机器人配置中的值
- `!settings set <path> [<value>]` — 设置机器人配置中的值
- `!settings bot get <botName> [<path>]` — 获取指定机器人模板的配置值
- `!settings bot set <botName> <path> [<value>]` — 设置指定机器人模板的配置值
- `!settings bot reload [<name>]` — 从文件重新加载机器人模板配置
- `!settings global get [<path>]` — 获取核心配置中的值
- `!settings global set <path> [<value>]` — 设置核心配置中的值
- `!settings help <path>` — 获取某个配置项的描述

## song
- `!song` — 显示当前播放歌曲的名称

## stop
- `!stop` — 停止当前歌曲

## subscribe
- `!subscribe` — 订阅音乐，让你不受所在频道的限制收听
- `!subscribe tempchannel` — 将你当前所在的频道临时添加到音乐播放
- `!subscribe channel [<channel...>]` — 将一个或多个频道添加到音乐播放
- `!subscribe client <clientId>` — 订阅指定客户端，使其可在任何地方收听

## system
- `!system info` — *猜测*：显示系统运行信息
- `!system quit` — 关闭 TS3AudioBot 应用

## take
- `!take <count> [<start> [<delimiter>]] <text>` — 从文本中按分隔符截取子串

## unsubscribe
- `!unsubscribe` — 恢复仅在活动频道收听音乐
- `!unsubscribe channel [<channel...>]` — 将一个或多个频道从音乐播放中移除
- `!unsubscribe temporary` — 清除所有临时目标
- `!unsubscribe client <clientId>` — 取消订阅指定客户端

## version
- `!version` — 获取当前构建版本

## volume
- `!volume [<level>]` — 查看或设置音量（level 为 0-100，也支持 `+/-<level>` 增减音量）

## whisper
- `!whisper all` — 设置音乐发送方式为发送给所有人
- `!whisper group <type> <target> [<targetId>]` — 设置特定的 TeamSpeak 耳语组
- `!whisper list` — 查看当前的音乐发送方式
- `!whisper off` — 启用普通语音模式
- `!whisper subscription` — 启用默认的耳语订阅模式

## xecute
- `!xecute <arguments...>` — 依次求值所有参数
