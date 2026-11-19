// Adaptive Volume Equalizer plugin for TS3AudioBot
//
// A single-band loudness normalizer (AGC) whose *constraint* and *control*
// parameters adapt slowly to the material, while the loudness *target*
// (the normalization invariant) stays fixed.
//
// Design rules (see README.md):
//  - `target` is a specification, not a tunable: it never adapts. If it did,
//    the reference would depend on the input and the system would no longer be
//    a normalizer (and could self-oscillate).
//  - `maxgain` and the time constants are constraint/control parameters: they
//    may adapt, because they only change *how* the target is approached.
//  - All adaptation is slow (seconds) and rate-limited with hysteresis, so it
//    can never turn the normalizer into a compressor.
//  - Per-track statistics are reset on every track start.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Plugins;
using TSLib.Audio;
using TSLib.Helper;

namespace AdaptiveVolumeEqualizer
{
	public sealed class AdaptiveVolumeEqualizerPlugin : IBotPlugin
	{
		private readonly Player player;
		private readonly PlayManager playManager;
		private readonly EqualizerSettings settings;
		private readonly AdaptiveEqualizerPipe pipe;

		private IAudioPassiveConsumer? restoreDownstream;
		private bool initialized;

		public AdaptiveVolumeEqualizerPlugin(Player player, PlayManager playManager)
		{
			this.player = player ?? throw new ArgumentNullException(nameof(player));
			this.playManager = playManager ?? throw new ArgumentNullException(nameof(playManager));
			settings = new EqualizerSettings();
			pipe = new AdaptiveEqualizerPipe(settings);
		}

		public void Initialize()
		{
			if (initialized)
				return;
			initialized = true;

			// Insert between the stall check and the user volume pipe: loudness is
			// normalized first, then the user's volume is applied on top of it.
			// Inserting after the volume pipe would let normalization cancel the
			// user volume, so upstream must be the stall check.
			var upstream = player.StallCheckPipe;
			restoreDownstream = upstream.OutStream;
			pipe.OutStream = restoreDownstream;
			upstream.OutStream = pipe;

			playManager.BeforeResourceStarted += OnBeforeResourceStarted;
		}

		public void Dispose()
		{
			if (!initialized)
				return;
			initialized = false;

			playManager.BeforeResourceStarted -= OnBeforeResourceStarted;

			var upstream = player.StallCheckPipe;
			if (ReferenceEquals(upstream.OutStream, pipe))
				upstream.OutStream = restoreDownstream;
		}

		private Task OnBeforeResourceStarted(object? sender, PlayInfoEventArgs e)
		{
			// Every track starts with clean adaptive statistics.
			pipe.RequestReset();
			return Task.CompletedTask;
		}

		// ---------------------------------------------------------------- commands

		[Command("equalizer")]
		public string CommandStatus()
		{
			var sb = new StringBuilder();
			sb.Append("Equalizer ").Append(settings.Enabled ? "on" : "off");
			sb.Append(", adaptive ").Append(settings.Adaptive ? "on" : "off");
			sb.Append(", target ").Append(Db(settings.TargetDb)).Append(" dBFS");
			sb.Append(", maxgain ").Append(Db(settings.MaxGainDb)).Append(" dB");
			if (settings.Adaptive)
				sb.Append(" (effective ").Append(Db(pipe.EffectiveMaxGainDb)).Append(" dB)");
			sb.Append(", current gain ").Append(Db(pipe.ReportedGainDb, true)).Append(" dB");
			return sb.ToString();
		}

		[Command("equalizer on")]
		public string CommandOn()
		{
			settings.Enabled = true;
			return "Equalizer on.";
		}

		[Command("equalizer off")]
		public string CommandOff()
		{
			settings.Enabled = false;
			return "Equalizer off.";
		}

		[Command("equalizer toggle")]
		public string CommandToggle()
		{
			settings.Enabled = !settings.Enabled;
			return "Equalizer " + (settings.Enabled ? "on." : "off.");
		}

		[Command("equalizer target")]
		public string CommandTarget(string value)
		{
			var db = ParseDb(value, "target");
			if (db < -30f || db > 0f)
				throw new CommandException("Target loudness must be between -30 and 0 dBFS.", CommandExceptionReason.CommandError);
			settings.TargetDb = db;
			return "Target loudness set to " + Db(db) + " dBFS.";
		}

		[Command("equalizer maxgain")]
		public string CommandMaxGain(string value)
		{
			var db = ParseDb(value, "maxgain");
			if (db < 3f || db > 30f)
				throw new CommandException("Max gain must be between 3 and 30 dB.", CommandExceptionReason.CommandError);
			settings.MaxGainDb = db;
			return "Max gain set to " + Db(db) + " dB.";
		}

		[Command("equalizer adaptive")]
		public string CommandAdaptive(string value)
		{
			switch (value.Trim().ToLowerInvariant())
			{
			case "on":
				settings.Adaptive = true;
				return "Adaptive parameters on.";
			case "off":
				settings.Adaptive = false;
				return "Adaptive parameters off.";
			case "toggle":
				settings.Adaptive = !settings.Adaptive;
				return "Adaptive parameters " + (settings.Adaptive ? "on." : "off.");
			default:
				throw new CommandException("Expected 'on' or 'off'.", CommandExceptionReason.CommandError);
			}
		}

		[Command("equalizer reset")]
		public string CommandReset()
		{
			pipe.RequestReset();
			return "Adaptive statistics reset.";
		}

		private static float ParseDb(string value, string name)
		{
			if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
				throw new CommandException("Could not parse " + name + " value.", CommandExceptionReason.CommandError);
			return db;
		}

		private static string Db(float value, bool signed = false)
			=> value.ToString(signed ? "+0.#;-0.#;0" : "0.#", CultureInfo.InvariantCulture);
	}

	/// <summary>Parameters shared between the command thread and the audio thread.</summary>
	internal sealed class EqualizerSettings
	{
		// Specification: never adapted.
		public volatile float TargetDb = -16f;

		// User-facing knobs.
		public volatile bool Enabled = true;
		public volatile bool Adaptive = true;
		public volatile float MaxGainDb = 15f;

		// Safety bounds for the adaptive controller.
		public volatile float MinAdaptiveMaxGainDb = 3f;
		public volatile float MaxCutDb = 20f;
		public volatile float NoiseCeilingDb = -55f;

		// Noise gate on the measured input level.
		public volatile float GateDb = -70f;

		// Control-law time constants (seconds).
		public volatile float RmsAttack = 0.05f;
		public volatile float RmsRelease = 0.6f;
		public volatile float GainAttack = 0.08f;
		public volatile float GainRelease = 2.0f;

		/// <summary>Set to 1 to ask the audio thread to reset its adaptive state.</summary>
		public int ResetRequested;
	}

	/// <summary>
	/// Single-band RMS loudness normalizer with slow parameter adaptation.
	/// Runs on the audio thread; only the reported values are shared outward.
	/// </summary>
	internal sealed class AdaptiveEqualizerPipe : IAudioPipe
	{
		private const int SampleRate = 48000;
		private const int Channels = 2;
		private const double MinDb = -90.0;

		// Long-term adaptation constants (deliberately slow).
		private const double NoiseFloorDownTau = 0.5;   // follow a falling noise floor quickly
		private const double NoiseFloorUpTau = 20.0;    // let it drift up very slowly
		private const double PeakDecayDbPerSec = 6.0;
		private const double AdaptIntervalSec = 1.0;
		private const double AdaptStep = 0.25;          // rate limit toward the new bound
		private const double AdaptHysteresisDb = 0.5;
		private const double MinReleaseSec = 0.3;
		private const double MaxReleaseSec = 2.5;

		private readonly EqualizerSettings settings;

		// Audio-thread-only state.
		private double smoothedLevelDb = MinDb;
		private double currentGainDb;
		private double noiseFloorDb = MinDb;
		private double peakDb = MinDb;
		private double effectiveMaxGainDb = 15.0;
		private double adaptAccum;

		// Shared read-only snapshot for the command thread.
		private volatile float reportedGainDb;
		private volatile float reportedEffectiveMaxGainDb = 15f;

		public AdaptiveEqualizerPipe(EqualizerSettings settings) => this.settings = settings;

		public IAudioPassiveConsumer? OutStream { get; set; }
		public bool Active => OutStream?.Active ?? false;

		public float ReportedGainDb => reportedGainDb;
		public float EffectiveMaxGainDb => reportedEffectiveMaxGainDb;

		public void RequestReset() => Interlocked.Exchange(ref settings.ResetRequested, 1);

		public void Write(Span<byte> data, Meta? meta)
		{
			if (OutStream is null)
				return;

			if (settings.Enabled && data.Length >= Channels * sizeof(short))
			{
				// DSP must never be able to break playback.
				try { Process(data); }
				catch { /* ignore */ }
			}

			OutStream.Write(data, meta);
		}

		private void Process(Span<byte> data)
		{
			var samples = MemoryMarshal.Cast<byte, short>(data);
			int frames = samples.Length / Channels;
			if (frames <= 0)
				return;
			double dt = (double)frames / SampleRate;

			// 1) Block RMS on the unmodified input.
			double sum = 0.0;
			for (int i = 0; i < samples.Length; i++)
			{
				double s = samples[i] * (1.0 / 32768.0);
				sum += s * s;
			}
			double levelDb = 20.0 * Math.Log10(Math.Sqrt(sum / samples.Length) + 1e-9);
			if (levelDb < MinDb) levelDb = MinDb;

			// 2) If a reset was requested, forget the per-track statistics first.
			if (Interlocked.Exchange(ref settings.ResetRequested, 0) != 0)
			{
				noiseFloorDb = levelDb;
				peakDb = levelDb;
				adaptAccum = 0.0;
			}

			// 3) Slow adaptation of the constraint parameters (uses the raw level,
			//    so the applied gain does not feed back into the measurement).
			if (settings.Adaptive)
				UpdateAdaptive(levelDb, dt);
			else
				effectiveMaxGainDb = settings.MaxGainDb;

			// 4) Noise gate: below the gate we neither measure toward the target nor move
			//    the gain, otherwise silence would let the gain drift up to its maximum.
			bool gated = levelDb < settings.GateDb;
			double levelForSmoothing = gated ? settings.GateDb : levelDb;

			// 5) Smooth the level (attack when it rises, release when it falls).
			double levelTau = levelDb > smoothedLevelDb ? settings.RmsAttack : settings.RmsRelease;
			smoothedLevelDb = Smooth(smoothedLevelDb, levelForSmoothing, dt, levelTau);

			// 6) Desired gain: bring the smoothed level to the fixed target, but never
			//    boost more than the (possibly adapted) maximum, and never cut more
			//    than MaxCutDb.
			double desiredGainDb = settings.TargetDb - smoothedLevelDb;
			desiredGainDb = Math.Min(desiredGainDb, effectiveMaxGainDb);
			desiredGainDb = Math.Max(desiredGainDb, -settings.MaxCutDb);

			// 7) Smooth the gain itself (held while gated). Cutting (signal got louder)
			//    reacts faster than boosting (signal got quieter) - the usual AGC convention.
			if (!gated)
			{
				double gainTau = desiredGainDb < currentGainDb ? settings.GainAttack : settings.GainRelease;
				currentGainDb = Smooth(currentGainDb, desiredGainDb, dt, gainTau);
			}

			// 8) Apply the gain in place, clamping to avoid overflow.
			double gain = Math.Pow(10.0, currentGainDb / 20.0);
			for (int i = 0; i < samples.Length; i++)
			{
				int v = (int)(samples[i] * gain);
				if (v > short.MaxValue) v = short.MaxValue;
				else if (v < short.MinValue) v = short.MinValue;
				samples[i] = (short)v;
			}

			reportedGainDb = (float)currentGainDb;
			reportedEffectiveMaxGainDb = (float)effectiveMaxGainDb;
		}

		private void UpdateAdaptive(double levelDb, double dt)
		{
			// Noise floor: fall quickly, rise very slowly.
			double nfTau = levelDb < noiseFloorDb ? NoiseFloorDownTau : NoiseFloorUpTau;
			noiseFloorDb = Smooth(noiseFloorDb, levelDb, dt, nfTau);

			// Peak envelope: instant attack, slow decay.
			peakDb = Math.Max(peakDb - PeakDecayDbPerSec * dt, levelDb);

			// Update the derived bounds at most once per second, rate limited.
			adaptAccum += dt;
			if (adaptAccum < AdaptIntervalSec)
				return;
			adaptAccum = 0.0;

			// Boost limit: keep the amplified noise floor below NoiseCeilingDb.
			double byNoise = settings.NoiseCeilingDb - noiseFloorDb;
			double targetMax = Math.Min(settings.MaxGainDb, byNoise);
			targetMax = Math.Max(settings.MinAdaptiveMaxGainDb, Math.Min(settings.MaxGainDb, targetMax));

			if (Math.Abs(targetMax - effectiveMaxGainDb) > AdaptHysteresisDb)
				effectiveMaxGainDb += (targetMax - effectiveMaxGainDb) * AdaptStep;

			// Release time: wider dynamics get a longer release to avoid pumping.
			double dynamicRange = Math.Max(0.0, peakDb - noiseFloorDb);
			double targetRelease = Clamp(MinReleaseSec + dynamicRange * 0.03, MinReleaseSec, MaxReleaseSec);
			if (Math.Abs(targetRelease - settings.GainRelease) > 0.1)
				settings.GainRelease = (float)(settings.GainRelease + (targetRelease - settings.GainRelease) * AdaptStep);
		}

		private static double Smooth(double current, double target, double dt, double tau)
		{
			if (tau <= 1e-6)
				return target;
			double coef = Math.Exp(-dt / tau);
			return target + (current - target) * coef;
		}

		private static double Clamp(double v, double lo, double hi)
			=> v < lo ? lo : (v > hi ? hi : v);
	}
}
