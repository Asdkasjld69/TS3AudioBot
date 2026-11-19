// Bilibili audio resolver for TS3AudioBot
//
// 解析 B 站视频地址，取出 DASH 音频轨（m4s），交给 FfmpegProducer 解码播放。
//
// 为什么是 BotPlugin 而不是 Factory：
//   TS3AudioBot 的 Factory 插件只被 Load 成 Ready 状态，Bot.cs 启动时仅自动
//   Start BotPlugin / CorePlugin / Commands，因此纯 Factory 必须每次用
//   !plugin load 手动注册，重启即失效。这里改用 BotPlugin，在 Initialize 里
//   把内部解析器挂到 ResourceResolver 上，随 bot 自动装载。
//   内部解析器是 internal 类型，不会被 GetExportedTypes 当成第二个 Factory。
//
// 原理（与 snowflake-hangdudu/bili-downloader 一致，但不走页面解析）：
//   1. /x/web-interface/view       -> title / aid / pages[].cid
//   2. /x/player/playurl (fnval=16) -> dash.audio[]（AAC，30216/30232/30280）
//   3. 选 id 最大的 AAC 轨，得到带 deadline 签名的 m4s 直链
//   4. 该直链必须带 Referer + 浏览器 UA 才能被 CDN 接受，否则 403
//
// 因此需要同时配置（见同目录 ts3audiobot.toml）：
//   [tools.ffmpeg]
//   extra_input_args = "-headers \"Referer: https://www.bilibili.com/\" -user_agent \"Mozilla/5.0 ...\""
//
// 注意：
//   - 直链有效期约 2 小时，绝不入库；ResourceId 只存 "bvid|cid"，播放时重新解析。
//   - 播放的是纯音频轨，不需要把视频轨合并回来。

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using TS3AudioBot;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;

namespace BilibiliResolver
{
	/// <summary>
	/// BotPlugin 外壳：只负责把 <see cref="BilibiliResolverImpl"/> 注册进解析器链。
	/// 依赖注入拿到的 <see cref="ResolveContext"/> 指向本 bot 的 ResourceResolver。
	/// </summary>
	public sealed class BilibiliResolverPlugin : IBotPlugin
	{
		// NLog.config 只放行 TS3AudioBot.* / TSLib.*，用 TS3AudioBot 前缀才能落盘。
		private static readonly Logger Log = LogManager.GetLogger("TS3AudioBot.Plugins.BilibiliResolver");

		private readonly ResolveContext context;
		private BilibiliResolverImpl? resolver;
		private int initialized;

		public BilibiliResolverPlugin(ResolveContext context)
		{
			this.context = context ?? throw new ArgumentNullException(nameof(context));
		}

		public void Initialize()
		{
			if (Interlocked.Exchange(ref initialized, 1) != 0)
				return;

			resolver = new BilibiliResolverImpl();
			context.Resolver.AddResolver(resolver);
			Log.Info("Bilibili 解析器已注册：ResolverFor = {0}", resolver.ResolverFor);
		}

		public void Dispose()
		{
			var current = resolver;
			if (current == null)
				return;

			resolver = null;
			try
			{
				context.Resolver.RemoveResolver(current);
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "移除 Bilibili 解析器时出错");
			}
			current.Dispose();
		}
	}

	/// <summary>
	/// 真正的解析器。internal 是有意的：GetExportedTypes() 看不到它，
	/// 插件管理器的 Factory 计数才不会变成 2 而拒载整个文件。
	/// </summary>
	internal sealed class BilibiliResolverImpl : IResourceResolver, IThumbnailResolver
	{
		private const string UserAgent =
			"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
		private const string Referer = "https://www.bilibili.com/";
		private const string ApiBase = "https://api.bilibili.com";

		// https://www.bilibili.com/video/BV1pM4y1v71j  /  .../video/av911998091
		private static readonly Regex VideoIdMatch = new Regex(
			@"bilibili\.com/video/(?<id>BV[0-9A-Za-z]{10}|av\d+)",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// 分 P：?p=2 / &p=2
		private static readonly Regex PageParamMatch = new Regex(
			@"[?&]p=(?<p>\d+)",
			RegexOptions.Compiled);

		private readonly HttpClient http;
		private readonly SemaphoreSlim sessionLock = new SemaphoreSlim(1, 1);
		private volatile string? cookieHeader;

		public BilibiliResolverImpl()
		{
			var handler = new HttpClientHandler
			{
				AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
				// 手动拼 Cookie：buvid3 属于 .bilibili.com，需要跨 www / api 子域复用。
				UseCookies = false,
			};
			http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		}

		public string ResolverFor => "bilibili";

		// ---------------------------------------------------------------- resolver

		public MatchCertainty MatchResource(ResolveContext? _, string uri)
			=> VideoIdMatch.IsMatch(uri) ? MatchCertainty.Always : MatchCertainty.Never;

		public async Task<PlayResource> GetResource(ResolveContext? ctx, string uri)
		{
			var match = VideoIdMatch.Match(uri);
			if (!match.Success)
				throw new AudioBotException("无法识别的 B 站视频地址");

			var id = match.Groups["id"].Value;
			var page = 1;
			var pageMatch = PageParamMatch.Match(uri);
			if (pageMatch.Success)
				int.TryParse(pageMatch.Groups["p"].Value, out page);

			var view = await GetViewAsync(id).ConfigureAwait(false);
			var bvid = view.Value<string>("bvid") ?? id;
			var aid = view.Value<long>("aid");
			var title = view.Value<string>("title") ?? bvid;

			var pages = view["pages"] as JArray;
			if (pages == null || pages.Count == 0)
				throw new AudioBotException("该视频没有可播放的分 P");

			if (page < 1) page = 1;
			if (page > pages.Count) page = pages.Count;

			var pageObj = pages[page - 1];
			var cid = pageObj.Value<long>("cid");
			var partTitle = pageObj.Value<string>("part");

			var displayTitle = pages.Count > 1 && !string.IsNullOrWhiteSpace(partTitle)
				? string.Concat(title, " - P", page.ToString(), " ", partTitle)
				: title;

			return await BuildAsync(bvid, aid, cid, displayTitle).ConfigureAwait(false);
		}

		public async Task<PlayResource> GetResourceById(ResolveContext? _, AudioResource resource)
		{
			var parts = (resource.ResourceId ?? string.Empty).Split('|');
			var bvid = parts.Length > 0 ? parts[0] : string.Empty;
			if (string.IsNullOrEmpty(bvid))
				throw new AudioBotException("B 站资源标识为空");

			var view = await GetViewAsync(bvid).ConfigureAwait(false);
			var aid = view.Value<long>("aid");
			var title = resource.ResourceTitle ?? view.Value<string>("title") ?? bvid;

			long cid = 0;
			if (parts.Length > 1)
				long.TryParse(parts[1], out cid);
			if (cid <= 0)
			{
				var pages = view["pages"] as JArray;
				if (pages == null || pages.Count == 0)
					throw new AudioBotException("该视频没有可播放的分 P");
				cid = pages[0].Value<long>("cid");
			}

			return await BuildAsync(bvid, aid, cid, title).ConfigureAwait(false);
		}

		public string RestoreLink(ResolveContext? _, AudioResource resource)
		{
			var bvid = (resource.ResourceId ?? string.Empty).Split('|')[0];
			return string.IsNullOrEmpty(bvid)
				? "https://www.bilibili.com/"
				: "https://www.bilibili.com/video/" + bvid;
		}

		// ---------------------------------------------------------------- core

		private async Task<PlayResource> BuildAsync(string bvid, long aid, long cid, string title)
		{
			var playurl = await ApiGetAsync(
				string.Concat(
					"/x/player/playurl?avid=", aid.ToString(),
					"&cid=", cid.ToString(),
					"&qn=80&fnval=16&fnver=0&fourk=1&platform=pc"))
				.ConfigureAwait(false);

			var audioUrl = await PickAudioAsync(playurl).ConfigureAwait(false);
			if (audioUrl == null)
				throw new AudioBotException("B 站未返回可用音频流（可能需要登录或该视频受限）");

			long durationMs = 0;
			var timelength = playurl["timelength"];
			if (timelength != null)
				long.TryParse(timelength.ToString(), out durationMs);

			var audioResource = new AudioResource(
				string.Concat(bvid, "|", cid.ToString()), title, ResolverFor);
			var songInfo = new SongInfo
			{
				Title = title,
				Length = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : (TimeSpan?)null,
			};

			return new PlayResource(audioUrl, audioResource, songInfo: songInfo);
		}

		/// <summary>
		/// 选音轨：AAC 且 id 最大（30280 &gt; 30232 &gt; 30216）。
		/// 杜比 / Hi-Res 需要大会员且最终仍要重采样到 48kHz，收益为零，故不参与选择。
		/// dash 缺失时回退 durl（音画合一的低清单文件）。
		/// </summary>
		private async Task<string?> PickAudioAsync(JToken playurl)
		{
			var audioArray = playurl["dash"]?["audio"] as JArray;
			if (audioArray != null && audioArray.Count > 0)
			{
				JToken? best = null;
				var bestId = long.MinValue;

				foreach (var item in audioArray)
				{
					var codecs = item.Value<string>("codecs") ?? string.Empty;
					var id = item.Value<long?>("id") ?? 0;
					var isAac = codecs.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
						|| id == 30216 || id == 30232 || id == 30280;

					if (!isAac)
						continue;
					if (id > bestId)
					{
						bestId = id;
						best = item;
					}
				}

				best ??= audioArray[0];
				var url = await PickReachableAsync(best).ConfigureAwait(false);
				if (url != null)
					return url;
			}

			var durl = playurl["durl"] as JArray;
			if (durl != null && durl.Count > 0)
				return await PickReachableAsync(durl[0]).ConfigureAwait(false);

			return null;
		}

		/// <summary>先探主地址，失败再依次试 backupUrl；全挂也返回主地址，交给 ffmpeg 报错。</summary>
		private async Task<string?> PickReachableAsync(JToken? item)
		{
			if (item == null)
				return null;

			var primary = GetStreamUrl(item);
			if (primary == null)
				return null;

			if (await IsReachableAsync(primary).ConfigureAwait(false))
				return primary;

			var backups = item["backupUrl"] as JArray ?? item["backup_url"] as JArray;
			if (backups != null)
			{
				foreach (var backup in backups)
				{
					var url = backup.Value<string>();
					if (string.IsNullOrEmpty(url))
						continue;
					if (await IsReachableAsync(url).ConfigureAwait(false))
						return url;
				}
			}

			return primary;
		}

		private static string? GetStreamUrl(JToken? item)
		{
			if (item == null)
				return null;

			var url = item.Value<string>("baseUrl") ?? item.Value<string>("base_url");
			if (!string.IsNullOrEmpty(url))
				return url;

			var backups = item["backupUrl"] as JArray ?? item["backup_url"] as JArray;
			if (backups != null && backups.Count > 0)
				return backups[0].Value<string>();

			return null;
		}

		private async Task<bool> IsReachableAsync(string url)
		{
			try
			{
				using var request = BuildRequest(url, "bytes=0-0");
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				using var response = await http
					.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
					.ConfigureAwait(false);
				return response.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		// ---------------------------------------------------------------- http

		private async Task<JObject> GetViewAsync(string id)
		{
			var query = id.StartsWith("av", StringComparison.OrdinalIgnoreCase)
				? "aid=" + id.Substring(2)
				: "bvid=" + id;

			var data = await ApiGetAsync("/x/web-interface/view?" + query).ConfigureAwait(false);
			if (!(data is JObject obj))
				throw new AudioBotException("B 站视频信息格式异常");
			return obj;
		}

		private async Task<JToken> ApiGetAsync(string path)
		{
			await EnsureSessionAsync().ConfigureAwait(false);

			using var request = BuildRequest(ApiBase + path, null);
			request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

			using var response = await http.SendAsync(request).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
				throw new AudioBotException(string.Concat("B 站接口 HTTP ", ((int)response.StatusCode).ToString()));

			var json = JObject.Parse(body);
			var code = json.Value<int?>("code") ?? 0;
			if (code != 0)
				throw new AudioBotException(string.Concat(
					"B 站接口错误 code=", code.ToString(), ": ", json.Value<string>("message") ?? string.Empty));

			var data = json["data"];
			if (data == null)
				throw new AudioBotException("B 站接口未返回 data");
			return data;
		}

		private HttpRequestMessage BuildRequest(string url, string? range)
		{
			var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
			request.Headers.TryAddWithoutValidation("Referer", Referer);
			if (range != null)
				request.Headers.TryAddWithoutValidation("Range", range);

			var cookie = cookieHeader;
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", cookie);

			return request;
		}

		/// <summary>
		/// 取一次首页，把 buvid3 / b_nut 之类游客 cookie 记下来。
		/// /x/web-interface/view 在没有 buvid3 时会返回 412，playurl 则通常不需要。
		/// </summary>
		private async Task EnsureSessionAsync()
		{
			if (cookieHeader != null)
				return;

			await sessionLock.WaitAsync().ConfigureAwait(false);
			try
			{
				if (cookieHeader != null)
					return;

				var cookies = new List<string>();
				using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/"))
				{
					request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
					request.Headers.TryAddWithoutValidation(
						"Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

					using var response = await http
						.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
						.ConfigureAwait(false);

					if (response.Headers.TryGetValues("Set-Cookie", out var values))
					{
						foreach (var raw in values)
						{
							var pair = raw.Split(';')[0].Trim();
							if (pair.StartsWith("buvid3=", StringComparison.Ordinal)
								|| pair.StartsWith("buvid4=", StringComparison.Ordinal)
								|| pair.StartsWith("b_nut=", StringComparison.Ordinal)
								|| pair.StartsWith("buvid_fp=", StringComparison.Ordinal))
							{
								cookies.Add(pair);
							}
						}
					}
				}

				cookieHeader = cookies.Count > 0 ? string.Join("; ", cookies) : string.Empty;
			}
			catch
			{
				// 首页拿不到就继续不带 cookie 请求；view 之外的大多数接口仍可用。
				cookieHeader = string.Empty;
			}
			finally
			{
				sessionLock.Release();
			}
		}

		// ---------------------------------------------------------------- thumbnail

		public async Task GetThumbnail(ResolveContext? _, PlayResource playResource, Func<Stream, Task> action)
		{
			var bvid = (playResource.AudioResource.ResourceId ?? string.Empty).Split('|')[0];
			if (string.IsNullOrEmpty(bvid))
				throw new AudioBotException("缺少 B 站视频标识");

			var view = await GetViewAsync(bvid).ConfigureAwait(false);
			var pic = view.Value<string>("pic");
			if (string.IsNullOrWhiteSpace(pic))
				throw new AudioBotException("B 站未返回封面");

			if (pic.StartsWith("//", StringComparison.Ordinal))
				pic = "https:" + pic;
			else if (pic.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
				pic = "https://" + pic.Substring("http://".Length);

			await EnsureSessionAsync().ConfigureAwait(false);

			using var request = BuildRequest(pic, null);
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
				throw new AudioBotException(string.Concat("封面下载失败 HTTP ", ((int)response.StatusCode).ToString()));

			using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
			await action(stream).ConfigureAwait(false);
		}

		public void Dispose()
		{
			http.Dispose();
			sessionLock.Dispose();
		}
	}
}
