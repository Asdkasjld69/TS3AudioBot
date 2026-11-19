// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.ResourceFactories;
using TSLib;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio
{
	public class Player : IDisposable
	{
		private const Codec SendCodec = Codec.OpusMusic;
		private readonly DedicatedTaskScheduler scheduler;
		private readonly ConfToolsFfmpeg ffmpegConfig;
		private readonly ConfAudio audioConfig;
		private readonly Id id;
		/// <summary>Protects all access to <see cref="standbyFfmpeg"/>.</summary>
		private readonly object standbyLock = new object();
		/// <summary>The pre-started ffmpeg instance waiting to be activated on the next loop iteration.</summary>
		private FfmpegProducer? standbyFfmpeg;

		public IPlayerSource? CurrentPlayerSource { get; private set; }
		/// <summary>True if a pre-started source for the next loop iteration is ready.</summary>
		public bool IsPreloaded { get { lock (standbyLock) return standbyFfmpeg != null; } }
		/// <summary>The scheduler all playback events run on. Use it to serialize access to this instance.</summary>
		public DedicatedTaskScheduler Scheduler => scheduler;
		public StallCheckPipe StallCheckPipe { get; }
		public VolumePipe VolumePipe { get; }
		public FfmpegProducer FfmpegProducer { get; }
		public PreciseTimedPipe TimePipe { get; }
		public PassiveMergePipe MergePipe { get; }
		public EncoderPipe EncoderPipe { get; }
		public IAudioPassiveConsumer? PlayerSink { get; private set; }

		public Player(ConfRoot confRoot, ConfBot config, DedicatedTaskScheduler scheduler, Id id)
		{
			this.scheduler = scheduler;
			this.id = id;
			ffmpegConfig = confRoot.Tools.Ffmpeg;
			audioConfig = config.Audio;

			FfmpegProducer = new FfmpegProducer(ffmpegConfig, audioConfig, scheduler, id);
			StallCheckPipe = new StallCheckPipe();
			VolumePipe = new VolumePipe();
			Volume = config.Audio.Volume.Default;
			EncoderPipe = new EncoderPipe(SendCodec) { Bitrate = ScaleBitrate(config.Audio.Bitrate) };
			TimePipe = new PreciseTimedPipe(EncoderPipe, id) { ReadBufferSize = EncoderPipe.PacketSize };
			MergePipe = new PassiveMergePipe();

			config.Audio.Bitrate.Changed += (s, e) => EncoderPipe.Bitrate = ScaleBitrate(e.NewValue);

			MergePipe.Into(TimePipe).Chain<CheckActivePipe>().Chain(StallCheckPipe).Chain(VolumePipe).Chain(EncoderPipe);
		}

		public void SetTarget(IAudioPassiveConsumer target)
		{
			PlayerSink = target;
			EncoderPipe.Chain(target);
		}

		private static int ScaleBitrate(int value) => Tools.Clamp(value, 1, 255) * 1000;

		public event AsyncEventHandler? OnSongEnd;
		public event AsyncEventHandler<SongInfoChanged>? OnSongUpdated;

		private void TriggerSongEnd(object? o, EventArgs e) => scheduler.InvokeAsync(() => OnSongEnd.InvokeAsync(this, EventArgs.Empty));
		private void TriggerSongUpdated(object? o, SongInfoChanged e) => scheduler.InvokeAsync(() => OnSongUpdated.InvokeAsync(this, e));

		public async Task Play(PlayResource res)
		{
			DiscardStandby();
			if (res is MediaPlayResource mres && mres.IsIcyStream)
				await FfmpegProducer.AudioStartIcy(res.PlayUri);
			else
				await FfmpegProducer.AudioStart(res.PlayUri, res.PlayInfo?.StartOffset);
			Play(FfmpegProducer);
		}

		public void Play(IPlayerSource source) => AttachSource(source);

		private void AttachSource(IPlayerSource source)
		{
			var oldSource = CurrentPlayerSource;
			if (oldSource != source)
			{
				// Clean up old
				CleanSource(oldSource);
				// Set events
				source.OnSongEnd += TriggerSongEnd;
				source.OnSongUpdated += TriggerSongUpdated;
				// Update pipes
				MergePipe.Add(source);
				CurrentPlayerSource = source;
			}
			// Start Ticker
			TimePipe.AudioTimer.Reset();
			TimePipe.Paused = false;
		}

		/// <summary>
		/// Pre-starts the given resource in a standby ffmpeg instance without hooking it into the
		/// playback pipeline yet. Its decoded output accumulates in the OS pipe buffer, so once
		/// activated it can deliver audio immediately (gapless loop). Call on the scheduler thread.
		/// </summary>
		public void Preload(PlayResource res)
		{
			lock (standbyLock)
			{
				DiscardStandby();
				if (res is MediaPlayResource mres && mres.IsIcyStream)
					return; // Live streams have no known end and can't be preloaded

				var standby = new FfmpegProducer(ffmpegConfig, audioConfig, scheduler, id);
				_ = standby.AudioStart(res.PlayUri, res.PlayInfo?.StartOffset);
				standbyFfmpeg = standby;
			}
		}

		/// <summary>
		/// Activates the preloaded standby source if one is ready. The old (ended) source is
		/// removed and disposed and the standby becomes the new current source.
		/// Returns false if no preloaded source is available.
		/// </summary>
		public bool TryActivatePreloaded()
		{
			FfmpegProducer? standby;
			lock (standbyLock)
			{
				standby = standbyFfmpeg;
				standbyFfmpeg = null;
			}
			if (standby is null)
				return false;
			AttachSource(standby);
			return true;
		}

		private void DiscardStandby()
		{
			lock (standbyLock)
			{
				var standby = standbyFfmpeg;
				standbyFfmpeg = null;
				standby?.Dispose();
			}
		}

		private void CleanSource(IPlayerSource? source)
		{
			if (source is null)
				return;
			source.OnSongEnd -= TriggerSongEnd;
			source.OnSongUpdated -= TriggerSongUpdated;
			MergePipe.Remove(source);
			source.Dispose();
		}

		public void Stop()
		{
			DiscardStandby();
			CurrentPlayerSource?.Dispose();
			if (MergePipe.Count <= 1)
				TimePipe.Paused = true;
		}

		public void StopAll()
		{
			Stop();
			TimePipe.Paused = true;
			MergePipe.Dispose();
		}

		public TimeSpan? Length => CurrentPlayerSource?.Length;

		public TimeSpan? Position => CurrentPlayerSource?.Position;

		public Task Seek(TimeSpan position) => CurrentPlayerSource?.Seek(position) ?? Task.CompletedTask;

		public float Volume
		{
			get => AudioValues.FactorToHumanVolume(VolumePipe.Volume);
			set => VolumePipe.Volume = AudioValues.HumanVolumeToFactor(value);
		}

		public bool Paused
		{
			get => TimePipe.Paused;
			set => TimePipe.Paused = value;
		}

		// Extras

		public void SetStall() => StallCheckPipe.SetStall();

		[Obsolete(AttributeStrings.UnderDevelopment)]
		public void MixInStreamOnce(IPlayerSource producer)
		{
			producer.OnSongEnd += (s, e) =>
			{
				MergePipe.Remove(producer);
				producer.Dispose();
			};
			MergePipe.Add(producer);
			TimePipe.Paused = false;
		}

		public void Dispose()
		{
			StopAll();
			CleanSource(CurrentPlayerSource);
			TimePipe.Dispose();
			FfmpegProducer.Dispose();
			EncoderPipe.Dispose();
		}
	}
}
