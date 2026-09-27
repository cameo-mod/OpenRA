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

using System.Collections.Generic;
using System.Numerics;
using OpenRA.Primitives;

namespace OpenRA.Graphics
{
	public readonly struct SpriteMaterialization
	{
		public readonly float BoundaryY;
		public readonly float CoreHeight;
		public readonly Vector3 SilhouetteColor;
		public readonly float SilhouetteAlpha;
		public readonly Vector3 CoreColor;
		public readonly float CoreAlpha;
		public readonly Vector3 InnerGlowColor;
		public readonly float InnerGlowAlpha;
		public readonly Vector3 OuterGlowColor;
		public readonly float OuterGlowAlpha;
		public readonly Vector3 AfterglowColor;
		public readonly float AfterglowAlpha;

		public SpriteMaterialization(float boundaryY, float coreHeight,
			in Vector3 silhouetteColor, float silhouetteAlpha,
			in Vector3 coreColor, float coreAlpha,
			in Vector3 innerGlowColor, float innerGlowAlpha,
			in Vector3 outerGlowColor, float outerGlowAlpha,
			in Vector3 afterglowColor, float afterglowAlpha)
		{
			BoundaryY = boundaryY;
			CoreHeight = coreHeight;
			SilhouetteColor = silhouetteColor;
			SilhouetteAlpha = silhouetteAlpha;
			CoreColor = coreColor;
			CoreAlpha = coreAlpha;
			InnerGlowColor = innerGlowColor;
			InnerGlowAlpha = innerGlowAlpha;
			OuterGlowColor = outerGlowColor;
			OuterGlowAlpha = outerGlowAlpha;
			AfterglowColor = afterglowColor;
			AfterglowAlpha = afterglowAlpha;
		}
	}

	public class SpriteRenderable : IPalettedRenderable, IModifyableRenderable, IFinalizedRenderable
	{
		public static readonly IEnumerable<IRenderable> None = [];

		readonly Sprite sprite;
		readonly WPos pos;
		readonly float scale;
		readonly WAngle rotation = WAngle.Zero;
		readonly SpriteMaterialization? materialization;

		public SpriteRenderable(Sprite sprite, WPos pos, WVec offset, int zOffset, PaletteReference palette, float scale, float alpha,
			Vector3 tint, TintModifiers tintModifiers, bool isDecoration, WAngle rotation)
			: this(sprite, pos, offset, zOffset, palette, scale, alpha, tint, tintModifiers, isDecoration, rotation, null) { }

		SpriteRenderable(Sprite sprite, WPos pos, WVec offset, int zOffset, PaletteReference palette, float scale, float alpha,
			Vector3 tint, TintModifiers tintModifiers, bool isDecoration, WAngle rotation, SpriteMaterialization? materialization)
		{
			this.sprite = sprite;
			this.pos = pos;
			Offset = offset;
			ZOffset = zOffset;
			Palette = palette;
			this.scale = scale;
			this.rotation = rotation;
			Tint = tint;
			IsDecoration = isDecoration;
			TintModifiers = tintModifiers;
			Alpha = alpha;
			this.materialization = materialization;

			// PERF: Remove useless palette assignments for RGBA sprites
			// HACK: This is working around the fact that palettes are defined on traits rather than sequences
			// and can be removed once this has been fixed
			if (sprite.Channel == TextureChannel.RGBA && !(palette?.HasColorShift ?? false))
				Palette = null;
		}

		public SpriteRenderable(Sprite sprite, WPos pos, WVec offset, int zOffset, PaletteReference palette, float scale, float alpha,
			Vector3 tint, TintModifiers tintModifiers, bool isDecoration)
			: this(sprite, pos, offset, zOffset, palette, scale, alpha, tint, tintModifiers, isDecoration, WAngle.Zero) { }

		public WPos Pos => pos + Offset;
		public WVec Offset { get; }
		public PaletteReference Palette { get; }
		public int ZOffset { get; }
		public bool IsDecoration { get; }

		public float Alpha { get; }
		public Vector3 Tint { get; }
		public TintModifiers TintModifiers { get; }

		public IPalettedRenderable WithPalette(PaletteReference newPalette)
		{
			return new SpriteRenderable(sprite, pos, Offset, ZOffset, newPalette, scale, Alpha, Tint, TintModifiers,
				IsDecoration, rotation, materialization);
		}

		public IRenderable WithZOffset(int newOffset)
		{
			return new SpriteRenderable(sprite, pos, Offset, newOffset, Palette, scale, Alpha, Tint, TintModifiers,
				IsDecoration, rotation, materialization);
		}

		public IRenderable OffsetBy(in WVec vec)
		{
			return new SpriteRenderable(sprite, pos + vec, Offset, ZOffset, Palette, scale, Alpha, Tint, TintModifiers,
				IsDecoration, rotation, materialization);
		}

		public IRenderable AsDecoration()
		{
			return new SpriteRenderable(sprite, pos, Offset, ZOffset, Palette, scale, Alpha, Tint, TintModifiers,
				true, rotation, materialization);
		}

		public IModifyableRenderable WithAlpha(float newAlpha)
		{
			return new SpriteRenderable(sprite, pos, Offset, ZOffset, Palette, scale, newAlpha, Tint, TintModifiers,
				IsDecoration, rotation, materialization);
		}

		public IModifyableRenderable WithTint(in Vector3 newTint, TintModifiers newTintModifiers)
		{
			return new SpriteRenderable(sprite, pos, Offset, ZOffset, Palette, scale, Alpha, newTint, newTintModifiers,
				IsDecoration, rotation, materialization);
		}

		public SpriteRenderable WithMaterialization(in SpriteMaterialization value)
		{
			return new SpriteRenderable(sprite, pos, Offset, ZOffset, Palette, scale, Alpha, Tint, TintModifiers,
				IsDecoration, rotation, value);
		}

		Vector3 ScreenPosition(WorldRenderer wr)
		{
			var s = 0.5f * scale * sprite.Size;
			return wr.Screen3DPxPosition(pos) + wr.ScreenPxOffset(Offset).ToVector3() - new Vector3((int)s.X, (int)s.Y, s.Z);
		}

		public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }
		public void Render(WorldRenderer wr)
		{
			var wsr = Game.Renderer.WorldSpriteRenderer;
			var t = Alpha * Tint;
			if (wr.TerrainLighting != null && (TintModifiers & TintModifiers.IgnoreWorldTint) == 0)
				t *= wr.TerrainLighting.TintAt(pos);

			// Shader interprets alpha below -2 as replacement tint that preserves sampled sprite alpha.
			// The -2 offset keeps this distinct from the legacy negative-alpha replacement mode.
			var a = Alpha;
			if ((TintModifiers & TintModifiers.ReplaceColorPreserveAlpha) != 0)
				a = -2f - a;
			else if ((TintModifiers & TintModifiers.ReplaceColor) != 0)
				a *= -1;

			if (materialization.HasValue)
				wsr.DrawMaterializedSprite(sprite, Palette, ScreenPosition(wr), scale, t, a,
					rotation.RendererRadians(), materialization.Value);
			else
				wsr.DrawSprite(sprite, Palette, ScreenPosition(wr), scale, t, a, rotation.RendererRadians());
		}

		public void RenderDebugGeometry(WorldRenderer wr)
		{
			var pos = ScreenPosition(wr) + scale * sprite.Offset;
			var tl = wr.Viewport.WorldToViewPx(pos).ToVector3();
			var br = wr.Viewport.WorldToViewPx(pos + scale * sprite.Size).ToVector3();
			if (rotation == WAngle.Zero)
				Game.Renderer.RgbaColorRenderer.DrawRect(tl, br, 1, Color.Red);
			else
				Game.Renderer.RgbaColorRenderer.DrawPolygon(Util.RotateQuad(tl, br - tl, rotation.RendererRadians()), 1, Color.Red);
		}

		public Rectangle ScreenBounds(WorldRenderer wr)
		{
			return CalculateScreenBounds(ScreenPosition(wr), sprite.Offset, sprite.Size, scale, rotation.RendererRadians());
		}

		internal static Rectangle CalculateScreenBounds(in Vector3 screenPosition, in Vector3 spriteOffset,
			in Vector3 spriteSize, float scale, float rotation)
		{
			return Util.BoundingRectangle(screenPosition + scale * spriteOffset, scale * spriteSize, rotation);
		}
	}
}
