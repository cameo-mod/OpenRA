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

using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Graphics
{
	public enum BeamRenderableShape { Cylindrical, Flat }
	public class BeamRenderable : IRenderable, IFinalizedRenderable
	{
		readonly WVec length;
		readonly BeamRenderableShape shape;
		readonly WDist startWidth;
		readonly WDist endWidth;
		readonly Color startColor;
		readonly Color endColor;
		readonly float glowIntensity;

		public BeamRenderable(WPos pos, int zOffset, in WVec length, BeamRenderableShape shape, WDist width, Color color,
			float glowIntensity = 1f)
			: this(pos, zOffset, length, shape, width, width, color, color, glowIntensity, false) { }

		public BeamRenderable(WPos pos, int zOffset, in WVec length, BeamRenderableShape shape,
			WDist startWidth, WDist endWidth, Color startColor, Color endColor, float glowIntensity = 1f)
			: this(pos, zOffset, length, shape, startWidth, endWidth, startColor, endColor, glowIntensity, true) { }

		BeamRenderable(WPos pos, int zOffset, in WVec length, BeamRenderableShape shape,
			WDist startWidth, WDist endWidth, Color startColor, Color endColor, float glowIntensity, bool endpointVariation)
		{
			if (endpointVariation && shape != BeamRenderableShape.Flat)
				throw new System.ArgumentException("Endpoint width and color variation is only supported for flat beams.", nameof(shape));

			Pos = pos;
			ZOffset = zOffset;
			this.length = length;
			this.shape = shape;
			this.startWidth = startWidth;
			this.endWidth = endWidth;
			this.startColor = startColor;
			this.endColor = endColor;
			this.glowIntensity = glowIntensity;
		}

		public WPos Pos { get; }
		public int ZOffset { get; }
		public bool IsDecoration => true;

		public IRenderable WithZOffset(int newOffset)
		{
			return new BeamRenderable(Pos, ZOffset, length, shape,
				startWidth, endWidth, startColor, endColor, glowIntensity, shape == BeamRenderableShape.Flat);
		}

		public IRenderable OffsetBy(in WVec vec)
		{
			return new BeamRenderable(Pos + vec, ZOffset, length, shape,
				startWidth, endWidth, startColor, endColor, glowIntensity, shape == BeamRenderableShape.Flat);
		}
		public IRenderable AsDecoration() { return this; }

		public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }
		public void Render(WorldRenderer wr)
		{
			var vecLength = length.Length;
			if (vecLength == 0)
				return;

			if (Game.Settings.Graphics.LaserGlow)
				wr.World.WorldActor.TraitOrDefault<GlowRenderer>()
					?.RegisterGlow(Pos, Pos + length, endColor,
						System.Math.Max(startWidth.Length, endWidth.Length) / 86f, intensity: glowIntensity);

			if (shape == BeamRenderableShape.Flat)
			{
				var startDelta = length * startWidth.Length / (2 * vecLength);
				var endDelta = length * endWidth.Length / (2 * vecLength);
				var startCorner = new WVec(-startDelta.Y, startDelta.X, startDelta.Z);
				var endCorner = new WVec(-endDelta.Y, endDelta.X, endDelta.Z);
				var a = wr.Screen3DPosition(Pos - startCorner);
				var b = wr.Screen3DPosition(Pos + startCorner);
				var c = wr.Screen3DPosition(Pos + endCorner + length);
				var d = wr.Screen3DPosition(Pos - endCorner + length);
				Game.Renderer.WorldRgbaColorRenderer.FillRect(a, b, c, d,
					startColor, startColor, endColor, endColor);
			}
			else
			{
				var start = wr.Screen3DPosition(Pos);
				var end = wr.Screen3DPosition(Pos + length);
				var screenWidth = wr.ScreenVector(new WVec(endWidth, WDist.Zero, WDist.Zero))[0];
				Game.Renderer.WorldRgbaColorRenderer.DrawLine(start, end, screenWidth, endColor);
			}
		}

		public void RenderDebugGeometry(WorldRenderer wr) { }
		public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
	}
}
