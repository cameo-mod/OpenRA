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
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SpriteRenderableTest
	{
		[Test]
		public void ScreenBoundsScaleSpriteOffsetAndSize()
		{
			var actual = SpriteRenderable.CalculateScreenBounds(
				new Vector3(100, 100, 0), new Vector3(-10, -5, 0), new Vector3(20, 10, 0), 2f, 0f);

			Assert.That(actual, Is.EqualTo(new Rectangle(80, 90, 40, 20)));
		}

		[Test]
		public void ScreenBoundsApplyScaleBeforeRotation()
		{
			var screenPosition = new Vector3(100, 100, 0);
			var spriteOffset = new Vector3(-10, -5, 0);
			var spriteSize = new Vector3(20, 10, 0);
			const float Scale = 2f;
			const float Rotation = 0.37f;
			var expected = Util.BoundingRectangle(
				screenPosition + Scale * spriteOffset, Scale * spriteSize, Rotation);

			var actual = SpriteRenderable.CalculateScreenBounds(
				screenPosition, spriteOffset, spriteSize, Scale, Rotation);

			Assert.That(actual, Is.EqualTo(expected));
			Assert.That(actual, Is.Not.EqualTo(Util.BoundingRectangle(
				screenPosition + spriteOffset, spriteSize, Rotation)));
		}

		[Test]
		public void ScreenBoundsSupportFractionalScale()
		{
			var actual = SpriteRenderable.CalculateScreenBounds(
				new Vector3(100, 100, 0), new Vector3(-10, -5, 0), new Vector3(20, 10, 0), 0.75f, 0f);

			Assert.That(actual, Is.EqualTo(new Rectangle(92, 96, 15, 7)));
		}
	}
}
