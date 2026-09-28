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
using OpenRA.Primitives;

namespace OpenRA.Platforms.Default
{
	public class DefaultPlatform : IPlatform
	{
		public IPlatformWindow CreateWindow(
			Size size, WindowMode windowMode, float scaleModifier, int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			return new Sdl2PlatformWindow(size, windowMode, scaleModifier, vertexBatchSize, indexBatchSize, videoDisplay, profile);
		}

		public ISoundEngine CreateSound(string device)
		{
			if (string.Equals(device, "none", StringComparison.OrdinalIgnoreCase))
			{
				Log.Write("sound", "Sound.Device=none selected; using DummySoundEngine.");
				return new DummySoundEngine();
			}

			if (Environment.GetEnvironmentVariable("OPENRA_NO_AUDIO") == "1")
			{
				Log.Write("sound", "OPENRA_NO_AUDIO=1 selected; using DummySoundEngine.");
				return new DummySoundEngine();
			}

			try
			{
				return new OpenAlSoundEngine(device);
			}
			catch (InvalidOperationException e)
			{
				Log.Write("sound", "Failed to initialize OpenAL device. Error was");
				Log.Write("sound", e);
				return new DummySoundEngine();
			}
		}

		public IFont CreateFont(byte[] data)
		{
			return new FreeTypeFont(data);
		}
	}
}
