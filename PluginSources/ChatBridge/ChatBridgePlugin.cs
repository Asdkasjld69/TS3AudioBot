// ChatBridge — TS3AudioBot 插件
//
// 职责：只响应聊天里 @<bot 昵称> 的消息，把内容转交给沙箱内的 codewhale，
// 再把回答发回原会话（私聊回私聊，频道回频道，服务器消息回服务器）。
//
// 安全边界不在本文件里：目录访问由 /usr/local/bin/ts3ab-cw-sandbox 的
// mount namespace 白名单强制，密钥与模型路由由独立的 CODEWHALE_HOME 提供。
// 本插件只做触发、限流、超时、输出清洗和回帖。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Plugins;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Rights;
using TSLib;
using TSLib.Messages;
using TSLib.Scheduler;

namespace ChatBridge
{
	public sealed class ChatBridgePlugin : IBotPlugin
	{
		private static readonly Logger Log = PluginExtensions.GetLogger();

		private const string SandboxPath = "/usr/local/bin/ts3ab-cw-sandbox";
		private const string CodewhalePath = "/usr/local/bin/codewhale";
		private const string HomeDir = "/var/lib/ts3ab-codewhale";
		// 沙箱工作区（ts3ab-cw-sandbox 的 --chdir 与提示词共用这一事实）。
		// 记忆库通过 bind 投影到 $WorkspaceDir/.ts3ab-memory，既落在 workspace 内，
		// 又由 bwrap 单独挂成 rw —— 否则 codewhale 的 workspace-write 会拒绝写入。
		private const string WorkspaceDir = "/mystorage/audio";
		// bot 自建 skill 的落地目录（沙箱内 bind 成 rw，见 ts3ab-cw-sandbox）。
		private const string SkillsDir = "/mystorage/audio/.ts3ab-skills";
		// read/ls/grep 是只读检索；load_skill 让内置技能可加载；
		// bash 用于跑 skill 自带脚本（记忆库、天气查询）。write/edit 仍不下放，
		// 且 bash 的破坏面被 bwrap 挂载约束在：/mystorage 只读 + ts3ab-bot 可写。
		private const string AllowedTools = "read,ls,grep,load_skill,bash";
		private const int MaxTurns = 6;
		private const int ReplyChunkBytes = 900;
		private const int MaxConcurrent = 1;
		private const int MaxPromptChars = 800;
		private const int BusyNoticeLimit = 3;

		// 会话策略：私聊按 uid 一条长期会话（空闲 24 小时失效），
		// 公共聊天（频道/服务器）全员共用一条，每 6 小时滚动换新。
		private static readonly TimeSpan PublicSessionLifetime = TimeSpan.FromHours(6);
		private static readonly TimeSpan PrivateSessionIdle = TimeSpan.FromHours(24);

		// 会话重置时把本轮对话精炼成长期记忆。精炼只跑一次无工具的 one-shot 问答；
		// 写库由插件直接调 mem.py（不经 ts3ab-mem），避免后台任务与主对话争用
		// .speaker 单文件，把记忆写到别人的分区。
		private const string MemPython = "/mystorage/ds_memory/.venv/bin/python";
		private const string MemScript =
			"/var/lib/ts3ab-codewhale/skills/memory-persistence/scripts/mem.py";
		private const string MemDb = "/mystorage/ds_memory/ts3ab-bot/memory.db";
		private static readonly TimeSpan DistillTimeout = TimeSpan.FromSeconds(120);
		private static readonly TimeSpan MemWriteTimeout = TimeSpan.FromSeconds(120);
		// 精炼只取最近若干轮并限长，避免长会话把一次请求撑爆。
		private const int DistillMaxTurns = 30;
		private const int DistillMaxChars = 12000;
		private const int DistillMaxItems = 5;
		// 轮次缓冲的防御上限：正常窗口内远达不到，只为防止异常情况下内存无界增长。
		private const int TurnBufferLimit = 200;

		// 精炼提示词：只输出条目正文，不调用工具、不编号，便于逐行解析。
		private const string DistillPrompt =
			"[记忆精炼] 你在整理一段 TeamSpeak 聊天记录。只提取值得长期记住的信息：" +
			"用户稳定的偏好、身份或环境事实、明确约定、纠正，以及长期有效的指令。" +
			"忽略一次性提问、闲聊、寒暄、泛泛常识和临时状态；不要写“用户说”这类转述，" +
			"也不要总结整段对话。如果记录里用户明确说过不要记住、不要保存，跳过那部分。" +
			"输出要求：每条一行，不超过 80 字，最多 5 条；" +
			"不要编号、项目符号、代码块、引号或任何解释；没有值得记的就只输出 NONE。\n\n" +
			"[聊天记录]\n";

		// 模型即使被要求“不要编号”，仍可能带列表前缀；解析时统一剥掉。
		private static readonly Regex DistillListPrefix = new Regex(
			@"^\s*(?:[-*•]|\d{1,2}[.、)])\s*",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		// 复用 rights.toml 的白名单：这个 right 不挂任何聊天命令，
		// 只用来问权限系统“这个 uid 是否被授权”。
		private const string TriggerRight = "cmd.chatbridge";
		// 口令分发用独立 right：能聊天不等于是能拿上传口令。
		private const string PasswordRight = "cmd.chatbridge.password";
		// 歌单写操作单独一个 right：改歌单是持久化副作用，比聊天重。
		private const string PlaylistRight = "cmd.chatbridge.playlist";

		// 上传口令由匿名聊天站点每小时整点轮换写入，32 位小写十六进制。
		// 分发起点在插件而不是模型：只有这里能“只发私信”，模型没有发私信的工具。
		private const string PasswordFile =
			"/mystorage/git-repos/anonymous-chat/server/.audio_password";

		// 本地控制通道：沙箱内的 ts3ab-ctl 据此控制播放。只绑回环，
		// 用每次启动重新生成的随机 token 认证；token 文件仅 teamspeak 可读。
		private const int ControlPort = 58914;
		private const string ControlTokenFile = "/var/lib/ts3ab-codewhale/ctl.token";

		// 当前发言者标记：沙箱内的 ts3ab-mem 据此把记忆读写限制在本人分区。
		// 写宿主真实目录（而非 .ts3ab-memory 空挂载点），沙箱内即投影为
		// "/mystorage/audio/.ts3ab-memory/.speaker"。
		// 并发闸门是 MaxConcurrent=1，所以单个文件不会串台。
		private const string SpeakerFile = "/mystorage/ds_memory/ts3ab-bot/.speaker";

		// 30 秒太长：连续问两个不同问题（先 hey 再问天气）会被静默吞掉，
		// 表现为"完全没反应"。5 秒足够挡住误触和刷屏。
		// 思考中头像：200x200 PNG，深蓝灰渐变底 + 三个白点；内嵌以免依赖外部文件。
		private static readonly byte[] ThinkingAvatar = Convert.FromBase64String(
			"iVBORw0KGgoAAAANSUhEUgAAAMgAAADICAIAAAAiOjnJAAAC9ElEQVR42u3Ux24UQRiF0X4QMMZ5nMcTnMERR97/IdiwZMkg1kjGc8vt6j7S2f9S3U/VfFjah7jGEyAshIWwQFgIi36HNYQ4YSEsagpreQhxwqJMWB+XhxAnLEqFdQBxwkJYCAtheQVKhLVyAHGzsEYQJyyERUVhLayMIE5YCAthIawxxDULq2OIExbCQlgIyysgLGoJ69PqBOKEhbCoKay1CcQJC2FRVVhTiBMWwkJYCMsrUCCsxbUpxDWL61OIm4V1CHHCQlgIC2F5BYSFsOh5WEcQ1yxuHEFc83njCOKEhbAQFsI6hjhhISyERe/DGhxDnLAQFhWFtTQ4gThhISyEhbC8AsKimrA2TyBuFtYpxAkLYSEs+h7W8uYpxAmLUmGdQZywKBTW1hnECQthISyE5RUoEtY5xAmLMmGtbJ1DnLAoFNb2OcQJC2FRVVhfIE5YCAthvbEfP3+5IqzYAP/iirDCMwQn6dKVFsJa3flalxcu8ZcrbWk6XNWr9+jSFWEVWeIVe3TpirCEJazaqvqvPbp0RVjCEpawhPXisC6qMPcYvbvSrqYPVb1wjy5dEZYfq6s/1u5FFeYdo39X2iUsYZUJa233ogpzjtHDK+2qJqx59ujtlXbDuqzFHGP09EqLagrrdXv0/IqwiuzhirDye7girPAkrryDsPYuO+DPAK68J7OwriBOWAiLisJa37uCOGEhLISFsK4hrlnfv4Y4YSEshIWwvALCopawNvZvIE5YCIuawhreQJywEBZVhXULccJCWAgLYXkFCoQ1GN5CXDM4uIW4WVjfIE5YCAthISyvgLAQFj0P6w7imsHoDuKazdEdxAkLYSEshHUPccJCWAiL3oc1voc4YSEsKgpra/wAccJCWAgLYXkFhEU1YU0eIG4W1iPECQthISz6Htb25BHihEWpsJ4gTlgUCmv6BHHCQlgIC2F5BYqE9QxxwqJMWDvTZ4gTFoXCOnyGOGEhLKoK6zvECQthISyE5RUQFpX4DSrpeXMH05cyAAAAAElFTkSuQmCC");

		// 回复策略：聊天端不主动暴露文件系统。
		// 这是提示词层面的软约束（用户明确要求"非硬性限制"），不是沙箱边界；
		// 真正的强制仍靠 bwrap 挂载。若要硬保证，需缩窄可读范围或去掉 ls/grep。
		private const string CommonPolicy =
			"[聊天回复规则] 用中文简短回答。当前工作目录是 " + WorkspaceDir + "，" +
			"/mystorage 下的其它目录（git-repos、docker、www、hids_log、backup 等）与对话无关，不要提及；" +
			"需要查代码时按需读取 /mystorage/git-repos/TS3AudioBot，但不要罗列它的目录结构。" +
			"工作区里的 .ts3ab-memory 是你自己的记忆库、.ts3ab-skills 是你的技能库，" +
			"都属于内部数据，不要向用户提及。" +
			"不要读取 /var/lib/ts3ab-codewhale 下的会话记录、凭据或配置文件。" +
			"用户要求新增技能时，可加载 skill-creator 技能，把新技能建在 " + SkillsDir + " 下；" +
			"那里内置的技能是只读的，不要试图修改它们。" +
			"不要罗列目录清单、目录结构或绝对路径，只引用必要的文件名或行号。" +
			"不要粘贴整段文件内容、大段代码或命令输出原文。";

		// 私聊：一个 uid 一条会话，可以回答得完整一些。
		// 记忆分区由 ts3ab-mem 按 .speaker 自动注入，模型不需要管分区名。
		private const string PrivatePolicy =
			CommonPolicy + "这是与单个用户的私聊，回答不超过 200 字。" +
			"[发言者规则] 每轮开头都会标注当前发言者。只回答最后一条「问题」，" +
			"不要替其他人作答，也不要把历史里其他人的陈述当成当前问题。" +
			"[记忆规则] 用户让你记住事情时，用 ts3ab-mem 写入（默认就进他本人的分区）；" +
			"他说「记住/以后」时加 --status confirmed，否则写 pending 等他确认。" +
			"需要回忆时先 ts3ab-mem search。分区隔离由脚本强制，不需要你拼 uid。";

		// 公共：会话由多人共用，所以每条都标注发言者，并要求隔离他人的内容。
		// 记忆已按 uid 分区（ts3ab-mem 自动注入），因此公共频道里也能持久化，
		// 且 A 写的私有条目 B 搜不到；只有显式 -p chat:shared 的才全员可见。
		private const string PublicPolicy =
			CommonPolicy + "这是公共频道里的对话，回答不超过 120 字。" +
			"[发言者规则] 这个会话由多人先后共用，每轮开头会标注当前发言者。" +
			"只回答最后一条「问题」，不要替其他人作答，" +
			"不要把上一位发言者的提问或陈述当成当前问题；" +
			"不得复述、总结或透露其他发言者说过的任何内容；" +
			"回答只针对当前发言者，用第二人称，不要评论还有谁在场。" +
			"[记忆规则] 公共频道也可以记住东西：" +
			"用户个人的偏好/事实用 ts3ab-mem 写入（脚本会自动落到他本人的分区），" +
			"他说「记住/以后」时加 --status confirmed；" +
			"只有他明确说「记给大家/写进公共规则」时才加 -p chat:shared。" +
			"回忆时先 ts3ab-mem search（自动搜本人分区 + 共享分区）。" +
			"检索结果里如果出现别人的内容不要念出来，只用自己的和共享条目作答。";

		// 续接已有会话时不再重复整段策略（历史里已经有了），只留一句提醒。
		private const string PrivateReminder =
			"[提醒] 只回答最后一条「问题」；记忆走 ts3ab-mem，分区自动。";

		private const string PublicReminder =
			"[提醒] 公共会话：只回答最后一条「问题」，不复述其他发言者说过的内容。" +
			"记忆走 ts3ab-mem（自动分区），记给大家才用 -p chat:shared。";

		private static readonly TimeSpan UserCooldown = TimeSpan.FromSeconds(5);
		private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(90);
		// 口令每人 60 秒一次：防刷屏，也避开服务器的私信反洪水。
		private static readonly TimeSpan PasswordCooldown = TimeSpan.FromSeconds(60);

		// 只匹配“索取口令”这种命令句，不匹配“口令文件在哪/记住口令”这类陈述句。
		// 实际匹配例子：口令、上传口令、给我上传口令、请把上传口令发给我、上传口令是多少
		private static readonly Regex PasswordRequest = new Regex(
			@"^\s*(?:请|麻烦)?\s*(?:帮我|给我|发我|把)?\s*(?:一下)?\s*(?:上传|音频)?\s*口令" +
			@"\s*(?:发给我|给我|发我|发一下)?\s*(?:是多少|是什么|是啥|呢)?\s*[。．.!！?？~～]*\s*\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PasswordFormat =
			new Regex(@"^[0-9a-f]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		// ── 歌单指令 ──
		// 全部写操作都调 bot 自己的 PlaylistManager，不碰文件：
		// 只有经过它，playlistCache / playlistInfo / dirtyList 才会同步，
		// 否则 bot 后续 !list、!play 读到的还是旧内容。
		// 形式：
		//   歌单                      → 列全部
		//   歌单 <名字>               → 展开曲目
		//   歌单 新建 <名字> [标题]
		//   歌单 删除 <名字>
		//   歌单 <名字> 改名 <新标题>
		//   歌单 <名字> 加 <链接>
		//   歌单 <名字> 删 <序号>
		private static readonly Regex PlaylistCommand =
			new Regex(@"^歌单(?:\s+(?<rest>.+))?\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlCreate = new Regex(
			@"^(?:新建|创建)\s+(?<name>\S+)(?:\s+(?<title>.+))?\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlDelete = new Regex(
			@"^(?:删除|删掉|移除)\s+(?<name>\S+)\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlRename = new Regex(
			@"^(?<name>\S+)\s*(?:改名|重命名)\s*(?:为|成)?\s*(?<title>.+?)\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlRemove = new Regex(
			@"^(?<name>\S+)\s*(?:删歌|删曲|删除第|删第)\s*(?<index>\d+)\s*(?:首|个)?\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlAdd = new Regex(
			@"^(?<name>\S+)\s*(?:加歌|添加歌曲|添加|加)\s+(?<link>.+?)\z",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex PlShow = new Regex(
			@"^(?<name>\S+)\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		// 可达性探测：只用于 http(s) 链接。
		private static readonly HttpClient ProbeClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(8),
		};
		private static readonly TimeSpan PlaylistOpTimeout = TimeSpan.FromSeconds(45);

		private readonly Ts3Client ts3;
		private readonly ConfBot config;
		private readonly RightsManager rights;
		private readonly CommandManager commands;
		// TS3 客户端只在专用线程上接受命令，回复必须调度回去。
		private readonly DedicatedTaskScheduler scheduler;
		// 思考态头像结束后要写回 bot 原有的头像状态，因此需要 Bot 与播放状态。
		private readonly Bot bot;
		private readonly PlayManager playManager;
		// 歌单写操作走 bot 自己的管理器，不直改文件。
		private readonly PlaylistManager playlistManager;
		private readonly ResolveContext resolver;
		// 播放控制：直接调 bot 自己的管理器，与 !play / !pause 走同一套逻辑。
		private readonly Player player;
		private readonly RightsBag rightsBag;
		private readonly SemaphoreSlim gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
		// 精炼串行执行：不同会话同时到期时排队，避免并发打满模型与记忆库。
		private readonly SemaphoreSlim distillGate = new SemaphoreSlim(1, 1);
		private readonly ConcurrentDictionary<string, DateTime> lastRequest =
			new ConcurrentDictionary<string, DateTime>();
		// 会话表：key 为 "dm:<uid>"（私聊）或 "public"（频道与服务器消息共用的公共会话）。
		private readonly ConcurrentDictionary<string, ChatSession> sessions =
			new ConcurrentDictionary<string, ChatSession>();
		private readonly ConcurrentDictionary<string, int> busyNotices =
			new ConcurrentDictionary<string, int>();
		// 口令冷却单独一份：不占用普通对话的 5 秒窗口。
		private readonly ConcurrentDictionary<string, DateTime> passwordLast =
			new ConcurrentDictionary<string, DateTime>();

		private Regex trigger = null!;
		private int initialized;
		private int runCounter;

		public ChatBridgePlugin(Ts3Client ts3, ConfBot config, RightsManager rights,
			CommandManager commands, DedicatedTaskScheduler scheduler,
			Bot bot, PlayManager playManager, Player player,
			PlaylistManager playlistManager, ResolveContext resolver)
		{
			this.ts3 = ts3 ?? throw new ArgumentNullException(nameof(ts3));
			this.config = config ?? throw new ArgumentNullException(nameof(config));
			this.rights = rights ?? throw new ArgumentNullException(nameof(rights));
			this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
			this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
			this.bot = bot ?? throw new ArgumentNullException(nameof(bot));
			this.playManager = playManager ?? throw new ArgumentNullException(nameof(playManager));
			this.player = player ?? throw new ArgumentNullException(nameof(player));
			this.playlistManager = playlistManager ?? throw new ArgumentNullException(nameof(playlistManager));
			this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
			rightsBag = new RightsBag(TriggerRight, PasswordRight, PlaylistRight);
		}

		public void Initialize()
		{
			if (Interlocked.Exchange(ref initialized, 1) != 0)
				return;

			var selfName = config.Connect.Name.Value;
			if (string.IsNullOrWhiteSpace(selfName))
				throw new InvalidOperationException("bot.connect.name 为空，无法确定 @ 触发词。");

			// 前一个字符不能是字母数字（排除 a@Mybot.com 这类邮箱），
			// 后一个字符必须是行尾、空白或标点（排除 @Mybot2 这类更长昵称）。
			trigger = new Regex(
				@"(?<![\w@])@" + Regex.Escape(selfName) + @"(?=$|[\s\p{P}])",
				RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

			ts3.OnMessageReceived += OnMessageReceived;

			// 把 right 名注册给命令系统，让 rights.toml 里的 "+" = "*" 能展开出它。
			// 否则通配符只涵盖已注册的 right，白名单规则会静默漏掉本插件。
			commands.RegisterCollection(rightsBag);

			PruneSessions();
			Log.Info("ChatBridge 已启用：触发词 '@{0}'，权限 right '{1}'，工具集 [{2}]",
				selfName, TriggerRight, AllowedTools);
			Log.Info("ChatBridge 口令分发：right '{0}'，源文件 {1}，只走私信",
				PasswordRight, PasswordFile);
			StartControlChannel();
			Log.Info("ChatBridge 会话策略：私聊按 uid 独立（空闲 {0}h 失效），公共聊天共用一条并每 {1}h 重置",
				PrivateSessionIdle.TotalHours, PublicSessionLifetime.TotalHours);
		}

		private async Task OnMessageReceived(object? sender, TextMessage message)
		{
			try
			{
				if (message?.Message == null)
					return;

				// 诊断：所有到达的消息都记一行元信息（不含全文，只记前 20 字），
				// 用来区分 消息没到 / 到了但没@ / @了但未授权。
				var uidDbg = message.InvokerUid?.Value ?? "(none)";
				var preview = message.Message.Length <= 20 ? message.Message : message.Message.Substring(0, 20);
				Log.Info("ChatBridge 收包 uid={0}.. target={1} len={2} hasAt={3} text=[{4}]",
					uidDbg.Substring(0, Math.Min(4, uidDbg.Length)),
					message.Target, message.Message.Length, message.Message.Contains('@'), preview);

				// 私聊直通：TS3 私聊消息不带 @ 前缀，文本就是纯内容。
				// 私聊本身已经是对着 bot 说的，不需要再要求 @。
				string prompt;
				if (message.Target == TextMessageTargetMode.Private)
				{
					prompt = message.Message.Trim();
				}
				else
				{
					var match = trigger.Match(message.Message);
					if (!match.Success)
						return; // 频道/服务器消息未 @ 自己：完全忽略
					prompt = message.Message.Remove(match.Index, match.Length).Trim();
				}

				if (prompt.Length == 0)
					return;
				if (prompt.Length > MaxPromptChars)
					prompt = prompt.Substring(0, MaxPromptChars);

				var who = message.InvokerUid?.Value ?? message.InvokerId.ToString();
				var now = DateTime.UtcNow;

				Log.Info("ChatBridge 命中@: {0} (uid {1}..) target={2} promptLen={3}",
					message.InvokerName, who.Substring(0, Math.Min(4, who.Length)),
					message.Target, prompt.Length);

				// 口令走独立通道：不调模型、不进程上下文，由插件直接私信。
				// 放在前面是因为它有自己的 right，不需要普通聊天权限。
				if (PasswordRequest.IsMatch(prompt))
				{
					await HandlePasswordRequest(message, who, now).ConfigureAwait(false);
					return;
				}

				// 歌单指令同理：写操作必须在 bot 进程内调 PlaylistManager，
				// 模型在沙箱里改不了（也不应该改）文件。
				var plMatch = PlaylistCommand.Match(prompt);
				if (plMatch.Success)
				{
					await HandlePlaylistCommand(message, who,
						plMatch.Groups["rest"].Success ? plMatch.Groups["rest"].Value.Trim() : "")
						.ConfigureAwait(false);
					return;
				}

				// 白名单由 rights.toml 里现有的 useruid 数组决定，不另建配置。
				if (!await IsAuthorized(message).ConfigureAwait(false))
				{
					Log.Info("ChatBridge 拒绝: uid {0}.. 没有 '{1}' 权限",
						who.Substring(0, Math.Min(4, who.Length)), TriggerRight);
					return;
				}

				if (lastRequest.TryGetValue(who, out var last) && now - last < UserCooldown)
				{
					// 冷却很短，静默丢弃不会让用户困惑；DEBUG 留下痕迹便于排查。
					Log.Debug("ChatBridge 冷却中丢弃：{0} 距上次 {1:0.0}s",
						message.InvokerName, (now - last).TotalSeconds);
					return;
				}

				if (!await gate.WaitAsync(0).ConfigureAwait(false))
				{
					var count = busyNotices.AddOrUpdate(who, 1, (_, v) => v + 1);
					if (count <= BusyNoticeLimit)
						await Reply(message, "我这边正忙，稍后再问一次。");
					return;
				}

				lastRequest[who] = now;
				busyNotices.TryRemove(who, out _);
				try
				{
					Log.Info("ChatBridge: {0} ({1}) 提问：{2}", message.InvokerName, who, prompt);

					// 头像在整个“思考 + 发送”期间保持提示态，回复落地后再交给 bot 恢复。
					await SetThinkingAvatar(true).ConfigureAwait(false);
					try
					{
						var started = Stopwatch.StartNew();
						string answer;
						// 私聊一人一条会话；频道/服务器消息共一条公共会话，由发言者标注区分。
						var isPublic = message.Target != TextMessageTargetMode.Private;
						var sessionKey = SessionKey(message, who);
						var session = GetSession(sessionKey, isPublic);
						// 会话 id、发言者、记忆分区都得在调模型前就位。
						MarkSpeaker(who);
						Log.Info("ChatBridge: 会话 {0}（{1}）", sessionKey,
							session.Id == null ? "新建" : "续接 " + session.Id);
						try
						{
							answer = await RunCodewhale(session, isPublic, SpeakerLine(message, who), prompt)
								.ConfigureAwait(false);
						}
						finally
						{
							started.Stop();
						}

						if (Interlocked.Increment(ref runCounter) % 20 == 0)
							PruneSessions();

						// 只把成功的一轮记进缓冲区；重置时据此精炼。
						if (!string.IsNullOrWhiteSpace(answer))
							RecordTurn(session, who, message.InvokerName, prompt, answer);

						if (string.IsNullOrWhiteSpace(answer))
							answer = "这次没拿到有效回答。";

						Log.Info("ChatBridge: 回答 {0} 字，耗时 {1:0.0}s", answer.Length, started.Elapsed.TotalSeconds);
						await Reply(message, answer).ConfigureAwait(false);
					}
					finally
					{
						await SetThinkingAvatar(false).ConfigureAwait(false);
					}
				}
				finally
				{
					gate.Release();
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "ChatBridge 处理消息失败");
			}
		}

		/// <summary>
		/// 问权限系统：这个发信人是否被授权触发。
		/// 复用 rights.toml 已有的规则与 useruid 数组，不另建白名单。
		/// </summary>
		private ValueTask<bool> IsAuthorized(TextMessage message)
			=> HasRight(message, TriggerRight);

		/// <summary>
		/// 问权限系统：这个发信人是否被授予了指定 right。
		/// 复用 rights.toml 已有的规则与 useruid 数组，不另建白名单。
		/// </summary>
		private async ValueTask<bool> HasRight(TextMessage message, string right)
		{
			var call = new ClientCall(
				message.InvokerUid ?? Uid.Anonymous,
				message.Message,
				clientId: message.InvokerId,
				nickName: message.InvokerName,
				visibiliy: message.Target,
				// 不在此发起 server-group 查询；本插件只关心 uid 白名单。
				serverGroups: Array.Empty<ServerGroupId>());

			var info = new ExecutionInformation();
			info.AddModule(call);

			try
			{
				var allowed = await rights.HasAllRights(info, right).ConfigureAwait(false);
				if (Log.IsTraceEnabled)
				{
					// 只记判定结果与 uid 前缀，避免把完整身份写进日志。
					var uid = message.InvokerUid?.Value ?? "?";
					Log.Trace("ChatBridge: uid {0}… 权限 {1} = {2}",
						uid.Substring(0, Math.Min(4, uid.Length)), right, allowed);
				}
				return allowed;
			}
			catch (Exception ex)
			{
				// 权限系统故障时 fail-closed，宁可不应答。
				Log.Warn(ex, "ChatBridge: 权限判定失败，按拒绝处理");
				return false;
			}
		}

		/// <summary>
		/// 索取上传口令。口令永远只走私信：
		///   * 公共频道/服务器消息只回一句“已私信”，不回口令本身；
		///   * 不调用 codewhale，口令不进模型上下文，也不进会话记录。
		/// </summary>
		private async Task HandlePasswordRequest(TextMessage message, string who, DateTime now)
		{
			var uid = who.Substring(0, Math.Min(4, who.Length));

			if (!await HasRight(message, PasswordRight).ConfigureAwait(false))
			{
				Log.Info("ChatBridge 拒绝口令: uid {0}.. 没有 '{1}' 权限", uid, PasswordRight);
				// 未授权不回复，避免在频道里暴露“支持口令功能”这件事。
				return;
			}

			if (passwordLast.TryGetValue(who, out var last) && now - last < PasswordCooldown)
			{
				Log.Debug("ChatBridge 口令冷却中丢弃：{0} 距上次 {1:0}s",
					message.InvokerName, (now - last).TotalSeconds);
				return;
			}
			passwordLast[who] = now;

			string password;
			try
			{
				password = (await File.ReadAllTextAsync(PasswordFile).ConfigureAwait(false)).Trim();
			}
			catch (Exception ex)
			{
				// 只记异常类型，不记路径内容以外的任何东西，更不记读到的值。
				Log.Warn(ex, "ChatBridge: 读取上传口令失败（{0}）", PasswordFile);
				await Reply(message, "现在拿不到上传口令，请稍后再试。").ConfigureAwait(false);
				return;
			}

			if (!PasswordFormat.IsMatch(password))
			{
				Log.Warn("ChatBridge: 上传口令格式异常（长度 {0}）", password.Length);
				await Reply(message, "现在拿不到上传口令，请稍后再试。").ConfigureAwait(false);
				return;
			}

			var text = "当前上传口令：" + password + "\n（每小时整点更换，请勿外传）";
			var sent = await SendPrivate(message.InvokerId, text).ConfigureAwait(false);
			Log.Info("ChatBridge: 上传口令已私信发给 {0} (uid {1}..)，结果={2}",
				message.InvokerName, uid, sent ? "成功" : "失败");

			// 公共场景只做确认；私聊场景刚才那条私信就是回答，不再重复。
			if (message.Target != TextMessageTargetMode.Private)
			{
				await Reply(message, sent
					? "已私信发给你。"
					: "私信发不出去，请确认允许接收私信后再试。").ConfigureAwait(false);
			}
		}

		/// <summary>私信给指定客户端。发送必须在 TS3 专用调度线程上。</summary>
		private async Task<bool> SendPrivate(ClientId clientId, string text)
		{
			try
			{
				await scheduler.InvokeAsync(() => ts3.SendMessage(text, clientId)).ConfigureAwait(false);
				return true;
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "ChatBridge: 私信发送失败");
				return false;
			}
		}

		// ── 歌单写操作 ──────────────────────────────────────────────
		// 全部经过 PlaylistManager，不直改文件：它同时维护 playlistCache、
		// playlistInfo 与 dirtyList，绕过它会让 bot 后续读到陈旧内容。

		private readonly SemaphoreSlim playlistGate = new SemaphoreSlim(1, 1);

		private async Task HandlePlaylistCommand(TextMessage message, string who, string rest)
		{
			var uid = who.Substring(0, Math.Min(4, who.Length));

			if (!await HasRight(message, PlaylistRight).ConfigureAwait(false))
			{
				Log.Info("ChatBridge 拒绝歌单指令: uid {0}.. 没有 '{1}' 权限", uid, PlaylistRight);
				return;
			}

			// 串行执行：可达性探测是异步的，不串行会出现交叉验证→写入的乱序。
			if (!await playlistGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
			{
				await Reply(message, "上一条歌单操作还在处理，稍后再试。").ConfigureAwait(false);
				return;
			}

			try
			{
				string answer;
				try
				{
					answer = await ExecutePlaylistCommand(rest).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "ChatBridge: 歌单指令失败（{0}）", rest);
					answer = "歌单操作失败：" + ex.Message;
				}
				await Reply(message, answer).ConfigureAwait(false);
			}
			finally
			{
				playlistGate.Release();
			}
		}

		private async Task<string> ExecutePlaylistCommand(string rest)
		{
			if (rest.Length == 0)
				return ListPlaylistsText();

			Match m;
			if ((m = PlCreate.Match(rest)).Success)
				return CreatePlaylist(m.Groups["name"].Value,
					m.Groups["title"].Success ? m.Groups["title"].Value.Trim() : null);
			if ((m = PlDelete.Match(rest)).Success)
				return DeletePlaylist(m.Groups["name"].Value);
			if ((m = PlRename.Match(rest)).Success)
				return RenamePlaylist(m.Groups["name"].Value, m.Groups["title"].Value.Trim());
			if ((m = PlRemove.Match(rest)).Success)
				return RemoveTrack(m.Groups["name"].Value, m.Groups["index"].Value);
			if ((m = PlAdd.Match(rest)).Success)
				return await AddTrack(m.Groups["name"].Value, m.Groups["link"].Value.Trim()).ConfigureAwait(false);
			if ((m = PlShow.Match(rest)).Success)
				return ShowPlaylist(m.Groups["name"].Value);

			return "看不懂这条歌单指令。可用：\n"
				+ "歌单 —— 列出全部\n"
				+ "歌单 <名字> —— 看曲目\n"
				+ "歌单 新建 <名字> [标题]\n"
				+ "歌单 删除 <名字>\n"
				+ "歌单 <名字> 改名 <新名字>\n"
				+ "歌单 <名字> 加 <链接>\n"
				+ "歌单 <名字> 删 <序号>";
		}

		private string ListPlaylistsText()
		{
			var res = playlistManager.GetAvailablePlaylists();
			if (!res.Ok)
				return "读取歌单列表失败：" + res.Error.Str;
			var lists = res.Value;
			if (lists.Length == 0)
				return "目前没有任何歌单。";

			var sb = new StringBuilder("共 ").Append(lists.Length).Append(" 个歌单：");
			foreach (var p in lists)
				sb.Append("\n- ").Append(p.Id).Append("（").Append(p.SongCount).Append(" 首）");
			return sb.ToString();
		}

		private string ShowPlaylist(string name)
		{
			var res = playlistManager.LoadPlaylist(name);
			if (!res.Ok)
				return "没有这个歌单：" + name;
			var items = res.Value.Items;
			if (items.Count == 0)
				return "歌单「" + name + "」是空的。";

			const int maxShow = 20;
			var sb = new StringBuilder("歌单「").Append(name).Append("」，共 ")
				.Append(items.Count).Append(" 首：");
			var shown = Math.Min(items.Count, maxShow);
			for (int i = 0; i < shown; i++)
				sb.Append("\n").Append(i + 1).Append(". ").Append(Describe(items[i]));
			if (items.Count > shown)
				sb.Append("\n……还有 ").Append(items.Count - shown).Append(" 首未显示");
			return sb.ToString();
		}

		private string CreatePlaylist(string name, string? title)
		{
			var res = playlistManager.CreatePlaylist(name, title);
			return res.Ok
				? "已新建歌单「" + name + "」。"
				: "新建失败：" + res.Error.Str;
		}

		private string DeletePlaylist(string name)
		{
			if (!playlistManager.ExistsPlaylist(name))
				return "没有这个歌单：" + name;
			var res = playlistManager.DeletePlaylist(name);
			return res.Ok
				? "已删除歌单「" + name + "」。"
				: "删除失败：" + res.Error.Str;
		}

		/// <summary>
		/// 改名同时换掉歌单标识与显示标题：用户说「改名」期望的是以后用新名访问。
		/// 实现上仍走 bot 的方法：新建 → 复制曲目 → 删旧名。
		/// </summary>
		private string RenamePlaylist(string oldName, string newName)
		{
			if (!playlistManager.ExistsPlaylist(oldName))
				return "没有这个歌单：" + oldName;
			if (newName == oldName)
				return "新旧名字一样，不需要改名。";
			if (playlistManager.ExistsPlaylist(newName))
				return "已经有叫「" + newName + "」的歌单了，换个名字。";

			var load = playlistManager.LoadPlaylist(oldName);
			if (!load.Ok)
				return "读取歌单失败：" + load.Error.Str;
			var items = load.Value.Items;
			var count = items.Count;

			var create = playlistManager.CreatePlaylist(newName, newName);
			if (!create.Ok)
				return "改名失败（建新歌单时）：" + create.Error.Str;

			var copy = playlistManager.ModifyPlaylist(newName, pl => pl.AddRange(items));
			if (!copy.Ok)
			{
				playlistManager.DeletePlaylist(newName);
				return "改名失败（复制曲目时）：" + copy.Error.Str;
			}

			var del = playlistManager.DeletePlaylist(oldName);
			if (!del.Ok)
				return "新歌单「" + newName + "」已建好，但删旧歌单失败：" + del.Error.Str;

			return "已把「" + oldName + "」改名为「" + newName + "」（" + count + " 首）。";
		}

		private string RemoveTrack(string name, string indexText)
		{
			if (!int.TryParse(indexText, out var index))
				return "序号要是数字。";

			var load = playlistManager.LoadPlaylist(name);
			if (!load.Ok)
				return "没有这个歌单：" + name;
			var count = load.Value.Items.Count;
			if (count == 0)
				return "歌单「" + name + "」是空的。";
			if (index < 1 || index > count)
				return "序号超范围：该歌单共 " + count + " 首，请用 1.." + count + "。";

			var removed = Describe(load.Value.Items[index - 1]);
			var res = playlistManager.ModifyPlaylist(name, pl => pl.RemoveAt(index - 1));
			return res.Ok
				? "已从「" + name + "」删掉第 " + index + " 首：" + removed
				: "删除失败：" + res.Error.Str;
		}

		/// <summary>
		/// 给歌单加歌。先解析链接，再对解析出的真实地址做一次可达性探测，
		/// 两步都过了才写入；任何一步失败都不改歌单。
		/// </summary>
		private async Task<string> AddTrack(string name, string link)
		{
			if (!playlistManager.ExistsPlaylist(name))
				return "没有这个歌单：" + name;

			PlayResource play;
			try
			{
				play = await resolver.Load(link).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Log.Info("ChatBridge: 歌单加歌解析失败 {0} -> {1}", link, ex.Message);
				return "无法加载「" + Shorten(link) + "」：链接无法识别或资源不存在。";
			}

			var probe = await ProbeReachable(play.PlayUri).ConfigureAwait(false);
			if (!probe.Ok)
			{
				Log.Info("ChatBridge: 歌单加歌不可达 {0} -> {1}", link, probe.Reason);
				return "无法加载「" + Shorten(link) + "」：" + probe.Reason;
			}

			var item = PlaylistItem.From(play);
			var res = playlistManager.ModifyPlaylist(name, pl => pl.Add(item));
			if (!res.Ok)
				return "添加失败：" + res.Error.Str;

			var total = playlistManager.LoadPlaylist(name);
			var count = total.Ok ? total.Value.Items.Count : -1;
			return "已加进「" + name + "」：" + Describe(item)
				+ (count >= 0 ? "（现在共 " + count + " 首）" : "");
		}

		private sealed class ProbeResult
		{
			public bool Ok;
			public string Reason = "";
		}

		/// <summary>
		/// 检查资源地址是否真的能取到。只对 http(s) 与本地文件做实际探测，
		/// 其它协议不做判断，按通过处理。
		/// </summary>
		private static async Task<ProbeResult> ProbeReachable(string uri)
		{
			if (string.IsNullOrWhiteSpace(uri))
				return new ProbeResult { Reason = "资源地址为空" };

			if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Head, uri);
					req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
					using var resp = await ProbeClient
						.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
					var code = (int)resp.StatusCode;
					if (code >= 200 && code < 400)
						return new ProbeResult { Ok = true };
					// 部分服务器不支持 HEAD，退化成只取一个字节
					if (code == 405 || code == 501)
						return await ProbeWithRange(uri).ConfigureAwait(false);
					return new ProbeResult { Reason = "服务器返回 HTTP " + code };
				}
				catch (TaskCanceledException)
				{
					return new ProbeResult { Reason = "连接超时" };
				}
				catch (Exception ex)
				{
					return new ProbeResult { Reason = "连接失败（" + ex.GetType().Name + "）" };
				}
			}

			// 本地文件：PlayUri 可能是 file:// 或裸路径
			var localPath = uri;
			if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
				localPath = parsed.LocalPath;

			if (localPath.StartsWith("/", StringComparison.Ordinal)
				|| (!uri.Contains("://") && localPath.IndexOfAny(new[] { '/', '\\' }) >= 0))
			{
				if (!File.Exists(localPath))
					return new ProbeResult { Reason = "文件不存在" };
				try
				{
					if (new System.IO.FileInfo(localPath).Length == 0)
						return new ProbeResult { Reason = "文件是 0 字节" };
					return new ProbeResult { Ok = true };
				}
				catch (Exception ex)
				{
					return new ProbeResult { Reason = "无法读取文件（" + ex.GetType().Name + "）" };
				}
			}

			return new ProbeResult { Ok = true };
		}

		private static async Task<ProbeResult> ProbeWithRange(string uri)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, uri);
				req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
				using var resp = await ProbeClient
					.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				var code = (int)resp.StatusCode;
				if (code >= 200 && code < 400)
					return new ProbeResult { Ok = true };
				return new ProbeResult { Reason = "服务器返回 HTTP " + code };
			}
			catch (TaskCanceledException)
			{
				return new ProbeResult { Reason = "连接超时" };
			}
			catch (Exception ex)
			{
				return new ProbeResult { Reason = "连接失败（" + ex.GetType().Name + "）" };
			}
		}

		private static string Describe(PlaylistItem item)
		{
			var res = item.AudioResource;
			var title = (res.ResourceTitle ?? "").Replace("\ufeff", "").Trim();
			if (!string.IsNullOrEmpty(title) && title != res.ResourceId)
				return title;
			var id = res.ResourceId ?? "";
			if (id.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				var host = id.Split("//", 2)[^1].Split('/')[0];
				return "网络音频（" + host + "）";
			}
			var file = Path.GetFileName(id);
			if (file.Length > 0)
			{
				var dot = file.LastIndexOf('.');
				return dot > 0 ? file.Substring(0, dot) : file;
			}
			return title.Length > 0 ? title : "（无标题）";
		}

		private static string Shorten(string text)
			=> text.Length <= 60 ? text : text.Substring(0, 57) + "…";

		// ── 本地控制通道 ────────────────────────────────────────────
		// 沙箱内的模型通过 ts3ab-ctl 调它。只接受回环连接，靠每次启动重新
		// 生成的随机 token 认证；命令直接调用 bot 自己的管理器，与聊天里
		// !play / !pause 走同一套逻辑，不直改任何状态文件。

		private TcpListener? controlListener;
		private CancellationTokenSource? controlCts;
		private Task? controlTask;

		private void StartControlChannel()
		{
			try
			{
				var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
				File.WriteAllText(ControlTokenFile, token);
				try { File.SetUnixFileMode(ControlTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
				catch { /* 非 Unix 或权限不足，忽略 */ }

				controlCts = new CancellationTokenSource();
				controlListener = new TcpListener(IPAddress.Loopback, ControlPort);
				controlListener.Start();
				var ct = controlCts.Token;
				controlTask = Task.Run(() => ControlLoop(controlListener, token, ct));
				Log.Info("ChatBridge 控制通道已启动：127.0.0.1:{0}（token 见 {1}）",
					ControlPort, ControlTokenFile);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "ChatBridge: 控制通道启动失败（端口 {0}）", ControlPort);
			}
		}

		private void StopControlChannel()
		{
			try { controlCts?.Cancel(); } catch { }
			try { controlListener?.Stop(); } catch { }
			try { controlTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
			try { if (File.Exists(ControlTokenFile)) File.Delete(ControlTokenFile); } catch { }
		}

		private async Task ControlLoop(TcpListener listener, string token, CancellationToken ct)
		{
			while (!ct.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
				}
				catch (ObjectDisposedException) { break; }
				catch (SocketException) when (ct.IsCancellationRequested) { break; }
				catch (Exception ex)
				{
					Log.Debug(ex, "ChatBridge: 控制通道 accept 失败");
					continue;
				}

				_ = Task.Run(() => HandleControlClient(client, token), CancellationToken.None);
			}
		}

		private async Task HandleControlClient(TcpClient client, string token)
		{
			using (client)
			{
				try
				{
					client.ReceiveTimeout = 10000;
					client.SendTimeout = 10000;
					using var stream = client.GetStream();
					using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
					using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

					var line = await reader.ReadLineAsync().ConfigureAwait(false);
					if (line == null)
						return;

					var req = JsonConvert.DeserializeObject<ControlRequest>(line);
					if (req == null || !FixedTimeEquals(req.Token, token))
					{
						await writer.WriteLineAsync(JsonConvert.SerializeObject(
							new ControlResponse { Ok = false, Error = "unauthorized" })).ConfigureAwait(false);
						return;
					}

					ControlResponse resp;
					try
					{
						resp = new ControlResponse
						{
							Ok = true,
							Result = await ExecuteControlCommand(req.Cmd ?? "", req.Arg ?? "").ConfigureAwait(false),
						};
					}
					catch (Exception ex)
					{
						Log.Warn(ex, "ChatBridge: 控制命令失败（{0}）", req.Cmd);
						resp = new ControlResponse { Ok = false, Error = ex.Message };
					}

					await writer.WriteLineAsync(JsonConvert.SerializeObject(resp)).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log.Debug(ex, "ChatBridge: 控制连接异常");
				}
			}
		}

		private static bool FixedTimeEquals(string? a, string b)
		{
			if (a == null) return false;
			var ba = Encoding.UTF8.GetBytes(a);
			var bb = Encoding.UTF8.GetBytes(b);
			return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
		}

		// 协议字段显式小写：客户端是脚本，不跟随 C# 命名习惯。
		private sealed class ControlRequest
		{
			[JsonProperty("token")] public string? Token { get; set; }
			[JsonProperty("cmd")] public string? Cmd { get; set; }
			[JsonProperty("arg")] public string? Arg { get; set; }
		}

		private sealed class ControlResponse
		{
			[JsonProperty("ok")] public bool Ok { get; set; }
			[JsonProperty("result")] public string? Result { get; set; }
			[JsonProperty("error")] public string? Error { get; set; }
		}

		private static readonly InvokerData AiInvoker = new InvokerData(Uid.Anonymous);

		/// <summary>
		/// 控制命令分发。全部调 bot 管理器，与聊天命令行为一致；
		/// 需要碰播放器的操作都过 DedicatedTaskScheduler，避免跨线程。
		/// </summary>
		private async Task<string> ExecuteControlCommand(string cmd, string arg)
		{
			switch (cmd)
			{
			case "help":
				return "status | play <link> | queue <link> | pause | resume | stop\n"
					+ "next | prev | seek <秒> | volume [0-100|+N|-N]\n"
					+ "playlist play <名字> | playlist queue <名字>";

			case "status":
				return DescribeStatus();

			case "play":
				RequireArg(cmd, arg);
				await scheduler.InvokeAsync(() => playManager.Play(AiInvoker, arg)).ConfigureAwait(false);
				return "开始播放：" + arg;

			case "queue":
				RequireArg(cmd, arg);
				await scheduler.InvokeAsync(() => playManager.Enqueue(AiInvoker, arg)).ConfigureAwait(false);
				return "已加入队列：" + arg;

			case "pause":
				await scheduler.Invoke(() => player.Paused = !player.Paused).ConfigureAwait(false);
				return player.Paused ? "已暂停。" : "已继续播放。";

			case "resume":
				await scheduler.Invoke(() => player.Paused = false).ConfigureAwait(false);
				return "已继续播放。";

			case "stop":
				await scheduler.InvokeAsync(() => playManager.Stop()).ConfigureAwait(false);
				return "已停止播放。";

			case "next":
				await scheduler.InvokeAsync(() => playManager.Next(AiInvoker)).ConfigureAwait(false);
				return "已切到下一首：" + DescribeStatus();

			case "prev":
				await scheduler.InvokeAsync(() => playManager.Previous(AiInvoker)).ConfigureAwait(false);
				return "已切到上一首：" + DescribeStatus();

			case "seek":
				RequireArg(cmd, arg);
				if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) || secs < 0)
					throw new InvalidOperationException("seek 需要非负秒数");
				await scheduler.InvokeAsync(() => player.Seek(TimeSpan.FromSeconds(secs))).ConfigureAwait(false);
				return "已跳到 " + secs + " 秒。";

			case "volume":
				RequireArg(cmd, arg);
				return await SetVolume(arg).ConfigureAwait(false);

			case "playlist":
				return await PlaylistControl(arg).ConfigureAwait(false);

			default:
				throw new InvalidOperationException("未知命令：" + cmd + "（用 help 看列表）");
			}
		}

		private static void RequireArg(string cmd, string arg)
		{
			if (string.IsNullOrWhiteSpace(arg))
				throw new InvalidOperationException(cmd + " 需要一个参数");
		}

		private string DescribeStatus()
		{
			var sb = new StringBuilder();
			var cur = playManager.CurrentPlayData;
			if (cur == null)
				sb.Append("当前没有播放。");
			else
			{
				var title = cur.PlayResource?.AudioResource?.ResourceTitle;
				if (string.IsNullOrWhiteSpace(title))
					title = cur.PlayResource?.PlayUri ?? "（未知曲目）";
				sb.Append("正在播放：").Append(title);
				sb.Append(player.Paused ? "（已暂停）" : "（播放中）");
			}
			sb.Append("\n音量：").Append(player.Volume.ToString("0.#"));
			var pos = player.Position;
			if (pos.HasValue)
				sb.Append("　进度：").Append(FormatTime(pos.Value));
			return sb.ToString();
		}

		private static string FormatTime(TimeSpan t)
			=> t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

		private async Task<string> SetVolume(string arg)
		{
			var current = player.Volume;
			double target;
			if (arg.StartsWith("+", StringComparison.Ordinal) || arg.StartsWith("-", StringComparison.Ordinal))
			{
				if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
					throw new InvalidOperationException("音量增量格式不对");
				target = current + delta;
			}
			else
			{
				if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
					throw new InvalidOperationException("音量需要 0-100 的数字");
			}

			target = Math.Clamp(target, 0, 100);
			await scheduler.Invoke(() => player.Volume = (float)target).ConfigureAwait(false);
			return "音量已设为 " + target.ToString("0.#") + "。";
		}

		private async Task<string> PlaylistControl(string arg)
		{
			var parts = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2)
				throw new InvalidOperationException("用法：playlist play|queue <歌单名>");

			var action = parts[0];
			var name = parts[1].Trim();
			var load = playlistManager.LoadPlaylist(name);
			if (!load.Ok)
				return "没有这个歌单：" + name;
			var items = load.Value.Items;
			if (items.Count == 0)
				return "歌单「" + name + "」是空的。";

			switch (action)
			{
			case "play":
				await scheduler.InvokeAsync(() => playManager.Play(AiInvoker, items, 0)).ConfigureAwait(false);
				return "开始播歌单「" + name + "」（" + items.Count + " 首）。";
			case "queue":
				await scheduler.InvokeAsync(() => playManager.Enqueue(AiInvoker, items)).ConfigureAwait(false);
				return "已把歌单「" + name + "」（" + items.Count + " 首）加入队列。";
			default:
				throw new InvalidOperationException("未知 playlist 子命令：" + action);
			}
		}


		/// <summary>
		/// 跑一轮 codewhale。已有会话就 resume，会话文件丢失（被 24 小时清理）时
		/// 自动回退成新建会话，并用完整策略重新提问。
		/// </summary>
		private async Task<string> RunCodewhale(ChatSession session, bool isPublic, string speaker, string question)
		{
			string? resumeId;
			lock (session)
				resumeId = session.Id;

			if (resumeId != null)
			{
				var resumed = await Execute(resumeId, BuildPrompt(speaker, question, isPublic, withPolicy: false))
					.ConfigureAwait(false);
				if (!resumed.SessionNotFound)
				{
					UpdateSession(session, resumed.SessionId);
					return resumed.Answer;
				}

				Log.Info("ChatBridge: 会话 {0} 已不存在，改为新建", resumeId);
				List<TurnRecord>? lostTurns = null;
				lock (session)
				{
					if (session.Id == resumeId)
					{
						// 会话文件被清理或 codewhale 重启，旧上下文已丢失；
						// 先把缓冲区里的问答精炼掉，再换新会话。
						lostTurns = DetachTurnsLocked(session);
						session.Id = null;
					}
				}
				if (lostTurns != null)
					QueueDistillation(session.Key, lostTurns);
			}

			var fresh = await Execute(null, BuildPrompt(speaker, question, isPublic, withPolicy: true))
				.ConfigureAwait(false);
			UpdateSession(session, fresh.SessionId);
			if (fresh.SessionId != null)
				Log.Info("ChatBridge: 新会话 {0}", fresh.SessionId);
			return fresh.Answer;
		}

		private sealed class ExecResult
		{
			public string Answer = string.Empty;
			public string? SessionId;
			public bool SessionNotFound;
		}

		private async Task<ExecResult> Execute(string? resumeId, string prompt)
		{
			var psi = new ProcessStartInfo(SandboxPath)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			psi.ArgumentList.Add(CodewhalePath);
			psi.ArgumentList.Add("exec");
			if (resumeId != null)
			{
				psi.ArgumentList.Add("--resume");
				psi.ArgumentList.Add(resumeId);
			}
			psi.ArgumentList.Add("--auto");
			psi.ArgumentList.Add("--sandbox");
			psi.ArgumentList.Add("workspace-write");
			psi.ArgumentList.Add("--allowed-tools");
			psi.ArgumentList.Add(AllowedTools);
			psi.ArgumentList.Add("--max-turns");
			psi.ArgumentList.Add(MaxTurns.ToString());
			psi.ArgumentList.Add("--");
			psi.ArgumentList.Add(prompt);

			using var proc = new Process { StartInfo = psi };
			if (!proc.Start())
				return new ExecResult { Answer = "无法启动沙箱进程。" };

			proc.StandardInput.Close();
			var stdoutTask = proc.StandardOutput.ReadToEndAsync();
			var stderrTask = proc.StandardError.ReadToEndAsync();
			var exitTask = WaitForExitAsync(proc);

			var finished = await Task.WhenAny(exitTask, Task.Delay(RunTimeout)).ConfigureAwait(false);
			if (finished != exitTask)
			{
				// bwrap 是这个 PID 命名空间里的 init，杀掉它即清理整棵子树。
				try { proc.Kill(); } catch { /* 已退出 */ }
				Log.Warn("ChatBridge: codewhale 超过 {0}s，已终止", RunTimeout.TotalSeconds);
				return new ExecResult { Answer = "这次思考超时了，换个更具体的问题试试。" };
			}

			var stdout = await SafeResult(stdoutTask).ConfigureAwait(false);
			var stderr = await SafeResult(stderrTask).ConfigureAwait(false);
			var exitCode = proc.ExitCode;

			if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
			{
				// 会话文件被清理掉时 codewhale 会报这个错，交给上层重建。
				if (stderr.IndexOf("No session found", StringComparison.OrdinalIgnoreCase) >= 0)
					return new ExecResult { SessionNotFound = true };

				Log.Warn("ChatBridge: codewhale 退出码 {0}，stderr: {1}", exitCode, Truncate(stderr, 800));
				return new ExecResult { Answer = "沙箱里的 codewhale 报错了，详情见服务器日志。" };
			}

			return new ExecResult
			{
				Answer = CleanOutput(stdout),
				// 会话标识走的是 stderr（与工具 trace 同一个流），stdout 只有回答正文。
				SessionId = ExtractSessionId(stdout + "\n" + stderr),
			};
		}

		private static string SessionKey(TextMessage message, string who)
			=> message.Target == TextMessageTargetMode.Private ? "dm:" + who : "public";

		/// <summary>
		/// 把当前发言者写给沙箱内的 ts3ab-mem，使它只能读写本人的记忆分区。
		/// 失败不影响对话，但会记 WARN；下一轮会重试。
		/// </summary>
		private static void MarkSpeaker(string who)
		{
			try
			{
				File.WriteAllText(SpeakerFile, who);
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "ChatBridge: 写入发言者标记失败（{0}），记忆将回退到全局", SpeakerFile);
			}
		}

		private static string SpeakerLine(TextMessage message, string who)
		{
			var where = message.Target == TextMessageTargetMode.Private ? "私聊"
				: message.Target == TextMessageTargetMode.Server ? "服务器消息" : "频道";
			var id = who.Length <= 8 ? who : who.Substring(0, 8);
			return "[发言者] " + message.InvokerName + "（uid " + id + "…，来自" + where + "）";
		}

		private static string BuildPrompt(string speaker, string question, bool isPublic, bool withPolicy)
		{
			var sb = new StringBuilder();
			sb.Append(withPolicy
				? (isPublic ? PublicPolicy : PrivatePolicy)
				: (isPublic ? PublicReminder : PrivateReminder));
			sb.Append('\n').Append(speaker);
			sb.Append('\n').Append("问题：").Append(question);
			return sb.ToString();
		}

		/// <summary>
		/// 取得 key 对应的会话。倒计时只对<strong>已建立</strong>的会话有意义：
		/// 起点是它第一次成功拿到 codewhale session id 的那条消息，而不是进程启动
		/// 时刻、也不是任何固定时间点。尚未建立（Id 为空）时不做任何时间判定。
		/// </summary>
		private ChatSession GetSession(string key, bool isPublic)
		{
			var now = DateTime.UtcNow;
			var session = sessions.GetOrAdd(key,
				k => new ChatSession { IsPublic = isPublic, Key = k });
			List<TurnRecord>? expiredTurns = null;
			lock (session)
			{
				if (session.Id != null)
				{
					var expired = isPublic
						? now - session.StartedUtc >= PublicSessionLifetime
						: now - session.LastUsedUtc >= PrivateSessionIdle;
					if (expired)
					{
						Log.Info("ChatBridge: 会话 {0} 到期重置（{1}，起于 {2:HH:mm:ss}）", key,
							isPublic ? "公共会话 6 小时" : "私聊空闲 24 小时",
							isPublic ? session.StartedUtc.ToLocalTime() : session.LastUsedUtc.ToLocalTime());
						// 在锁内只做摘取，精炼与写库放到锁外、后台执行，
						// 不拖慢触发重置的这条新消息。
						expiredTurns = DetachTurnsLocked(session);
						session.Id = null;
					}
				}
				session.LastUsedUtc = now;
			}
			if (expiredTurns != null)
				QueueDistillation(key, expiredTurns);
			return session;
		}

		private static void UpdateSession(ChatSession session, string? id)
		{
			if (id == null)
				return;
			var now = DateTime.UtcNow;
			lock (session)
			{
				// 会话首次建立（或 id 变了说明是另一条会话）时，才从这里开始计时。
				// 续接同一个 id 不重置窗口，否则 6 小时会退化成永不重置。
				if (session.Id != id)
					session.StartedUtc = now;
				session.Id = id;
				session.LastUsedUtc = now;
			}
		}

		/// <summary>记下成功的一轮问答，等会话重置时一并精炼。</summary>
		private static void RecordTurn(ChatSession session, string uid, string name,
			string question, string answer)
		{
			lock (session)
			{
				session.Turns.Add(new TurnRecord
				{
					Uid = uid,
					Name = name,
					Question = question,
					Answer = answer,
				});
				if (session.Turns.Count > TurnBufferLimit)
					session.Turns.RemoveRange(0, session.Turns.Count - TurnBufferLimit);
			}
		}

		/// <summary>把待精炼轮次摘走（调用方持有 session 锁）。</summary>
		private static List<TurnRecord> DetachTurnsLocked(ChatSession session)
		{
			var copy = new List<TurnRecord>(session.Turns);
			session.Turns.Clear();
			return copy;
		}

		/// <summary>后台精炼并写库。不占用对话闸门，也不阻塞触发重置的消息。</summary>
		private void QueueDistillation(string sessionKey, List<TurnRecord> turns)
		{
			if (turns.Count == 0)
				return;

			_ = Task.Run(async () =>
			{
				await distillGate.WaitAsync().ConfigureAwait(false);
				try
				{
					await DistillAndStore(sessionKey, turns).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "ChatBridge: 会话 {0} 的记忆精炼任务失败", sessionKey);
				}
				finally
				{
					distillGate.Release();
				}
			});
		}

		/// <summary>插件停用前把所有会话里尚未精炼的轮次排入后台。</summary>
		private void FlushPendingTurns()
		{
			foreach (var pair in sessions)
			{
				List<TurnRecord> turns;
				lock (pair.Value)
					turns = DetachTurnsLocked(pair.Value);
				if (turns.Count > 0)
					QueueDistillation(pair.Key, turns);
			}
		}

		/// <summary>
		/// 按发言者分组精炼：公共会话里多人共用一条会话，记忆只能落到各自的
		/// chat:&lt;uid8&gt; 分区。发言者过多时只处理轮数最多的前 8 个，避免一次
		/// 重置打出几十个模型请求。
		/// </summary>
		private async Task DistillAndStore(string sessionKey, List<TurnRecord> turns)
		{
			var groups = new Dictionary<string, List<TurnRecord>>(StringComparer.Ordinal);
			foreach (var turn in turns)
			{
				if (string.IsNullOrWhiteSpace(turn.Uid))
					continue;
				if (!groups.TryGetValue(turn.Uid, out var list))
					groups[turn.Uid] = list = new List<TurnRecord>();
				list.Add(turn);
			}

			var ordered = new List<KeyValuePair<string, List<TurnRecord>>>(groups);
			ordered.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
			if (ordered.Count > 8)
			{
				Log.Warn("ChatBridge: 会话 {0} 有 {1} 位发言者，本次只精炼轮数最多的 8 位",
					sessionKey, ordered.Count);
				ordered.RemoveRange(8, ordered.Count - 8);
			}

			foreach (var pair in ordered)
			{
				var uid = pair.Key;
				var uidShort = uid.Length <= 4 ? uid : uid.Substring(0, 4);
				try
				{
					var transcript = BuildTranscript(pair.Value);
					if (transcript.Length == 0)
						continue;

					var lines = await Distill(transcript).ConfigureAwait(false);
					var stored = 0;
					foreach (var line in lines)
					{
						if (await StoreMemory(uid, line).ConfigureAwait(false))
							stored++;
					}
					Log.Info("ChatBridge: 会话 {0} 重置，uid {1}.. 精炼 {2} 条，写入 {3} 条",
						sessionKey, uidShort, lines.Count, stored);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "ChatBridge: 会话 {0} 的精炼失败（uid {1}..）", sessionKey, uidShort);
				}
			}
		}

		/// <summary>取最近若干轮拼成精炼输入，总长受限。</summary>
		private static string BuildTranscript(List<TurnRecord> turns)
		{
			var sb = new StringBuilder();
			var start = Math.Max(0, turns.Count - DistillMaxTurns);
			for (var i = start; i < turns.Count; i++)
			{
				sb.Append("Q: ").Append(turns[i].Question).Append('\n');
				sb.Append("A: ").Append(turns[i].Answer).Append('\n');
				if (sb.Length >= DistillMaxChars)
					break;
			}
			if (sb.Length > DistillMaxChars)
			{
				var cut = DistillMaxChars;
				// 不把代理对切成两半，否则 JSON/模型输入会出现孤立码元。
				if (char.IsHighSurrogate(sb[cut - 1]))
					cut--;
				sb.Length = cut;
			}
			return sb.ToString();
		}

		/// <summary>跑一次无工具的 one-shot 精炼，结果不落 codewhale 会话文件。</summary>
		private async Task<List<string>> Distill(string transcript)
		{
			var prompt = DistillPrompt + transcript;
			var args = new List<string> { CodewhalePath, "exec", "--", prompt };
			var result = await RunProcess(SandboxPath, args, DistillTimeout).ConfigureAwait(false);
			if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
			{
				if (!string.IsNullOrWhiteSpace(result.Stderr))
					Log.Warn("ChatBridge: 记忆精炼调用失败（exit {0}）：{1}",
						result.ExitCode, Truncate(result.Stderr.Trim(), 300));
				return new List<string>();
			}
			return ParseDistilledLines(result.Stdout);
		}

		/// <summary>宽容解析模型输出：剥列表前缀、去引号，最多保留 DistillMaxItems 条。</summary>
		private static List<string> ParseDistilledLines(string raw)
		{
			var lines = new List<string>();
			foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
			{
				var line = DistillListPrefix.Replace(rawLine.Trim(), string.Empty).Trim();
				line = line.Trim('"', '\'', '`', '“', '”', '「', '」').Trim();
				if (line.Length == 0)
					continue;
				if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)
					|| line == "无" || line == "没有" || line == "没有值得记住的内容")
					continue;
				if (line.Length > 200)
					line = line.Substring(0, 200);
				lines.Add(line);
				if (lines.Count >= DistillMaxItems)
					break;
			}
			return lines;
		}

		/// <summary>直接调 mem.py 写库（不经 ts3ab-mem，避免 .speaker 竞态）。</summary>
		private async Task<bool> StoreMemory(string uid, string content)
		{
			var project = "chat:" + (uid.Length <= 8 ? uid : uid.Substring(0, 8));
			var args = new List<string>
			{
				MemScript,
				"--db", MemDb,
				"--scope-uid", uid,
				"add",
				"-c", content,
				"-p", project,
				"-k", "note",
				"--tags", "auto-distill",
				"--source", "chatbridge-distill",
				"--status", "confirmed",
			};
			var result = await RunProcess(MemPython, args, MemWriteTimeout).ConfigureAwait(false);
			if (result.ExitCode != 0)
			{
				Log.Warn("ChatBridge: 写入记忆失败（exit {0}）：{1}",
					result.ExitCode, Truncate(result.Stderr.Trim(), 300));
				return false;
			}
			return true;
		}

		private sealed class ProcessResult
		{
			public int ExitCode;
			public string Stdout = string.Empty;
			public string Stderr = string.Empty;
		}

		/// <summary>跑一个外部进程并限时；超时即杀掉，不阻塞调用方。</summary>
		private static async Task<ProcessResult> RunProcess(
			string file, IReadOnlyList<string> args, TimeSpan timeout)
		{
			var psi = new ProcessStartInfo(file)
			{
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};
			foreach (var arg in args)
				psi.ArgumentList.Add(arg);

			try
			{
				using var proc = new Process { StartInfo = psi };
				if (!proc.Start())
					return new ProcessResult { ExitCode = -1, Stderr = "无法启动进程" };

				// 提示词全部走参数，不需要 stdin；关闭它，避免子进程误等输入。
				proc.StandardInput.Close();
				var stdoutTask = proc.StandardOutput.ReadToEndAsync();
				var stderrTask = proc.StandardError.ReadToEndAsync();
				var exitTask = WaitForExitAsync(proc);

				var finished = await Task.WhenAny(exitTask, Task.Delay(timeout)).ConfigureAwait(false);
				if (finished != exitTask)
				{
					try { proc.Kill(); } catch { /* 已退出 */ }
					// 等管道收尾，避免读取任务在 using 释放后异常。
					await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
					return new ProcessResult { ExitCode = -1, Stderr = "超时" };
				}

				return new ProcessResult
				{
					ExitCode = proc.ExitCode,
					Stdout = await SafeResult(stdoutTask).ConfigureAwait(false),
					Stderr = await SafeResult(stderrTask).ConfigureAwait(false),
				};
			}
			catch (Exception ex)
			{
				return new ProcessResult { ExitCode = -1, Stderr = ex.Message };
			}
		}

		private sealed class ChatSession
		{
			public string? Id;
			/// <summary>会话表里的 key（dm:&lt;uid&gt; 或 public），精炼日志与写库分区用。</summary>
			public string Key = string.Empty;
			/// <summary>当前会话建立成功（拿到 session id）的时刻，公共会话 6 小时窗口的起点。</summary>
			public DateTime StartedUtc;
			public DateTime LastUsedUtc;
			public bool IsPublic;
			/// <summary>本轮会话尚未精炼的问答。重置时整体摘走，避免重复写入。</summary>
			public readonly List<TurnRecord> Turns = new List<TurnRecord>();
		}

		private sealed class TurnRecord
		{
			public string Uid = string.Empty;
			public string Name = string.Empty;
			public string Question = string.Empty;
			public string Answer = string.Empty;
		}

		private static Task<string> SafeResult(Task<string> t)
			=> t.ContinueWith(x => x.Status == TaskStatus.RanToCompletion ? x.Result : string.Empty,
				TaskContinuationOptions.ExecuteSynchronously);

		private static Task WaitForExitAsync(Process proc)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (proc.HasExited)
			{
				tcs.TrySetResult(true);
				return tcs.Task;
			}
			proc.EnableRaisingEvents = true;
			proc.Exited += (sender, args) => tcs.TrySetResult(true);
			return tcs.Task;
		}

		// codewhale 新建会话时输出 `session: <id>`，恢复时额外先说 `resumed session: <id>`。
		// 两行都要从聊天输出里去掉，但 id 要先抓下来留给下一轮 resume。
		private static readonly Regex SessionLine =
			new Regex(@"(?m)^(?:resumed )?session:\s*([0-9a-fA-F]{8,})\s*$", RegexOptions.Compiled);

		private static string? ExtractSessionId(string raw)
		{
			string? id = null;
			foreach (Match match in SessionLine.Matches(raw))
				id = match.Groups[1].Value;
			return id;
		}

		/// <summary>把 codewhale 的终端输出裁成适合 TeamSpeak 显示的一行一段纯文本。</summary>
		private static string CleanOutput(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return string.Empty;

			var text = Regex.Replace(raw, @"(?m)^(?:resumed )?session:\s*[0-9a-fA-F]+\s*$", string.Empty); // 会话标识
			text = Regex.Replace(text, @"```[a-zA-Z0-9]*", string.Empty);                    // 代码围栏
			text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);               // 标题井号
			text = text.Replace("**", string.Empty).Replace("`", string.Empty);
			text = Regex.Replace(text, @"\n{3,}", "\n\n");

			var lines = new List<string>();
			foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
			{
				var trimmed = line.TrimEnd();
				if (trimmed.Length == 0 && (lines.Count == 0 || lines[lines.Count - 1].Length == 0))
					continue;
				lines.Add(trimmed);
			}

			var result = string.Join("\n", lines).Trim();
			return result.Length > 1600 ? result.Substring(0, 1600) + " …" : result;
		}

		/// <summary>
		/// 思考期间把头像换成提示图，结束（含异常/超时）后交回 bot 自己恢复。
		/// 失败只记 Debug，绝不影响回复本身。
		/// </summary>
		private async Task SetThinkingAvatar(bool thinking)
		{
			var started = Stopwatch.StartNew();
			try
			{
				if (thinking)
				{
					// UploadAvatar 走 SendCommandBase，必须在专用调度线程上执行。
					await scheduler.InvokeAsync(async () =>
					{
						using var stream = new MemoryStream(ThinkingAvatar, writable: false);
						await ts3.UploadAvatar(stream).ConfigureAwait(false);
					}).ConfigureAwait(false);
					Log.Info("ChatBridge: 头像→思考（{0}ms，{1}B）",
						started.ElapsedMilliseconds, ThinkingAvatar.Length);
				}
				else
				{
					// 交回 bot 的原生逻辑：播放中恢复曲目封面，未播放恢复休眠图。
					// 这与"从没被插件改过"时应有的头像完全一致。
					await bot.GenerateStatusImage(playManager.IsPlaying, playManager.CurrentPlayData)
						.ConfigureAwait(false);
					Log.Info("ChatBridge: 头像→恢复（{0}ms）", started.ElapsedMilliseconds);
				}
			}
			catch (Exception ex)
			{
				// 以前记 Debug，而 NLog 的 minlevel 是 Info，失败会被完全吞掉。
				// 头像失败不影响回复，所以只警告不抛。
				Log.Warn(ex, "ChatBridge: 头像切换（{0}）失败，{1}ms",
					thinking ? "思考" : "恢复", started.ElapsedMilliseconds);
			}
		}

		private async Task Reply(TextMessage source, string text)
		{
			// TsFullClient.SendCommandBase 会调 VerifyOwnThread()，非专用线程直接抛异常。
			// 每段单独调度：await 之后 continuation 会回到线程池，若只在最外层包一次，
			// 第二段起就会在错误线程上发送。
			foreach (var chunk in SplitUtf8(text, ReplyChunkBytes))
			{
				try
				{
					await scheduler.InvokeAsync(() => SendOne(source, chunk)).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "ChatBridge 发送回复失败");
				}
			}
		}

		private Task SendOne(TextMessage source, string chunk)
		{
			switch (source.Target)
			{
			case TextMessageTargetMode.Private:
				return ts3.SendMessage(chunk, source.InvokerId);
			case TextMessageTargetMode.Server:
				return ts3.SendServerMessage(chunk);
			default:
				return ts3.SendChannelMessage(chunk);
			}
		}

		/// <summary>按 UTF-8 字节数切片，不切断代理对与汉字。</summary>
		private static IEnumerable<string> SplitUtf8(string text, int maxBytes)
		{
			var sb = new StringBuilder();
			var bytes = 0;
			var i = 0;
			while (i < text.Length)
			{
				var len = 1;
				if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
					len = 2;

				var slice = text.Substring(i, len);
				var cost = Encoding.UTF8.GetByteCount(slice);
				if (bytes + cost > maxBytes && sb.Length > 0)
				{
					yield return sb.ToString();
					sb.Clear();
					bytes = 0;
				}

				sb.Append(slice);
				bytes += cost;
				i += len;
			}

			if (sb.Length > 0)
				yield return sb.ToString();
		}

		/// <summary>只保留最近一天的会话痕迹，避免沙箱 HOME 无限增长。</summary>
		private void PruneSessions()
		{
			try
			{
				var dir = Path.Combine(HomeDir, "sessions");
				if (Directory.Exists(dir))
				{
					var cutoff = DateTime.UtcNow.AddHours(-24);
					foreach (var file in new DirectoryInfo(dir).GetFiles("*.json"))
					{
						if (file.LastWriteTimeUtc >= cutoff)
							continue;
						try { file.Delete(); }
						catch (IOException) { /* 正被占用，下次再说 */ }
					}
				}

				// 同步收缩内存里的会话表，否则长期运行会持续堆积。
				// 尚未建立的空壳只按最后活动时间回收，不参与窗口判定。
				var now = DateTime.UtcNow;
				foreach (var pair in sessions)
				{
					bool expired;
					List<TurnRecord>? turns = null;
					lock (pair.Value)
					{
						expired = pair.Value.Id == null
							? now - pair.Value.LastUsedUtc >= PrivateSessionIdle
							: pair.Value.IsPublic
								? now - pair.Value.StartedUtc >= PublicSessionLifetime
								: now - pair.Value.LastUsedUtc >= PrivateSessionIdle;
						if (expired)
							turns = DetachTurnsLocked(pair.Value);
					}
					if (!expired)
						continue;
					if (sessions.TryRemove(pair.Key, out _) && turns != null)
						QueueDistillation(pair.Key, turns);
				}
			}
			catch (Exception ex)
			{
				Log.Debug(ex, "ChatBridge: 清理旧会话失败");
			}
		}

		private static string Truncate(string value, int max)
			=> value.Length <= max ? value : value.Substring(0, max);

		public void Dispose()
		{
			// 插件停用/热重载前尽力把未精炼的轮次冲出去；进程直接退出时可能来不及，
			// 属于 best-effort，不影响主流程。
			FlushPendingTurns();
			StopControlChannel();
			ts3.OnMessageReceived -= OnMessageReceived;
			try { commands.UnregisterCollection(rightsBag); }
			catch (Exception ex) { Log.Debug(ex, "ChatBridge: 注销 right 失败"); }
			gate.Dispose();
			Log.Info("ChatBridge 已停用");
		}

		/// <summary>
		/// 只用来向命令系统宣告一个 right 名，不带任何可调用命令。
		/// 这样 rights.toml 里的通配符能展开到它，而聊天里看不到多余命令。
		/// </summary>
		private sealed class RightsBag : ICommandBag
		{
			public IReadOnlyCollection<BotCommand> BagCommands { get; } = Array.Empty<BotCommand>();
			public IReadOnlyCollection<string> AdditionalRights { get; }

			public RightsBag(params string[] rights) => AdditionalRights = rights;
		}
	}
}
