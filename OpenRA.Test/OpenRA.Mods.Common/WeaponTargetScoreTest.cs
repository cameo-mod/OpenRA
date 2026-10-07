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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	public class WeaponTargetScoreTest
	{
		[Test]
		public void VersusWinsBeforeDistance() => Assert.That(WeaponTargetScore.Prefer(
			new(120, 1), 10000, 20, new(80, 1), 1, 1), Is.True);
		[Test]
		public void EqualVersusPrefersNearest() => Assert.That(WeaponTargetScore.Prefer(
			new(120, 1), 10, 20, new(240, 2), 20, 1), Is.True);
		[Test]
		public void ExactTieUsesActorId() => Assert.That(WeaponTargetScore.Prefer(
			new(120, 1), 10, 1, new(120, 1), 10, 20), Is.True);
		[Test]
		public void FractionalVersusDoesNotTruncate() => Assert.That(new WeaponTargetScore(1201, 10)
			.CompareTo(new WeaponTargetScore(120, 1)), Is.GreaterThan(0));
		[Test]
		public void MissingArmorIsNeutral() => Assert.That(WeaponTargetScore.ArmorMultiplier([])
			.CompareTo(WeaponTargetScore.Neutral), Is.Zero);
		[Test]
		public void ArmorModifiersCompose() => Assert.That(WeaponTargetScore.ArmorMultiplier([120, 80])
			.CompareTo(new WeaponTargetScore(96, 1)), Is.Zero);
		[Test]
		public void LargeProductsDoNotOverflow() => Assert.That(WeaponTargetScore.ArmorMultiplier([int.MaxValue, int.MaxValue])
			.CompareTo(new WeaponTargetScore((BigInteger)int.MaxValue * int.MaxValue, 100)), Is.Zero);
		[Test]
		public void ImmunityIsZero() => Assert.That(WeaponTargetScore.ArmorMultiplier([120, 0])
			.CompareTo(new WeaponTargetScore(0, 1)), Is.Zero);
	}
}
