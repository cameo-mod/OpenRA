#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAL;

namespace OpenRA.Platforms.Default
{
	sealed class OpenAlSoundEngine : ISoundEngine
	{
		public bool Dummy => false;

		public SoundDevice[] AvailableDevices()
		{
			var defaultDevices = new[]
			{
				new SoundDevice(null, "Default Output"),
			};

			// OpenAL Soft prefixes every specifier with "OpenAL Soft on "; strip it from the
			// display label so the dropdown matches the OS device names. The Device key keeps
			// the raw specifier — that is what gets passed back to OpenAL to open/reopen it.
			const string Prefix = "OpenAL Soft on ";
			var physicalDevices = PhysicalDevices()
				.Select(d => new SoundDevice(d, d.StartsWith(Prefix, StringComparison.Ordinal) ? d[Prefix.Length..] : d));
			return defaultDevices.Concat(physicalDevices).ToArray();
		}

		sealed class PoolSlot
		{
			public bool IsActive;
			public int FrameStarted;
			public WPos Pos;
			public bool IsRelative;
			public OpenAlSoundSource SoundSource;
			public OpenAlSound Sound;
		}

		const int MaxInstancesPerFrame = 3;
		const int GroupDistance = 2730;
		const int GroupDistanceSqr = GroupDistance * GroupDistance;

		// Listener height above the battlefield plane is derived from the width of the visible viewport (in
		// world units), not a fixed constant: a fixed height made panning severity depend on zoom/resolution, and
		// a screen-filling map easily spans tens of thousands of world units while the old fixed height was
		// ~2000, so sources panned hard left/right just a few tiles off-centre instead of tracking their
		// on-screen position. height = viewportHalfWidth / PanSpread; a bigger divisor gives a shorter listener
		// (sharper panning), a smaller divisor a taller one (gentler panning).
		const float PanSpread = 1f;

		// Floor so a not-yet-sized viewport (or extreme zoom) can't collapse the listener height towards 0 and
		// push the pan angle towards 90 degrees for every source.
		const int MinListenerHeight = 256;

		// Volume falloff-by-distance is computed manually (see ComputeDistanceGain) instead of through OpenAL's
		// built-in distance attenuation, and deliberately does NOT use the synthetic pan height above: that height
		// is sized to the screen (tens of thousands of units at typical zoom) purely to make panning track
		// on-screen position, but AL_REFERENCE_DISTANCE/AL_MAX_DISTANCE need to stay anchored to genuine world
		// distance regardless of zoom - a battle 100 tiles away should sound distant at any zoom level. Sharing
		// one 3D vector for both would force a choice between hard-panning near screen centre or making distant,
		// off-screen fights audible at near-full volume; every source instead gets AL_ROLLOFF_FACTOR 0 (below) so
		// OpenAL's own distance attenuation is a no-op, and this reproduces the original fixed-height tuning.
		const float DistanceGainReferenceDistance = 6826f;
		const float DistanceGainMaxDistance = 136533f;
		const float DistanceGainRolloff = 1f;

		// Listener position from the most recent SetListenerPosition call (without the synthetic pan height),
		// used by ComputeDistanceGain. Sounds played before the first frame (e.g. UI/menu audio, which is
		// listener-relative and so never distance-attenuated anyway) just get the zero default.
		WPos currentListenerPos;

		float ComputeDistanceGain(WPos pos)
		{
			var offset = pos - currentListenerPos;
			var distance = Math.Sqrt((double)offset.X * offset.X + (double)offset.Y * offset.Y + (double)offset.Z * offset.Z);
			var clamped = Math.Clamp(distance, DistanceGainReferenceDistance, DistanceGainMaxDistance);
			return (float)(DistanceGainReferenceDistance / (DistanceGainReferenceDistance + DistanceGainRolloff * (clamped - DistanceGainReferenceDistance)));
		}

		// https://github.com/kcat/openal-soft/issues/580
		// https://github.com/kcat/openal-soft/blob/b6aa73b26004afe63d83097f2f91ecda9bc25cb9/alc/alc.cpp#L3191-L3203
		const int PoolSize = 256;

		readonly Dictionary<uint, PoolSlot> sourcePool = new(PoolSize);
		readonly Dictionary<SoundCategory, float> categoryVolumes = new()
		{
			{ SoundCategory.None, 1f },
			{ SoundCategory.SoundEffect, 1f },
			{ SoundCategory.EVA, 1f },
			{ SoundCategory.UnitVoice, 1f },
		};

		float volume = 1f;
		IntPtr device;
		IntPtr context;

		// ALC_SOFT_reopen_device lets us swap the live output device without
		// tearing down sources/buffers. Resolved at construction time.
		bool canReopenDevice;

		// alcReopenDeviceSOFT is an OpenAL extension entry point. Extension functions
		// aren't guaranteed to be exported as named DLL symbols, so we must resolve
		// the pointer at runtime via alcGetProcAddress and call it through a delegate
		// (a plain [DllImport] throws EntryPointNotFoundException).
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		delegate bool AlcReopenDeviceSOFTDelegate(IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceName, int[] attribs);

		AlcReopenDeviceSOFTDelegate alcReopenDeviceSOFT;

		static string[] QueryDevices(string label, int type)
		{
			// Clear the ALC error state. Device enumeration is an ALC call and must be
			// validated against the ALC error domain (alcGetError on the NULL device),
			// NOT the AL domain (alGetError): alGetError requires a current context and
			// otherwise returns AL_INVALID_OPERATION, which would falsely discard a
			// perfectly valid device list — leaving only "Default Output" in the dropdown.
			ALC10.alcGetError(IntPtr.Zero);

			// Returns a null separated list of strings, terminated by two nulls.
			var devicesPtr = ALC10.alcGetString(IntPtr.Zero, type);
			if (devicesPtr == IntPtr.Zero || ALC10.alcGetError(IntPtr.Zero) != ALC10.ALC_NO_ERROR)
			{
				Log.Write("sound", $"Failed to query OpenAL device list using {label}");
				return [];
			}

			var devices = new List<string>();
			var buffer = new List<byte>();
			var offset = 0;

			// The list is null-separated and terminated by two successive nulls. A null
			// terminates the current string; an empty string (two nulls in a row) ends the
			// list. NOTE: the previous do/while peeked the next byte in the loop condition
			// and exited on the FIRST string's terminator before flushing the buffer, so it
			// returned an empty list for any non-empty device list.
			while (true)
			{
				var b = Marshal.ReadByte(devicesPtr, offset++);
				if (b != 0)
				{
					buffer.Add(b);
					continue;
				}

				if (buffer.Count == 0)
					break;

				devices.Add(Encoding.UTF8.GetString(buffer.ToArray()));
				buffer.Clear();
			}

			return devices.ToArray();
		}

		static string[] PhysicalDevices()
		{
			// Returns all devices under Windows Vista and newer
			if (ALC11.alcIsExtensionPresent(IntPtr.Zero, "ALC_ENUMERATE_ALL_EXT"))
				return QueryDevices("ALC_ENUMERATE_ALL_EXT", ALC11.ALC_ALL_DEVICES_SPECIFIER);

			if (ALC11.alcIsExtensionPresent(IntPtr.Zero, "ALC_ENUMERATION_EXT"))
				return QueryDevices("ALC_ENUMERATION_EXT", ALC10.ALC_DEVICE_SPECIFIER);

			return [];
		}

		internal static int MakeALFormat(int channels, int bits)
		{
			if (channels == 1)
				return bits == 16 ? AL10.AL_FORMAT_MONO16 : AL10.AL_FORMAT_MONO8;
			else
				return bits == 16 ? AL10.AL_FORMAT_STEREO16 : AL10.AL_FORMAT_STEREO8;
		}

		public OpenAlSoundEngine(string deviceName)
		{
			if (deviceName != null)
				Console.WriteLine("Using sound device `{0}`", deviceName);
			else
				Console.WriteLine("Using default sound device");

			device = ALC10.alcOpenDevice(deviceName);
			if (device == IntPtr.Zero)
			{
				Console.WriteLine("Failed to open device. Falling back to default");
				device = ALC10.alcOpenDevice(null);
				if (device == IntPtr.Zero)
					throw new InvalidOperationException("Can't create OpenAL device");
			}

			context = ALC10.alcCreateContext(device, null);
			if (context == IntPtr.Zero)
				throw new InvalidOperationException("Can't create OpenAL context");
			ALC10.alcMakeContextCurrent(context);

			// Resolve the reopen extension entry point through alcGetProcAddress.
			if (ALC11.alcIsExtensionPresent(device, "ALC_SOFT_reopen_device"))
			{
				var procPtr = ALC10.alcGetProcAddress(device, "alcReopenDeviceSOFT");
				if (procPtr != IntPtr.Zero)
				{
					alcReopenDeviceSOFT = Marshal.GetDelegateForFunctionPointer<AlcReopenDeviceSOFTDelegate>(procPtr);
					canReopenDevice = true;
				}
			}

			if (!canReopenDevice)
				Log.Write("sound", "ALC_SOFT_reopen_device unavailable; sound device changes require a restart");

			for (var i = 0; i < PoolSize; i++)
			{
				AL10.alGenSources(1, out var source);
				if (AL10.alGetError() != AL10.AL_NO_ERROR)
				{
					Log.Write("sound", $"Failed generating OpenAL source {i}");
					return;
				}

				sourcePool.Add(source, new PoolSlot() { IsActive = false });
			}
		}

		// Attempts to swap the live output device in place, keeping all sources and
		// buffers intact. deviceName == null means the system default output.
		// Returns false (and changes nothing) if the reopen extension is missing
		// or the call fails, so callers can fall back to requiring a restart.
		public bool TrySetDevice(string deviceName)
		{
			if (!canReopenDevice)
				return false;

			AL10.alGetError();
			if (!alcReopenDeviceSOFT(device, deviceName, null))
			{
				Log.Write("sound", $"alcReopenDeviceSOFT failed for device `{deviceName ?? "Default Output"}`");
				return false;
			}

			return true;
		}

		bool TryGetSourceFromPool(out uint source)
		{
			foreach (var kv in sourcePool)
			{
				if (!kv.Value.IsActive)
				{
					sourcePool[kv.Key].IsActive = true;
					source = kv.Key;
					return true;
				}
			}

			var freeSources = new List<uint>();
			foreach (var kv in sourcePool)
			{
				var sound = kv.Value.Sound;
				if (sound != null && sound.Complete)
				{
					var freeSource = kv.Key;
					freeSources.Add(freeSource);
					AL10.alSourceRewind(freeSource);
					AL10.alSourcei(freeSource, AL10.AL_BUFFER, 0);

					// Make sure we can accurately determine the end of the original sound,
					// even if the source is immediately reused.
					sound.UnbindSource();

					var slot = kv.Value;
					slot.SoundSource = null;
					slot.Sound = null;
					slot.IsActive = false;
				}
			}

			if (freeSources.Count == 0)
			{
				source = 0;
				return false;
			}

			source = freeSources[0];
			sourcePool[source].IsActive = true;
			return true;
		}

		public ISoundSource AddSoundSourceFromMemory(byte[] data, int channels, int sampleBits, int sampleRate)
		{
			return new OpenAlSoundSource(data, data.Length, channels, sampleBits, sampleRate);
		}

		public ISound Play2D(ISoundSource soundSource, bool loop, bool relative, WPos pos, float volume, bool attenuateVolume)
		{
			return Play2D(soundSource, loop, relative, pos, volume, attenuateVolume, SoundCategory.SoundEffect);
		}

		public ISound Play2D(ISoundSource soundSource, bool loop, bool relative, WPos pos, float volume, bool attenuateVolume, SoundCategory category)
		{
			if (soundSource == null)
			{
				Log.Write("sound", "Attempt to Play2D a null `ISoundSource`");
				return null;
			}

			var alSoundSource = (OpenAlSoundSource)soundSource;

			var currFrame = Game.LocalTick;
			var atten = 1f;

			// Check if max # of instances-per-location reached:
			if (attenuateVolume)
			{
				int instances = 0, activeCount = 0;
				foreach (var s in sourcePool.Values)
				{
					if (!s.IsActive)
						continue;
					if (s.IsRelative != relative)
						continue;

					++activeCount;
					if (s.SoundSource != alSoundSource)
						continue;
					if (currFrame - s.FrameStarted >= 5)
						continue;

					// Too far away to count?
					var lensqr = (s.Pos - pos).LengthSquared;
					if (lensqr >= GroupDistanceSqr)
						continue;

					// If we are starting too many instances of the same sound within a short time then stop this one:
					if (++instances == MaxInstancesPerFrame)
						return null;
				}

				// Attenuate a little bit based on number of active sounds:
				atten = 0.66f * ((PoolSize - activeCount * 0.5f) / PoolSize);
			}

			if (!TryGetSourceFromPool(out var source))
				return null;

			var slot = sourcePool[source];
			slot.Pos = pos;
			slot.FrameStarted = currFrame;
			slot.IsRelative = relative;
			slot.SoundSource = alSoundSource;
			var distanceGain = relative ? 1f : ComputeDistanceGain(pos);
			slot.Sound = new OpenAlSound(source, loop, relative, pos, volume * atten, alSoundSource.SampleRate,
				alSoundSource.Buffer, distanceGain, category, categoryVolumes[category]);
			return slot.Sound;
		}

		public ISound Play2DStream(Stream stream, int channels, int sampleBits, int sampleRate, bool loop, bool relative, WPos pos, float volume)
		{
			var currFrame = Game.LocalTick;

			if (!TryGetSourceFromPool(out var source))
				return null;

			var slot = sourcePool[source];
			slot.Pos = pos;
			slot.FrameStarted = currFrame;
			slot.IsRelative = relative;
			slot.SoundSource = null;
			var distanceGain = relative ? 1f : ComputeDistanceGain(pos);
			slot.Sound = new OpenAlAsyncLoadSound(source, loop, relative, pos, volume, channels, sampleBits, sampleRate, stream, distanceGain);
			return slot.Sound;
		}

		public float Volume
		{
			get => volume;
			set => AL10.alListenerf(AL10.AL_GAIN, volume = value);
		}

		public void PauseSound(ISound sound, bool paused)
		{
			if (sound == null || sound.Complete)
				return;

			var source = ((OpenAlSound)sound).Source;
			PauseSound(source, paused);
		}

		public void SetAllSoundsPaused(bool paused)
		{
			foreach (var source in sourcePool.Keys)
				PauseSound(source, paused);
		}

		static void PauseSound(uint source, bool paused)
		{
			AL10.alGetSourcei(source, AL10.AL_SOURCE_STATE, out var state);
			if (paused)
			{
				if (state == AL10.AL_PLAYING)
					AL10.alSourcePause(source);
				else if (state == AL10.AL_INITIAL)
				{
					// If a sound hasn't started yet,
					// we indicate it should not play be transitioning it to the stopped state.
					AL10.alSourcePlay(source);
					AL10.alSourceStop(source);
				}
			}
			else if (!paused && state != AL10.AL_PLAYING)
				AL10.alSourcePlay(source);
		}

		public void SetCategoryVolume(SoundCategory category, float volume)
		{
			categoryVolumes[category] = volume;
			foreach (var slot in sourcePool.Values)
				if (slot.Sound != null && !slot.Sound.Complete && slot.Sound.Category == category)
					slot.Sound.SetCategoryVolume(volume);
		}

		public void SetSoundVolume(float volume, ISound music, ISound video)
		{
			SetCategoryVolume(SoundCategory.SoundEffect, volume);
		}

		public void StopSounds(SoundCategory category)
		{
			foreach (var slot in sourcePool.Values)
				if (slot.Sound != null && !slot.Sound.Complete && slot.Sound.Category == category)
					StopSound(slot.Sound);
		}

		public void StopSound(ISound sound)
		{
			((OpenAlSound)sound)?.Stop();
		}

		public void StopAllSounds()
		{
			foreach (var slot in sourcePool.Values)
				slot.Sound?.Stop();
		}

		public void SetListenerPosition(WPos position, int viewportHalfWidth)
		{
			currentListenerPos = position;

			// Lift the listener out of the battlefield plane for panning purposes only (see ComputeDistanceGain
			// for why volume falloff doesn't use this). The height is derived from the current viewport width and
			// re-read every frame, so zoom changes update the panning live.
			var height = Math.Max(MinListenerHeight, (int)(viewportHalfWidth / PanSpread));
			AL10.alListener3f(AL10.AL_POSITION, position.X, position.Y, position.Z + height);

			var orientation = new[] { 0f, 0f, 1f, 0f, -1f, 0f };
			AL10.alListenerfv(AL10.AL_ORIENTATION, orientation);
			AL10.alListenerf(EFX.AL_METERS_PER_UNIT, .01f);

			// Refresh every active world (non-relative) source's distance-based volume now that the listener has
			// moved. Sources' own positions are kept current by SetSoundPosition below.
			foreach (var slot in sourcePool.Values)
			{
				if (!slot.IsActive || slot.IsRelative || slot.Sound == null || slot.Sound.Complete)
					continue;

				slot.Sound.SetDistanceGain(ComputeDistanceGain(slot.Pos));
			}
		}

		public void SetSoundLooping(bool looping, ISound sound)
		{
			((OpenAlSound)sound)?.SetLooping(looping);
		}

		public void SetSoundPosition(ISound sound, WPos position)
		{
			var alSound = (OpenAlSound)sound;
			if (alSound == null)
				return;

			if (sourcePool.TryGetValue(alSound.Source, out var slot))
			{
				slot.Pos = position;
				if (!slot.IsRelative)
					alSound.SetDistanceGain(ComputeDistanceGain(position));
			}

			alSound.SetPosition(position);
		}

		~OpenAlSoundEngine()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		void Dispose(bool disposing)
		{
			if (disposing)
				StopAllSounds();

			if (sourcePool.Count > 0)
				AL10.alDeleteSources(PoolSize, sourcePool.Keys.ToArray());

			sourcePool.Clear();

			if (context != IntPtr.Zero)
			{
				ALC10.alcMakeContextCurrent(IntPtr.Zero);
				ALC10.alcDestroyContext(context);
				context = IntPtr.Zero;
			}

			if (device != IntPtr.Zero)
			{
				ALC10.alcCloseDevice(device);
				device = IntPtr.Zero;
			}
		}
	}

	sealed class OpenAlSoundSource : ISoundSource
	{
		uint buffer;
		bool disposed;

		public uint Buffer => buffer;
		public int SampleRate { get; }

		public OpenAlSoundSource(byte[] data, int byteCount, int channels, int sampleBits, int sampleRate)
		{
			SampleRate = sampleRate;
			AL10.alGenBuffers(1, out buffer);
			AL10.alBufferData(buffer, OpenAlSoundEngine.MakeALFormat(channels, sampleBits), data, byteCount, sampleRate);
		}

		void Dispose(bool _)
		{
			if (!disposed)
			{
				AL10.alDeleteBuffers(1, ref buffer);
				disposed = true;
			}
		}

		~OpenAlSoundSource()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
	}

	class OpenAlSound : ISound
	{
		internal uint Source { get; private set; }
		internal SoundCategory Category { get; }
		protected readonly float SampleRate;

		bool done;
		float logicalVolume;
		float distanceGain;
		float categoryVolume;

		public OpenAlSound(uint source, bool looping, bool relative, WPos pos, float volume, int sampleRate, uint buffer,
			float distanceGain, SoundCategory category, float categoryVolume)
			: this(source, looping, relative, pos, volume, sampleRate, distanceGain, category, categoryVolume)
		{
			AL10.alSourcei(source, AL10.AL_BUFFER, (int)buffer);
			AL10.alSourcePlay(source);
		}

		protected OpenAlSound(uint source, bool looping, bool relative, WPos pos, float volume, int sampleRate, float distanceGain,
			SoundCategory category = SoundCategory.None, float categoryVolume = 1f)
		{
			Source = source;
			SampleRate = sampleRate;
			this.distanceGain = distanceGain;
			Category = category;
			this.categoryVolume = categoryVolume;
			Volume = volume;

			AL10.alSourcef(source, AL10.AL_PITCH, 1f);
			AL10.alSource3f(source, AL10.AL_POSITION, pos.X, pos.Y, pos.Z);
			AL10.alSource3f(source, AL10.AL_VELOCITY, 0f, 0f, 0f);
			AL10.alSourcei(source, AL10.AL_LOOPING, looping ? 1 : 0);
			AL10.alSourcei(source, AL10.AL_SOURCE_RELATIVE, relative ? 1 : 0);

			// Volume falloff by distance is computed manually and applied through AL_GAIN instead - see
			// OpenAlSoundEngine.ComputeDistanceGain for why.
			AL10.alSourcef(source, AL10.AL_ROLLOFF_FACTOR, 0f);
		}

		internal void UnbindSource()
		{
			done = true;
			Source = uint.MaxValue;
		}

		// Re-applies AL_GAIN as logicalVolume * distanceGain * categoryVolume. Called whenever an input changes:
		// Volume by callers adjusting the logical/pre-attenuation volume, distance gain as the source or listener
		// moves, and category volume when its settings slider or mute state changes.
		internal void SetDistanceGain(float distanceGain)
		{
			if (done)
				return;

			this.distanceGain = distanceGain;
			AL10.alSourcef(Source, AL10.AL_GAIN, logicalVolume * distanceGain * categoryVolume);
		}

		internal void SetCategoryVolume(float volume)
		{
			if (done)
				return;

			categoryVolume = volume;
			AL10.alSourcef(Source, AL10.AL_GAIN, logicalVolume * distanceGain * categoryVolume);
		}

		public float Volume
		{
			get => done ? float.NaN : logicalVolume;

			set
			{
				if (done)
					return;

				logicalVolume = value;
				AL10.alSourcef(Source, AL10.AL_GAIN, logicalVolume * distanceGain * categoryVolume);
			}
		}

		public virtual float SeekPosition
		{
			get
			{
				if (done)
					return float.NaN;

				AL10.alGetSourcei(Source, AL11.AL_SAMPLE_OFFSET, out var sampleOffset);
				return sampleOffset / SampleRate;
			}
		}

		public virtual bool Complete
		{
			get
			{
				if (done)
					return true;

				AL10.alGetSourcei(Source, AL10.AL_SOURCE_STATE, out var state);
				return state == AL10.AL_STOPPED;
			}
		}

		public void SetPosition(WPos pos)
		{
			if (done)
				return;

			AL10.alSource3f(Source, AL10.AL_POSITION, pos.X, pos.Y, pos.Z);
		}

		protected void StopSource()
		{
			if (done)
				return;

			AL10.alGetSourcei(Source, AL10.AL_SOURCE_STATE, out var state);
			if (state == AL10.AL_PLAYING || state == AL10.AL_PAUSED)
				AL10.alSourceStop(Source);
		}

		public virtual void Stop()
		{
			if (done)
				return;

			StopSource();
			AL10.alSourcei(Source, AL10.AL_BUFFER, 0);
		}

		public void SetLooping(bool looping)
		{
			if (done)
				return;

			AL10.alSourcei(Source, AL10.AL_LOOPING, looping ? AL10.AL_TRUE : AL10.AL_FALSE);
		}
	}

	sealed class OpenAlAsyncLoadSound : OpenAlSound
	{
		static readonly byte[] SilentData = new byte[2];
		readonly Lock syncObject = new();
		readonly CancellationTokenSource cts = new();
		readonly Task playTask;

		public OpenAlAsyncLoadSound(uint source, bool looping, bool relative, WPos pos, float volume, int channels, int sampleBits, int sampleRate, Stream stream,
			float distanceGain)
			: base(source, looping, relative, pos, volume, sampleRate, distanceGain)
		{
			// Load a silent buffer into the source. Without this,
			// attempting to change the state (i.e. play/pause) the source fails on some systems.
			var silentSource = new OpenAlSoundSource(SilentData, SilentData.Length, channels, sampleBits, sampleRate);
			AL10.alSourcei(source, AL10.AL_BUFFER, (int)silentSource.Buffer);

			playTask = Task.Run(async () =>
			{
				MemoryStream memoryStream;
				using (stream)
				{
					try
					{
						memoryStream = new MemoryStream((int)stream.Length);
					}
					catch (NotSupportedException)
					{
						// Fallback for stream types that don't support Length.
						memoryStream = new MemoryStream();
					}

					try
					{
						await stream.CopyToAsync(memoryStream, 81920, cts.Token);
					}
					catch (TaskCanceledException)
					{
						// Sound was stopped early, cleanup the unused buffer and exit.
						AL10.alSourceStop(source);
						AL10.alSourcei(source, AL10.AL_BUFFER, 0);
						silentSource.Dispose();
						return;
					}
				}

				var data = memoryStream.GetBuffer();
				var dataLength = (int)memoryStream.Length;
				var bytesPerSample = sampleBits / 8f;
				var lengthInSecs = dataLength / (channels * bytesPerSample * sampleRate);
				using (var soundSource = new OpenAlSoundSource(data, dataLength, channels, sampleBits, sampleRate))
				{
					// Need to stop the source, before attaching the real input and deleting the silent one.
					AL10.alSourceStop(source);
					AL10.alSourcei(source, AL10.AL_BUFFER, (int)soundSource.Buffer);
					silentSource.Dispose();

					lock (syncObject)
					{
						if (!cts.IsCancellationRequested)
						{
							// TODO: A race condition can happen between the state check and playing/rewinding if a
							// user pauses/resumes at the right moment. The window of opportunity is small and the
							// consequences are minor, so for now we'll ignore it.
							AL10.alGetSourcei(Source, AL10.AL_SOURCE_STATE, out var state);
							if (state != AL10.AL_STOPPED)
								AL10.alSourcePlay(source);
							else
							{
								// A stopped sound indicates it was paused before we finishing loaded.
								// We don't want to start playing it right away.
								// We rewind the source so when it is started, it plays from the beginning.
								AL10.alSourceRewind(source);
							}
						}
					}

					while (!cts.IsCancellationRequested)
					{
						// Need to check seek before state. Otherwise, the music can stop after our state check at
						// which point the seek will be zero, meaning we'll wait the full track length before seeing it
						// has stopped.
						var currentSeek = SeekPosition;

						AL10.alGetSourcei(Source, AL10.AL_SOURCE_STATE, out var state);
						if (state == AL10.AL_STOPPED)
							break;

						try
						{
							// Wait until the track is due to complete, and at most 60 times a second to prevent a
							// busy-wait.
							var delaySecs = Math.Max(lengthInSecs - currentSeek, 1 / 60f);
							await Task.Delay(TimeSpan.FromSeconds(delaySecs), cts.Token);
						}
						catch (TaskCanceledException)
						{
							// Sound was stopped early, allow normal cleanup to occur.
						}
					}

					AL10.alSourcei(Source, AL10.AL_BUFFER, 0);
				}
			});
		}

		public override void Stop()
		{
			lock (syncObject)
			{
				StopSource();
				cts.Cancel();
			}

			try
			{
				playTask.Wait();
			}
			catch (AggregateException)
			{
			}
		}

		public override bool Complete => playTask.IsCompleted;
	}
}
