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
using System.Linq;
using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Renders a decorative animation on units and buildings.")]
	public class WithIdleOverlayInfo : PausableConditionalTraitInfo, IRenderActorPreviewSpritesInfo,
		Requires<RenderSpritesInfo>, Requires<BodyOrientationInfo>, NotBefore<WithSpriteBodyInfo>
	{
		[Desc("Image used for this decoration. Defaults to the actor's type.")]
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Animation to play when the actor is created.")]
		public readonly string StartSequence = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Sequence name to use")]
		public readonly string Sequence = "idle-overlay";

		[Desc("Position relative to body")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Add the current sprite offset from FollowBodyName to this overlay.",
			"Use this for overlays that must follow a visually offset sprite body without actor-specific overlay offsets.")]
		public readonly bool FollowBodySpriteOffset = false;

		[Desc("Sprite body name whose current sequence offset is followed when FollowBodySpriteOffset is enabled.")]
		public readonly string FollowBodyName = "body";

		[PaletteReference(nameof(IsPlayerPalette))]
		[Desc("Custom palette name")]
		public readonly string Palette = null;

		[Desc("Custom palette is a player palette BaseName")]
		public readonly bool IsPlayerPalette = false;

		public readonly bool IsDecoration = false;

		internal static WVec SpriteOffsetToWorld(Size tileSize, int tileScale, in Vector3 offset)
		{
			return new WVec(
				(int)Math.Round(offset.X * tileScale / tileSize.Width),
				(int)Math.Round(offset.Y * tileScale / tileSize.Height),
				0);
		}

		internal static WVec SpriteOffsetToWorld(World world, in Vector3 offset)
		{
			return SpriteOffsetToWorld(world.Map.Rules.TerrainInfo.TileSize, world.Map.Grid.TileScale, offset);
		}

		// Allows generated sequence tables to select an actor-specific sequence without
		// duplicating the overlay trait on every concrete actor definition.
		public static string ResolveSequence(string sequence, string actorName)
		{
			return sequence?.Replace("{actor}", actorName, StringComparison.OrdinalIgnoreCase);
		}

		public override object Create(ActorInitializer init) { return new WithIdleOverlay(init.Self, this); }

		public IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, string image, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			if (Palette != null)
				p = init.WorldRenderer.Palette(IsPlayerPalette ? Palette + init.Get<OwnerInit>().InternalName : Palette);

			Func<WAngle> facing;
			var dynamicfacingInit = init.GetOrDefault<DynamicFacingInit>();
			if (dynamicfacingInit != null)
				facing = dynamicfacingInit.Value;
			else
			{
				var f = init.GetValue<FacingInit, WAngle>(WAngle.Zero);
				facing = () => f;
			}

			var anim = new Animation(init.World, Image ?? image, facing)
			{
				IsDecoration = IsDecoration
			};

			var sequence = ResolveSequence(Sequence, init.Actor.Name);
			anim.PlayRepeating(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), sequence));

			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			Animation followedBody = null;
			if (FollowBodySpriteOffset)
			{
				var followedBodyInfo = init.Actor.TraitInfos<WithSpriteBodyInfo>()
					.FirstOrDefault(candidate => candidate.Name == FollowBodyName);
				if (followedBodyInfo == null)
					throw new InvalidOperationException($"WithIdleOverlay FollowBodyName '{FollowBodyName}' does not exist on {init.Actor.Name}.");

				followedBody = new Animation(init.World, image, facing);
				followedBody.PlayRepeating(RenderSprites.NormalizeSequence(followedBody, init.GetDamageState(), followedBodyInfo.Sequence));
			}

			WRot Orientation() => body.QuantizeOrientation(WRot.FromYaw(facing()), facings);
			WVec Offset()
			{
				var offset = body.LocalToWorld(this.Offset.Rotate(Orientation()));
				if (followedBody?.CurrentSequence != null)
					offset += SpriteOffsetToWorld(init.World, followedBody.CurrentSequence.Scale * followedBody.Image.Offset);

				return offset;
			}
			int ZOffset()
			{
				var tmpOffset = Offset();
				return FollowBodySpriteOffset ? -(tmpOffset.Y + tmpOffset.Z) + 1 : tmpOffset.Y + tmpOffset.Z + 1;
			}

			yield return new SpriteActorPreview(anim, Offset, ZOffset, p);
		}
	}

	public class WithIdleOverlay : PausableConditionalTrait<WithIdleOverlayInfo>, INotifyDamageStateChanged
	{
		readonly Animation overlay;

		public WithIdleOverlay(Actor self, WithIdleOverlayInfo info)
			: base(info)
		{
			var rs = self.Trait<RenderSprites>();
			var body = self.Trait<BodyOrientation>();
			var facing = self.TraitOrDefault<IFacing>();
			var followedBody = info.FollowBodySpriteOffset ? self.TraitsImplementing<WithSpriteBody>()
				.FirstOrDefault(candidate => candidate.Info.Name == info.FollowBodyName) : null;
			if (info.FollowBodySpriteOffset && followedBody == null)
				throw new InvalidOperationException($"WithIdleOverlay FollowBodyName '{info.FollowBodyName}' does not exist on {self.Info.Name}.");

			var image = info.Image ?? rs.GetImage(self);
			overlay = new Animation(self.World, image,
				facing == null ? () => WAngle.Zero : (body == null ? () => facing.Facing : () => body.QuantizeFacing(facing.Facing)),
				() => IsTraitPaused)
			{
				IsDecoration = info.IsDecoration
			};

			var sequence = WithIdleOverlayInfo.ResolveSequence(info.Sequence, self.Info.Name);
			var startSequence = WithIdleOverlayInfo.ResolveSequence(info.StartSequence, self.Info.Name);
			if (startSequence != null)
				overlay.PlayThen(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), startSequence),
					() => overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), sequence)));
			else
				overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), sequence));

			WVec OverlayOffset()
			{
				var offset = body.LocalToWorld(info.Offset.Rotate(body.QuantizeOrientation(self.Orientation)));
				if (followedBody?.DefaultAnimation.CurrentSequence != null)
					offset += WithIdleOverlayInfo.SpriteOffsetToWorld(self.World,
						followedBody.DefaultAnimation.CurrentSequence.Scale * followedBody.DefaultAnimation.Image.Offset);

				return offset;
			}

			var anim = new AnimationWithOffset(overlay,
				OverlayOffset,
				() => IsTraitDisabled,
				p => RenderUtils.ZOffsetFromCenter(self, p, 1));

			rs.Add(anim, info.Palette, info.IsPlayerPalette);
		}

		void INotifyDamageStateChanged.DamageStateChanged(Actor self, AttackInfo e)
		{
			overlay.ReplaceAnim(RenderSprites.NormalizeSequence(overlay, e.DamageState, overlay.CurrentSequence.Name));
		}
	}
}
