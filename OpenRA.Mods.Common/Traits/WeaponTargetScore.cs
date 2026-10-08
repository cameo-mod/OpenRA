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
using OpenRA.Mods.Common.Warheads;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>Exact weapon effectiveness, independent of raw damage and target identity.</summary>
	public readonly struct WeaponTargetScore : IComparable<WeaponTargetScore>
	{
		public readonly BigInteger Numerator;
		public readonly BigInteger Denominator;
		public WeaponTargetScore(BigInteger numerator, BigInteger denominator)
		{
			if (denominator <= 0)
				throw new ArgumentOutOfRangeException(nameof(denominator));
			Numerator = numerator;
			Denominator = denominator;
		}
		public int CompareTo(WeaponTargetScore other) =>
			(Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
		public static readonly WeaponTargetScore Neutral = new(100, 1);

		// A target with the stronger Versus wins even when it is farther away.
		// Distance and actor id only settle equal effectiveness scores.
		public static bool Prefer(WeaponTargetScore candidate, long distanceSquared, uint actorId,
			WeaponTargetScore current, long currentDistanceSquared, uint currentActorId)
		{
			var comparison = candidate.CompareTo(current);
			return comparison > 0 || comparison == 0 &&
				(distanceSquared < currentDistanceSquared || distanceSquared == currentDistanceSquared && actorId < currentActorId);
		}

		public static WeaponTargetScore ArmorMultiplier(IEnumerable<int> percentages)
		{
			BigInteger numerator = 100, denominator = 1;
			foreach (var percentage in percentages)
			{
				numerator *= percentage;
				denominator *= 100;
			}
			return new WeaponTargetScore(numerator, denominator);
		}

		/// <summary>Damage-weighted Versus for the weapon's valid positive-damage warheads.
		/// The denominator removes raw damage, so this never ranks DPS instead of Versus.
		/// Call only for a visible actor; no frozen actor's live backing state is read.</summary>
		public static WeaponTargetScore Against(Armament armament, Actor source, Actor victim)
		{
			var shape = victim.EnabledTargetablePositions.OfType<HitShape>()
				.MinByOrDefault(h => h.DistanceFromEdge(victim, victim.CenterPosition));
			if (shape == null)
				return Neutral;
			var total = new WeaponTargetScore(0, 1);
			BigInteger weight = 0;
			foreach (var warhead in armament.Weapon.Warheads.OfType<DamageWarhead>())
			{
				if (warhead.Damage <= 0 || !warhead.IsValidAgainst(victim, source))
					continue;
				var multiplier = warhead.TargetingVersus(victim, shape);
				total = new WeaponTargetScore(total.Numerator * multiplier.Denominator +
					multiplier.Numerator * warhead.Damage * total.Denominator,
					total.Denominator * multiplier.Denominator);
				weight += warhead.Damage;
			}
			return weight == 0 ? Neutral : new WeaponTargetScore(total.Numerator, total.Denominator * weight);
		}
	}
}
