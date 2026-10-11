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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.HitShapes
{
	public class PolygonShape : IHitShape
	{
		public WDist OuterRadius { get; private set; }

		[FieldLoader.Require]
		public readonly ImmutableArray<int2> Points;

		[Desc("Defines the top offset relative to the actor's center.")]
		public readonly int VerticalTopOffset = 0;

		[Desc("Defines the bottom offset relative to the actor's center.")]
		public readonly int VerticalBottomOffset = 0;

		[Desc("Rotates shape by an angle relative to actor facing. Mostly required for buildings on isometric terrain.",
			"Mobile actors do NOT need this!")]
		public readonly WAngle LocalYaw = WAngle.Zero;

		WVec[] combatOverlayVertsTop;
		WVec[] combatOverlayVertsBottom;
		long[] squares;

		public PolygonShape() { }

		public PolygonShape(ImmutableArray<int2> points) { Points = points; }

		public void Initialize()
		{
			if (VerticalTopOffset < VerticalBottomOffset)
				throw new YamlException("VerticalTopOffset must be equal to or higher than VerticalBottomOffset.");

			OuterRadius = new WDist(Points.Max(x => x.Length));
			combatOverlayVertsTop = Points.Select(p => new WVec(p.X, p.Y, VerticalTopOffset)).ToArray();
			combatOverlayVertsBottom = Points.Select(p => new WVec(p.X, p.Y, VerticalBottomOffset)).ToArray();
			squares = new long[Points.Length];
			squares[0] = LengthSquaredLong(Points[0] - Points[^1]);
			for (var i = 1; i < Points.Length; i++)
				squares[i] = LengthSquaredLong(Points[i] - Points[i - 1]);
		}

		static long LengthSquaredLong(int2 v)
		{
			return (long)v.X * v.X + (long)v.Y * v.Y;
		}

		// All squared distances are computed in long: int2.LengthSquared wraps
		// silently past ~46340 units and a wrapped-negative min2 or min2 + z*z
		// crashes Exts.ISqrt (InvalidOperationException) — observed live in a
		// 4v4 campaign game (sqrt of -1868759040). Long math preserves every
		// result inside the previously valid int domain bit-for-bit.
		static long DistanceSquaredFromLineSegment(int2 c, int2 a, int2 b, long ab2)
		{
			var ac = c - a;
			var ac2 = LengthSquaredLong(ac);
			var bc2 = LengthSquaredLong(c - b);

			// c is closest to point a
			if (ac2 + ab2 <= bc2)
				return ac2;

			// c is closest to point b
			if (bc2 + ab2 <= ac2)
				return bc2;

			// c is closest to its unknown orthogonal projection (p) onto the line spanned by b with a as the origin
			// Cast to a long for the calculations to avoid overflows
			var ab = b - a;
			var ap2 = (long)ac.X * ab.X + (long)ac.Y * ab.Y;
			var apX = ab.X * ap2 / ab2;
			var apY = ab.Y * ap2 / ab2;
			var dx = ac.X - apX;
			var dy = ac.Y - apY;

			// Length of vector pc squared.
			return dx * dx + dy * dy;
		}

		public WDist DistanceFromEdge(in WVec v)
		{
			var p = new int2(v.X, v.Y);
			var z = (long)Math.Abs(v.Z);
			if (Points.PolygonContains(p))
				return new WDist((int)Math.Min(z, int.MaxValue));

			var min2 = DistanceSquaredFromLineSegment(p, Points[^1], Points[0], squares[0]);
			for (var i = 1; i < Points.Length; i++)
			{
				var d2 = DistanceSquaredFromLineSegment(p, Points[i - 1], Points[i], squares[i]);
				if (d2 < min2)
					min2 = d2;
			}

			var dist = Exts.ISqrt(min2 + z * z);
			return new WDist((int)Math.Min(dist, int.MaxValue));
		}

		public WDist DistanceFromEdge(WPos pos, WPos origin, WRot orientation)
		{
			orientation += WRot.FromYaw(LocalYaw);

			if (pos.Z > origin.Z + VerticalTopOffset)
				return DistanceFromEdge((pos - (origin + new WVec(0, 0, VerticalTopOffset))).Rotate(-orientation));

			if (pos.Z < origin.Z + VerticalBottomOffset)
				return DistanceFromEdge((pos - (origin + new WVec(0, 0, VerticalBottomOffset))).Rotate(-orientation));

			return DistanceFromEdge((pos - new WPos(origin.X, origin.Y, pos.Z)).Rotate(-orientation));
		}

		IEnumerable<IRenderable> IHitShape.RenderDebugOverlay(HitShape hs, WorldRenderer wr, WPos actorPos, WRot orientation)
		{
			orientation += WRot.FromYaw(LocalYaw);
			var vertsTop = combatOverlayVertsTop.Select(v => actorPos + v.Rotate(orientation)).ToArray();
			var vertsBottom = combatOverlayVertsBottom.Select(v => actorPos + v.Rotate(orientation)).ToArray();

			var shapeColor = hs.IsTraitDisabled ? Color.LightGray : Color.Yellow;

			yield return new PolygonAnnotationRenderable(vertsTop, actorPos, 1, shapeColor);
			yield return new PolygonAnnotationRenderable(vertsBottom, actorPos, 1, shapeColor);
			yield return new CircleAnnotationRenderable(actorPos, OuterRadius, 1, hs.IsTraitDisabled ? Color.Gray : Color.LimeGreen);
		}
	}
}
