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

using System.Linq;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class WithIdleOverlayFollowBodyOffsetTest
	{
		[Test]
		public void WaitsForOptionalSpriteBodyConstruction()
		{
			Assert.That(typeof(WithIdleOverlayInfo).GetInterfaces(), Does.Contain(typeof(NotBefore<WithSpriteBodyInfo>)));
		}

		[Test]
		public void ConvertsScaledSpritePixelsToEquivalentWorldOffset()
		{
			var spriteOffset = 1.5f * new Vector3(8, -32, 0);
			var method = typeof(WithIdleOverlayInfo).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
				.Single(candidate => candidate.Name == "SpriteOffsetToWorld" && candidate.GetParameters()[0].ParameterType == typeof(Size));
			var worldOffset = (WVec)method.Invoke(null, [new Size(48, 24), 1024, spriteOffset]);

			Assert.That(worldOffset, Is.EqualTo(new WVec(256, -2048, 0)));
			Assert.That(48f * worldOffset.X / 1024, Is.EqualTo(spriteOffset.X).Within(0.01f));
			Assert.That(24f * worldOffset.Y / 1024, Is.EqualTo(spriteOffset.Y).Within(0.01f));
		}
	}
}
