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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.HitShapes
{
	public class CapsuleShape : IHitShape
	{
		public WDist OuterRadius { get; private set; }

		[FieldLoader.Require]
		public readonly int2 PointA;

		[FieldLoader.Require]
		public readonly int2 PointB;

		public readonly WDist Radius = new(426);

		[Desc("Defines the top offset relative to the actor's center.")]
		public readonly int VerticalTopOffset = 0;

		[Desc("Defines the bottom offset relative to the actor's center.")]
		public readonly int VerticalBottomOffset = 0;

		int2 ab;
		long abLenSq;

		public CapsuleShape() { }

		public CapsuleShape(int2 a, int2 b, WDist radius)
		{
			PointA = a;
			PointB = b;
			Radius = radius;
		}

		static long LengthSquaredLong(int2 v)
		{
			return (long)v.X * v.X + (long)v.Y * v.Y;
		}

		public void Initialize()
		{
			ab = PointB - PointA;
			abLenSq = LengthSquaredLong(ab) / 1024;

			if (abLenSq == 0)
				throw new YamlException("This Capsule describes a circle. Use a Circle HitShape instead.");

			if (VerticalTopOffset < VerticalBottomOffset)
				throw new YamlException("VerticalTopOffset must be equal to or higher than VerticalBottomOffset.");

			OuterRadius = Radius + new WDist(Math.Max(PointA.Length, PointB.Length));
		}

		// All squared distances and dot products are computed in long:
		// int2.LengthSquared/int2.Dot wrap silently past ~46340 units and a
		// wrapped-negative operand crashes Exts.ISqrt (InvalidOperationException)
		// — the same defect class that killed a 4v4 campaign game in
		// PolygonShape.DistanceFromEdge. Long math preserves every result inside
		// the previously valid int domain bit-for-bit.
		public WDist DistanceFromEdge(in WVec v)
		{
			var p = new int2(v.X, v.Y);

			var t = ((long)(p.X - PointA.X) * ab.X + (long)(p.Y - PointA.Y) * ab.Y) / abLenSq;

			if (t < 0)
				return EndpointDistance(PointA, p);
			if (t > 1024)
				return EndpointDistance(PointB, p);

			var apX = ab.X * t / 1024;
			var apY = ab.Y * t / 1024;
			var dx = apX - ((long)p.X - PointA.X);
			var dy = apY - ((long)p.Y - PointA.Y);

			var distance = Exts.ISqrt(dx * dx + dy * dy);

			return new WDist((int)Math.Min(Math.Max(0L, distance - Radius.Length), int.MaxValue));
		}

		WDist EndpointDistance(int2 endpoint, int2 p)
		{
			var distance = Exts.ISqrt(LengthSquaredLong(endpoint - p));
			return new WDist((int)Math.Min(Math.Max(0L, distance - Radius.Length), int.MaxValue));
		}

		public WDist DistanceFromEdge(WPos pos, WPos origin, WRot orientation)
		{
			if (pos.Z > origin.Z + VerticalTopOffset)
				return DistanceFromEdge((pos - (origin + new WVec(0, 0, VerticalTopOffset))).Rotate(-orientation));

			if (pos.Z < origin.Z + VerticalBottomOffset)
				return DistanceFromEdge((pos - (origin + new WVec(0, 0, VerticalBottomOffset))).Rotate(-orientation));

			return DistanceFromEdge((pos - new WPos(origin.X, origin.Y, pos.Z)).Rotate(-orientation));
		}

		IEnumerable<IRenderable> IHitShape.RenderDebugOverlay(HitShape hs, WorldRenderer wr, WPos origin, WRot orientation)
		{
			var a = origin + new WVec(PointA.X, PointA.Y, VerticalTopOffset).Rotate(orientation);
			var b = origin + new WVec(PointB.X, PointB.Y, VerticalTopOffset).Rotate(orientation);
			var aa = origin + new WVec(PointA.X, PointA.Y, VerticalBottomOffset).Rotate(orientation);
			var bb = origin + new WVec(PointB.X, PointB.Y, VerticalBottomOffset).Rotate(orientation);

			var offset1 = new WVec(a.Y - b.Y, b.X - a.X, 0);
			offset1 = offset1 * Radius.Length / offset1.Length;
			var offset2 = new WVec(aa.Y - bb.Y, bb.X - aa.X, 0);
			offset2 = offset2 * Radius.Length / offset2.Length;

			var shapeColor = hs.IsTraitDisabled ? Color.LightGray : Color.Yellow;

			yield return new CircleAnnotationRenderable(a, Radius, 1, shapeColor);
			yield return new CircleAnnotationRenderable(b, Radius, 1, shapeColor);
			yield return new CircleAnnotationRenderable(aa, Radius, 1, shapeColor);
			yield return new CircleAnnotationRenderable(bb, Radius, 1, shapeColor);
			yield return new CircleAnnotationRenderable(origin, OuterRadius, 1, hs.IsTraitDisabled ? Color.Gray : Color.LimeGreen);
			yield return new LineAnnotationRenderable(a - offset1, b - offset1, 1, shapeColor);
			yield return new LineAnnotationRenderable(a + offset1, b + offset1, 1, shapeColor);
			yield return new LineAnnotationRenderable(aa - offset2, bb - offset2, 1, shapeColor);
			yield return new LineAnnotationRenderable(aa + offset2, bb + offset2, 1, shapeColor);
		}
	}
}
