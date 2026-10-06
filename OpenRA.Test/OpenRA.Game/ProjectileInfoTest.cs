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

using NUnit.Framework;
using OpenRA.GameRules;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class ProjectileInfoTest
	{
		[TestCase(TestName = "RangeLimitPercent defaults to weapon range, scales range, and preserves unlimited exceptions")]
		public void RangeLimitPercentUsesExpectedPrecedence()
		{
			var weaponRange = new WDist(10000);
			Assert.That(ProjectileInfoUtils.EffectiveRangeLimit(WDist.Zero, weaponRange, 0), Is.EqualTo(weaponRange));
			Assert.That(ProjectileInfoUtils.EffectiveRangeLimit(WDist.Zero, weaponRange, 150), Is.EqualTo(new WDist(15000)));
			Assert.That(ProjectileInfoUtils.EffectiveRangeLimit(WDist.Zero, weaponRange, -1), Is.EqualTo(new WDist(-1)));
			Assert.That(ProjectileInfoUtils.EffectiveRangeLimit(new WDist(1200), weaponRange, 150), Is.EqualTo(new WDist(1200)));
		}

		[TestCase(TestName = "CloseEnoughFromSpeed follows current speed deterministically, including acceleration")]
		public void CloseEnoughFromSpeedUsesCurrentSpeed()
		{
			var fixedRadius = new WDist(298);
			Assert.That(ProjectileInfoUtils.CloseEnoughRadius(false, 512, fixedRadius), Is.EqualTo(298));
			Assert.That(ProjectileInfoUtils.CloseEnoughRadius(true, 256, fixedRadius), Is.EqualTo(256));
			Assert.That(ProjectileInfoUtils.CloseEnoughRadius(true, 256 + 64, fixedRadius), Is.EqualTo(320));
			Assert.That(ProjectileInfoUtils.CloseEnoughRadius(true, 256 + 64, fixedRadius), Is.EqualTo(320));
		}

		[TestCase(TestName = "CloseEnoughFromSpeed shares the same radius for detonation and airburst")]
		public void CloseEnoughFromSpeedRadiusCoversAirburst()
		{
			var radius = ProjectileInfoUtils.CloseEnoughRadius(true, 400, new WDist(298));
			var nearHorizontalDistance = new WVec(399, 0, 0).HorizontalLength;
			var farHorizontalDistance = new WVec(401, 0, 0).HorizontalLength;
			Assert.That(nearHorizontalDistance < radius, Is.True);
			Assert.That(farHorizontalDistance < radius, Is.False);
		}
	}
}
