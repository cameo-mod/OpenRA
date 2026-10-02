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

using OpenRA.Primitives;

namespace OpenRA.Graphics
{
	public static class SpriteFrameScaler
	{
		public static ISpriteFrame Downscale(ISpriteFrame frame)
		{
			// Halving a 1px (or 0px) dimension would collapse the frame to nothing.
			if (frame.Size.Width < 2 || frame.Size.Height < 2)
				return frame;

			var bytesPerPixel = BytesPerPixel(frame.Type);
			var srcWidth = frame.Size.Width;
			var srcHeight = frame.Size.Height;
			var dstWidth = srcWidth / 2;
			var dstHeight = srcHeight / 2;

			var srcData = frame.Data;
			var dstData = new byte[dstWidth * dstHeight * bytesPerPixel];

			// Nearest-neighbor sampling: Indexed8 pixels are palette indices, not colors,
			// so channels cannot be averaged/blended without producing invalid indices.
			for (var y = 0; y < dstHeight; y++)
			{
				var srcRow = (y * 2) * srcWidth;
				var dstRow = y * dstWidth;
				for (var x = 0; x < dstWidth; x++)
				{
					var srcOffset = (srcRow + x * 2) * bytesPerPixel;
					var dstOffset = (dstRow + x) * bytesPerPixel;
					for (var b = 0; b < bytesPerPixel; b++)
						dstData[dstOffset + b] = srcData[srcOffset + b];
				}
			}

			return new ScaledSpriteFrame(
				frame.Type,
				new Size(dstWidth, dstHeight),
				new Size(frame.FrameSize.Width / 2, frame.FrameSize.Height / 2),
				frame.Offset / 2,
				dstData,
				frame.DisableExportPadding);
		}

		static int BytesPerPixel(SpriteFrameType type)
		{
			return type switch
			{
				SpriteFrameType.Indexed8 => 1,
				SpriteFrameType.Bgr24 or SpriteFrameType.Rgb24 => 3,
				_ => 4
			};
		}

		sealed class ScaledSpriteFrame : ISpriteFrame
		{
			public SpriteFrameType Type { get; }
			public Size Size { get; }
			public Size FrameSize { get; }
			public float2 Offset { get; }
			public byte[] Data { get; }
			public bool DisableExportPadding { get; }

			public ScaledSpriteFrame(SpriteFrameType type, Size size, Size frameSize, float2 offset, byte[] data, bool disableExportPadding)
			{
				Type = type;
				Size = size;
				FrameSize = frameSize;
				Offset = offset;
				Data = data;
				DisableExportPadding = disableExportPadding;
			}
		}
	}
}
