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
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Graphics
{
	// Taper geometry for a moving projectile streak, shared by Bullet and by mod projectiles that draw fake bullets.
	// Tail/head taper keys and the BeamRenderable endpoint variation were written by Blackrobe (engine PR #101).
	// Render only: nothing here touches simulation state.
	public readonly struct ProjectileStreakStyle
	{
		public readonly WDist Width;
		public readonly WDist TailWidth;
		public readonly WDist HeadWidth;
		public readonly int HeadTaperLength;
		public readonly Color Color;
		public readonly Color TailColor;
		public readonly int ZOffset;

		// Defaults: tail width, head width = width; tail color = color. That draws one flat quad, as before the taper keys existed.
		public ProjectileStreakStyle(WDist width, WDist? tailWidth, WDist? headWidth, WDist headTaperLength,
			Color color, Color? tailColor, int tailColorAlpha, int zOffset)
		{
			Width = width;
			TailWidth = tailWidth ?? width;
			HeadWidth = headWidth ?? width;
			HeadTaperLength = Math.Max(headTaperLength.Length, 0);
			Color = color;
			TailColor = tailColor.HasValue
				? Color.FromArgb(Math.Clamp(tailColorAlpha, 0, 255), tailColor.Value)
				: color;
			ZOffset = zOffset;
		}
	}

	// One flat quad of the streak: from Start along Vector, widths and colors interpolate start to end.
	public readonly struct ProjectileStreakSegment
	{
		public readonly WPos Start;
		public readonly WVec Vector;
		public readonly WDist StartWidth;
		public readonly WDist EndWidth;
		public readonly Color StartColor;
		public readonly Color EndColor;

		public ProjectileStreakSegment(WPos start, in WVec vector, WDist startWidth, WDist endWidth, Color startColor, Color endColor)
		{
			Start = start;
			Vector = vector;
			StartWidth = startWidth;
			EndWidth = endWidth;
			StartColor = startColor;
			EndColor = endColor;
		}
	}

	public static class ProjectileStreak
	{
		// Pure geometry, no renderer needed. visibleLength is the streak length in WDist units (head - tail).
		public static IReadOnlyList<ProjectileStreakSegment> Build(in ProjectileStreakStyle style, WPos head, WPos tail, int visibleLength)
		{
			if (visibleLength <= 0)
				return [];

			var taper = Math.Min(style.HeadTaperLength, visibleLength);
			if (taper == 0)
				return [new ProjectileStreakSegment(tail, head - tail, style.TailWidth, style.Width, style.TailColor, style.Color)];

			var bodyLength = visibleLength - taper;
			var taperStart = tail + Scale(head - tail, bodyLength, visibleLength);
			var segments = new List<ProjectileStreakSegment>(2);
			if (bodyLength > 0)
				segments.Add(new ProjectileStreakSegment(tail, taperStart - tail, style.TailWidth, style.Width, style.TailColor, style.Color));

			segments.Add(new ProjectileStreakSegment(taperStart, head - taperStart, style.Width, style.HeadWidth, style.Color, style.Color));
			return segments;
		}

		// v * num / den per component in long, so a long streak over a big map cannot overflow the int WVec multiplication.
		// den must be positive. The result is truncated toward zero, like the int form it replaces.
		public static WVec Scale(WVec v, long num, long den)
		{
			if (den <= 0)
				throw new ArgumentOutOfRangeException(nameof(den), den, "Denominator must be positive.");

			return new WVec((int)(v.X * num / den), (int)(v.Y * num / den), (int)(v.Z * num / den));
		}

		// Streak length with a bounded random offset. roll is any stable value (the caller derives it from state that does not
		// change between frames); it only picks the offset, so this is deterministic and consumes no random generator.
		// Long math: a variation up to int.MaxValue and a length up to int.MaxValue neither overflow nor leave [1, int.MaxValue].
		public static int VariedLength(int length, int variation, ulong roll)
		{
			var v = Math.Max((long)variation, 0L);
			var offset = v == 0 ? 0L : (long)(roll % (ulong)(2 * v + 1)) - v;
			return (int)Math.Clamp(length + offset, 1L, int.MaxValue);
		}

		public static IEnumerable<IRenderable> Render(ProjectileStreakStyle style, WPos head, WPos tail, int visibleLength)
		{
			foreach (var s in Build(style, head, tail, visibleLength))
				yield return new BeamRenderable(s.Start, style.ZOffset, s.Vector, BeamRenderableShape.Flat,
					s.StartWidth, s.EndWidth, s.StartColor, s.EndColor, 0f);
		}
	}
}
