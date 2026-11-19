// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio
{
	public class FfmpegProducer : IPlayerSource, ISampleInfo, IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly Id id;
		private static readonly Regex FindDurationMatch = new Regex(@"^\s*Duration: (\d+):(\d\d):(\d\d).(\d\d)", Util.DefaultRegexConfig);
		private static readonly Regex IcyMetadataMacher = new Regex("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);
		private const string PreLinkConf = "-hide_banner -nostats -threads 1";
		private const string InputLinkConf = "-i \"";
		private const string PostLinkConf = "\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private const string LinkConfIcy = "-hide_banner -nostats -threads 1 -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private static readonly TimeSpan retryOnDropBeforeEnd = TimeSpan.FromSeconds(10);

		private readonly ConfToolsFfmpeg config;
		private readonly ConfAudio audioConfig;

		public event EventHandler? OnSongEnd;
		public event EventHandler<SongInfoChanged>? OnSongUpdated;

		private readonly DedicatedTaskScheduler scheduler;
		private FfmpegInstance? ffmpegInstance;

		public int SampleRate { get; } = 48000;
		public int Channels { get; } = 2;
		public int BitsPerSample { get; } = 16;

		public FfmpegProducer(ConfToolsFfmpeg config, ConfAudio audioConfig, DedicatedTaskScheduler scheduler, Id id)
		{
			this.config = config;
			this.audioConfig = audioConfig;
			this.scheduler = scheduler;
			this.id = id;
		}

		public Task AudioStart(string url, TimeSpan? startOff = null)
		{
			StartFfmpegProcess(url, startOff ?? TimeSpan.Zero);
			return Task.CompletedTask;
		}

		public async Task AudioStartIcy(string url) => await StartFfmpegProcessIcy(url);

		public void AudioStop()
		{
			StopFfmpegProcess();
		}

		public TimeSpan? Length => GetCurrentSongLength();

		public TimeSpan? Position => ffmpegInstance?.AudioTimer.SongPosition;

		public Task Seek(TimeSpan position) { SetPosition(position); return Task.CompletedTask; }

		public int Read(byte[] buffer, int offset, int length, out Meta? meta)
		{
			meta = default;
			int read;

			var instance = ffmpegInstance;

			if (instance is null)
				return 0;

			try
			{
				read = instance.FfmpegProcess.StandardOutput.BaseStream.Read(buffer, 0, length);
			}
			catch (Exception ex)
			{
				read = 0;
				Log.Debug(ex, "Can't read ffmpeg");
			}

			if (read == 0)
			{
				AssertNotMainScheduler();

				var (ret, triggerEndSafe) = instance.IsIcyStream
					? OnReadEmptyIcy(instance)
					: OnReadEmpty(instance);
				if (ret)
					return 0;

				if (instance.FfmpegProcess.HasExitedSafe())
				{
					Log.Trace("Ffmpeg has exited");
					AudioStop();
					triggerEndSafe = true;
				}

				if (triggerEndSafe)
				{
					OnSongEnd?.Invoke(this, EventArgs.Empty);
					return 0;
				}
			}

			instance.HasTriedToReconnect = false;
			instance.AudioTimer.PushBytes(read);
			return read;
		}

		private (bool ret, bool trigger) OnReadEmpty(FfmpegInstance instance)
		{
			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				var expectedStopLength = GetCurrentSongLength();
				Log.Trace("Expected song length {0}", expectedStopLength);
				if (expectedStopLength != TimeSpan.Zero)
				{
					var actualStopPosition = instance.AudioTimer.SongPosition;
					Log.Trace("Actual song position {0}", actualStopPosition);
					if (actualStopPosition + retryOnDropBeforeEnd < expectedStopLength)
					{
						Log.Debug("Connection to song lost, retrying at {0}", actualStopPosition);
						instance.HasTriedToReconnect = true;
						var newInstance = SetPosition(actualStopPosition);
						if (newInstance.Ok)
						{
							newInstance.Value.HasTriedToReconnect = true;
							return (true, false);
						}
						else
						{
							Log.Debug("Retry failed {0}", newInstance.Error);
							return (false, true);
						}
					}
				}
			}
			return (false, false);
		}

		private (bool ret, bool trigger) OnReadEmptyIcy(FfmpegInstance instance)
		{
			AssertNotMainScheduler();

			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				Log.Debug("Connection to stream lost, retrying...");
				instance.HasTriedToReconnect = true;
				var newInstance = StartFfmpegProcessIcy(instance.ReconnectUrl).Result;
				if (newInstance.Ok)
				{
					newInstance.Value.HasTriedToReconnect = true;
					return (true, false);
				}
				else
				{
					Log.Debug("Retry failed {0}", newInstance.Error);
					return (false, true);
				}
			}
			return (false, false);
		}

		private R<FfmpegInstance, string> SetPosition(TimeSpan value)
		{
			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value));
			var instance = ffmpegInstance;
			if (instance is null)
				return "No instance running";
			if (instance.IsIcyStream)
				return "Cannot seek icy stream";
			var lastLink = instance.ReconnectUrl;
			if (lastLink is null)
				return "No current url active";
			return StartFfmpegProcess(lastLink, value);
		}

		/// <summary>Builds the ffmpeg argument prefix up to and including the input flag, injecting the
		/// user configured extra input arguments (e.g. reconnect and buffer options for network streams).</summary>
		private string BuildLinkConf()
		{
			var extra = config.ExtraInputArgs.Value;
			if (string.IsNullOrWhiteSpace(extra))
				return string.Concat(PreLinkConf, " ", InputLinkConf);
			return string.Concat(PreLinkConf, " ", extra.Trim(), " ", InputLinkConf);
		}

		private R<FfmpegInstance, string> StartFfmpegProcess(string url, TimeSpan? offsetOpt)
		{
			StopFfmpegProcess();
			Log.Trace("Start request {0}", url);

			string arguments;
			var offset = offsetOpt ?? TimeSpan.Zero;
			if (offset > TimeSpan.Zero)
			{
				var seek = string.Format(CultureInfo.InvariantCulture, @"-ss {0:hh\:mm\:ss\.fff}", offset);
				arguments = string.Concat(seek, " ", BuildLinkConf(), url, PostLinkConf, " ", seek);
			}
			else
			{
				arguments = string.Concat(BuildLinkConf(), url, PostLinkConf);
			}

			var newInstance = new FfmpegInstance(
				url,
				new PreciseAudioTimer(this)
				{
					SongPositionOffset = offset,
				});

			return StartFfmpegProcessInternal(newInstance, arguments);
		}

		private async Task<R<FfmpegInstance, string>> StartFfmpegProcessIcy(string url)
		{
			StopFfmpegProcess();
			Log.Trace("Start icy-stream request {0}", url);

			try
			{
				var response = await WebWrapper
					.Request(url)
					.WithHeader("Icy-MetaData", "1")
					.UnsafeResponse();

				if (!int.TryParse(response.Headers.GetSingle("icy-metaint"), out var metaint))
				{
					response.Dispose();
					return "Invalid icy stream tags";
				}

				var stream = await response.Content.ReadAsStreamAsync();
				int.TryParse(response.Headers.GetSingle("icy-br"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var icyBitrate);
				var newInstance = new FfmpegInstance(
					url,
					new PreciseAudioTimer(this),
					stream,
					metaint,
					icyBitrate)
				{
					OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e),
					IcyBufferChunkCapacity = GetIcyBufferChunkCapacity(icyBitrate),
				};

				new Thread(() => newInstance.ReadStreamLoop(id))
				{
					Name = $"IcyStreamReader[{id}]",
				}.Start();

				return StartFfmpegProcessInternal(newInstance, LinkConfIcy);
			}
			catch (Exception ex)
			{
				var error = $"Unable to create icy-stream ({ex.Message})";
				Log.Warn(ex, error);
				return error;
			}
		}

		private R<FfmpegInstance, string> StartFfmpegProcessInternal(FfmpegInstance instance, string arguments)
		{
			try
			{
				instance.FfmpegProcess.StartInfo = new ProcessStartInfo
				{
					FileName = config.Path.Value,
					Arguments = arguments,
					RedirectStandardOutput = true,
					RedirectStandardInput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				instance.FfmpegProcess.EnableRaisingEvents = true;

				Log.Debug("Starting ffmpeg with {0}", arguments);
				instance.FfmpegProcess.ErrorDataReceived += instance.FfmpegProcess_ErrorDataReceived;
				instance.FfmpegProcess.Start();
				instance.FfmpegProcess.BeginErrorReadLine();

				instance.AudioTimer.Start();

				var oldInstance = Interlocked.Exchange(ref ffmpegInstance, instance);
				oldInstance?.Close();

				return instance;
			}
			catch (Exception ex)
			{
				var error = ex is Win32Exception
					? $"Ffmpeg could not be found ({ex.Message})"
					: $"Unable to create stream ({ex.Message})";
				Log.Error(ex, error);
				instance.Close();
				StopFfmpegProcess();
				return error;
			}
		}

		private void StopFfmpegProcess()
		{
			var oldInstance = Interlocked.Exchange(ref ffmpegInstance, null);
			if (oldInstance != null)
			{
				oldInstance.OnMetaUpdated = null;
				oldInstance.Close();
			}
		}

		private TimeSpan? GetCurrentSongLength() => ffmpegInstance?.ParsedSongLength;

		private void AssertNotMainScheduler()
		{
			if (TaskScheduler.Current == scheduler)
				throw new Exception("Cannot read on own scheduler. Throwing to prevent deadlock");
		}

		private const int IcyChunkSize = 4096;
		private const int DefaultIcyBitrateKbps = 128;

		/// <summary>Queue capacity in chunks for the icy read-ahead buffer, derived from the configured seconds.</summary>
		private int GetIcyBufferChunkCapacity(int icyBitrateKbps)
		{
			var seconds = audioConfig.IcyBufferSeconds.Value;
			if (seconds <= 0)
				return 1;
			var bitrateKbps = icyBitrateKbps > 0 ? icyBitrateKbps : DefaultIcyBitrateKbps;
			var targetBytes = seconds * bitrateKbps * 1000.0 / 8.0;
			return Math.Max(1, (int)(targetBytes / IcyChunkSize));
		}

		public void Dispose()
		{
			StopFfmpegProcess();
		}

		private class FfmpegInstance
		{
			public Process FfmpegProcess { get; }
			public bool HasTriedToReconnect { get; set; }
			public string ReconnectUrl { get; }
			public bool IsIcyStream => IcyStream != null;

			public PreciseAudioTimer AudioTimer { get; }
			public TimeSpan? ParsedSongLength { get; set; } = null;

			public Stream? IcyStream { get; }
			public int IcyMetaInt { get; }
			/// <summary>Stream bitrate from the icy-br header, or 0 when unknown.</summary>
			public int IcyBitrateKbps { get; }
			/// <summary>Read-ahead queue capacity in chunks; 1 means no extra buffering.</summary>
			public int IcyBufferChunkCapacity { get; set; } = 1;
			public bool Closed { get; set; }

			public Action<SongInfoChanged>? OnMetaUpdated;

			public FfmpegInstance(string url, PreciseAudioTimer timer) : this(url, timer, null!, 0, 0) { }
			public FfmpegInstance(string url, PreciseAudioTimer timer, Stream icyStream, int icyMetaInt, int icyBitrateKbps)
			{
				FfmpegProcess = new Process();
				ReconnectUrl = url;
				AudioTimer = timer;
				IcyStream = icyStream;
				IcyMetaInt = icyMetaInt;
				IcyBitrateKbps = icyBitrateKbps;

				HasTriedToReconnect = false;
			}

			public void Close()
			{
				Closed = true;

				try
				{
					if (!FfmpegProcess.HasExitedSafe())
						FfmpegProcess.Kill();
				}
				catch { }
				try { FfmpegProcess.CancelErrorRead(); } catch { }
				try { FfmpegProcess.StandardInput.Dispose(); } catch { }
				try { FfmpegProcess.StandardOutput.Dispose(); } catch { }
				try { FfmpegProcess.Dispose(); } catch { }

				IcyStream?.Dispose();
			}

			public void FfmpegProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
			{
				if (e.Data is null)
					return;

				if (sender != FfmpegProcess)
					throw new InvalidOperationException("Wrong process associated to event");

				if (ParsedSongLength is null)
				{
					var match = FindDurationMatch.Match(e.Data);
					if (!match.Success)
						return;

					int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
					int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
					int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
					int millisec = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10;
					ParsedSongLength = new TimeSpan(0, hours, minutes, seconds, millisec);
				}

				//if (!HasIcyTag && e.Data.AsSpan().TrimStart().StartsWith("icy-".AsSpan()))
				//{
				//	HasIcyTag = true;
				//}
			}

			/// <summary>Consecutive read/write errors tolerated before the stream is closed.</summary>
			private const int MaxConsecutiveErrors = 50;
			private static readonly TimeSpan ErrorCountResetWindow = TimeSpan.FromSeconds(30);

			public void ReadStreamLoop(Id id)
			{
				if (IcyStream is null)
					throw new InvalidOperationException("Instance is not an icy stream");

				Tools.SetLogId(id.ToString());

				// The network reader runs ahead of ffmpeg and queues its data, so short
				// network hiccups are served from the queue instead of reaching playback.
				var queue = new BlockingCollection<byte[]>(IcyBufferChunkCapacity);
				var netReader = new Thread(() =>
				{
					try { ReadIcyNetworkLoop(queue); }
					catch (Exception ex) { Log.Debug(ex, "Icy stream reader stopped"); }
					finally { queue.CompleteAdding(); }
				})
				{
					Name = $"IcyStreamNetReader[{id}]",
					IsBackground = true,
				};
				netReader.Start();

				var lastErrorTime = DateTime.MinValue;
				int errorCount = 0;

				try
				{
					foreach (var chunk in queue.GetConsumingEnumerable())
					{
						if (Closed)
							break;

						try
						{
							FfmpegProcess.StandardInput.BaseStream.Write(chunk, 0, chunk.Length);
							errorCount = 0;
						}
						catch (Exception ex)
						{
							if (Closed)
								break;

							var now = DateTime.UtcNow;
							if (now - lastErrorTime > ErrorCountResetWindow)
								errorCount = 0;
							lastErrorTime = now;

							if (++errorCount >= MaxConsecutiveErrors)
							{
								Log.Error(ex, "Failed too many times trying to access ffmpeg. Closing stream.");
								break;
							}

							if (ex is InvalidOperationException)
							{
								Log.Debug(ex, "Waiting for ffmpeg");
								Thread.Sleep(100);
							}
							else
							{
								Log.Debug(ex, "Stream read/write error");
							}
						}
					}
				}
				finally
				{
					if (!Closed)
						Close();
				}
			}

			/// <summary>Reads the icy stream, splits off the metadata blocks and enqueues the audio bytes.</summary>
			private void ReadIcyNetworkLoop(BlockingCollection<byte[]> queue)
			{
				const int IcyMaxMeta = 255 * 16;
				var buffer = new byte[Math.Max(IcyChunkSize, IcyMaxMeta)];
				var stream = IcyStream!;
				int readCount = 0;
				int readErrorCount = 0;
				var lastReadErrorTime = DateTime.MinValue;

				while (!Closed)
				{
					try
					{
						while (readCount < IcyMetaInt)
						{
							int read = stream.Read(buffer, 0, Math.Min(IcyChunkSize, IcyMetaInt - readCount));
							if (read == 0)
								return;
							readCount += read;
							if (!EnqueueChunk(queue, buffer, read))
								return;
						}
						readCount = 0;

						int metaByte = stream.ReadByte();
						if (metaByte < 0)
							return;

						if (metaByte > 0)
						{
							metaByte *= 16;
							while (readCount < metaByte)
							{
								int read = stream.Read(buffer, 0, metaByte - readCount);
								if (read == 0)
									return;
								readCount += read;
							}
							readCount = 0;

							var metaString = Tools.Utf8Encoder.GetString(buffer, 0, metaByte).TrimEnd('\0');
							Log.Debug("Meta: {0}", metaString);
							OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
						}
					}
					catch (Exception ex)
					{
						var now = DateTime.UtcNow;
						if (now - lastReadErrorTime > ErrorCountResetWindow)
							readErrorCount = 0;
						lastReadErrorTime = now;

						if (++readErrorCount >= MaxConsecutiveErrors)
						{
							Log.Error(ex, "Failed too many times reading the icy stream. Closing stream.");
							Close();
							return;
						}

						Log.Debug(ex, "Icy stream read error");
						Thread.Sleep(100);
					}
				}
			}

			/// <summary>Copies and enqueues a chunk, aborting when the stream is closing.</summary>
			private bool EnqueueChunk(BlockingCollection<byte[]> queue, byte[] buffer, int count)
			{
				var chunk = new byte[count];
				Buffer.BlockCopy(buffer, 0, chunk, 0, count);
				while (!Closed)
				{
					if (queue.TryAdd(chunk, 200))
						return true;
				}
				return false;
			}

			private static SongInfoChanged ParseIcyMeta(string metaString)
			{
				var songInfo = new SongInfoChanged();
				var match = IcyMetadataMacher.Match(metaString);
				if (match.Success)
				{
					for (int i = 0; i < match.Groups[1].Captures.Count; i++)
					{
						switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
						{
						case "STREAMTITLE":
							songInfo.Title = match.Groups[3].Captures[i].Value;
							break;
						}
					}
				}
				return songInfo;
			}
		}
	}
}
