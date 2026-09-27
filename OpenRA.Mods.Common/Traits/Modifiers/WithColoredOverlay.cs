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
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Display a colored overlay when a timed condition is active.")]
	public class WithColoredOverlayInfo : ConditionalTraitInfo
	{
		[Desc("Color to overlay.")]
		public readonly Color Color = Color.FromArgb(128, 128, 0, 0);

		[Desc("Skip applying the color overlay to voxel renderables.")]
		public readonly bool SkipVoxels = false;

		[Desc("Override alpha for voxel renderables. Use -1 to use the same alpha as sprites.")]
		public readonly int VoxelAlpha = 30;

		[Desc("Multiply the sprite colour by the overlay (Photoshop \"Multiply\" blend) instead of",
			"compositing an opaque copy on top. This darkens/tints the sprite while preserving its",
			"underlying texture and detail. The Color's alpha controls the blend strength; the RGB is",
			"the colour multiplied toward (000000 = darken to black, FFFFFF = no change).")]
		public readonly bool Multiply = false;

		public override object Create(ActorInitializer init) { return new WithColoredOverlay(this); }
	}

	public class WithColoredOverlay : ConditionalTrait<WithColoredOverlayInfo>, IRenderModifier
	{
		readonly Vector3 tint;
		readonly float alpha;
		readonly Vector3 multiply;

		public WithColoredOverlay(WithColoredOverlayInfo info)
			: base(info)
		{
			var color = info.Color.ToVector4();
			tint = color.AsVector3();
			alpha = color.W;

			// Photoshop "Multiply" at <alpha> opacity: lerp the per-channel multiplier from
			// white (1 = no change) toward the tint colour. At full alpha the sprite is multiplied
			// straight by the tint; at lower alpha the darkening is proportionally weaker.
			var white = Vector3.One;
			multiply = white + alpha * (tint - white);
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (IsTraitDisabled)
				return r;

			return ModifiedRender(r);
		}

		IEnumerable<IRenderable> ModifiedRender(IEnumerable<IRenderable> r)
		{
			foreach (var a in r)
			{
					var isVoxel = a.GetType().Name == "ModelRenderable";

				if (!a.IsDecoration && a is IModifyableRenderable ma)
				{
					if (Info.SkipVoxels && isVoxel)
					{
						yield return a;
						continue;
					}

					if (Info.Multiply)
					{
						// Multiply blend: scale the renderable's own tint toward the overlay colour
						// (the shader's default path multiplies the sprite colour by the tint). This keeps
						// the sprite's texture/detail visible instead of pasting a flat colour over it.
						yield return ma.WithTint(ma.Tint * multiply, ma.TintModifiers);
					}
					else if (isVoxel && Info.VoxelAlpha >= 0)
					{
						// For voxels: use OverlayTint mode — adds tint colour on top of model without
						// replacing colours, and draws shadow untinted (handled in ModelRenderable)
						// Scale tint RGB by VoxelAlpha so the additive strength is controlled by VoxelAlpha
						var voxelStrength = Info.VoxelAlpha / 255f;
						var scaledTint = new Vector3(tint.X * voxelStrength, tint.Y * voxelStrength, tint.Z * voxelStrength);
						// Pass alpha=2f as sentinel — shader sees vTint.a > 1.0 and uses additive mode
						yield return ma.WithTint(scaledTint, TintModifiers.OverlayTint).WithAlpha(2f);
					}
					else
					{
						// For sprites: yield original + tinted overlay copy
						yield return a;
						yield return ma.WithTint(tint, ma.TintModifiers | TintModifiers.ReplaceColor).WithAlpha(alpha);
					}
				}
				else
				{
					yield return a;
				}
			}
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}
	}
}
