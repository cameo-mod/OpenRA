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

using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Graphics
{
	public class LensFlareRenderable : IRenderable, IFinalizedRenderable
	{
		Color rayColor;
		Color coreColor;
		float horizontalLength;
		float verticalLength;
		float rayWidth;
		float coreSize;

		public LensFlareRenderable(WPos pos, int zOffset, Color rayColor, Color coreColor,
			float horizontalLength, float verticalLength, float rayWidth, float coreSize)
		{
			Update(pos, zOffset, rayColor, coreColor, horizontalLength, verticalLength, rayWidth, coreSize);
		}

		public void Update(WPos pos, int zOffset, Color rayColor, Color coreColor,
			float horizontalLength, float verticalLength, float rayWidth, float coreSize)
		{
			Pos = pos;
			ZOffset = zOffset;
			this.rayColor = rayColor;
			this.coreColor = coreColor;
			this.horizontalLength = horizontalLength;
			this.verticalLength = verticalLength;
			this.rayWidth = rayWidth;
			this.coreSize = coreSize;
		}

		public WPos Pos { get; private set; }
		public int ZOffset { get; private set; }
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset)
		{
			return new LensFlareRenderable(Pos, newOffset, rayColor, coreColor,
				horizontalLength, verticalLength, rayWidth, coreSize);
		}

		public IRenderable OffsetBy(in WVec vec)
		{
			return new LensFlareRenderable(Pos + vec, ZOffset, rayColor, coreColor,
				horizontalLength, verticalLength, rayWidth, coreSize);
		}

		public IRenderable AsDecoration() { return this; }
		public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }

		public void Render(WorldRenderer wr)
		{
			var center = wr.Screen3DPosition(Pos);
			var renderer = Game.Renderer.WorldRgbaColorRenderer;
			var transparentRay = Color.FromArgb(0, rayColor);
			var transparentCore = Color.FromArgb(0, coreColor);

			var left = center - new Vector3(horizontalLength / 2, 0, 0);
			var right = center + new Vector3(horizontalLength / 2, 0, 0);
			var top = center - new Vector3(0, verticalLength / 2, 0);
			var bottom = center + new Vector3(0, verticalLength / 2, 0);

			renderer.DrawLine(left, center, rayWidth, transparentRay, rayColor, BlendMode.Additive);
			renderer.DrawLine(center, right, rayWidth, rayColor, transparentRay, BlendMode.Additive);
			renderer.DrawLine(top, center, rayWidth, transparentRay, rayColor, BlendMode.Additive);
			renderer.DrawLine(center, bottom, rayWidth, rayColor, transparentRay, BlendMode.Additive);

			var coreRayWidth = rayWidth > 1f ? rayWidth / 2 : 1f;
			var coreLeft = center - new Vector3(horizontalLength / 4, 0, 0);
			var coreRight = center + new Vector3(horizontalLength / 4, 0, 0);
			var coreTop = center - new Vector3(0, verticalLength / 4, 0);
			var coreBottom = center + new Vector3(0, verticalLength / 4, 0);
			renderer.DrawLine(coreLeft, center, coreRayWidth, transparentCore, coreColor, BlendMode.Additive);
			renderer.DrawLine(center, coreRight, coreRayWidth, coreColor, transparentCore, BlendMode.Additive);
			renderer.DrawLine(coreTop, center, coreRayWidth, transparentCore, coreColor, BlendMode.Additive);
			renderer.DrawLine(center, coreBottom, coreRayWidth, coreColor, transparentCore, BlendMode.Additive);

			var coreOffset = new Vector3(coreSize / 2, coreSize / 2, 0);
			renderer.FillEllipse(center - coreOffset, center + coreOffset, coreColor, BlendMode.Additive);
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
