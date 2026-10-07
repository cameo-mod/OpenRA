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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Each physical turret acquires, aims and fires independently.")]
	public class AttackMultiTurretedInfo : AttackTurretedInfo
	{
		public readonly int ScanInterval = 5;
		public override object Create(ActorInitializer init) => new AttackMultiTurreted(init.Self, this);
		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (ScanInterval < 1)
				throw new YamlException("AttackMultiTurreted ScanInterval must be positive.");
			base.RulesetLoaded(rules, ai);
		}
	}

	public class AttackMultiTurreted : AttackTurreted
	{
		sealed class Station
		{
			public Turreted Turret;
			public Armament[] Arms;
			public Target Target;
			public int ScanTicks;
		}
		Station[] stations = [];
		AutoTarget autoTarget;
		readonly int scanInterval;
		public AttackMultiTurreted(Actor self, AttackMultiTurretedInfo info) : base(self, info)
		{
			scanInterval = info.ScanInterval;
		}
		protected override void Created(Actor self)
		{
			base.Created(self);
			autoTarget = self.TraitOrDefault<AutoTarget>();
			stations = turrets.Select(t => new Station
			{
				Turret = t,
				Arms = Armaments.Where(a => a.Info.Turret == t.Name).ToArray(),
				Target = Target.Invalid,
			}).ToArray();
		}
		[VerifySync]
		public int TurretTargetHash
		{
			get
			{
				unchecked
				{
					var hash = 17;
					foreach (var station in stations)
						hash = (hash * 31 + Sync.HashTarget(station.Target)) * 31 + station.ScanTicks;
					return hash;
				}
			}
		}
		public Target[] TurretTargets => stations.Select(s => s.Target).ToArray();
		protected override void FinishAttackTick(Actor self) { /* Notify after all turret stations finish. */ }
		protected override Target ScanOpportunityTarget(Actor self, AutoTarget scanner) => Target.Invalid;
		public override void DoAttack(Actor self, in Target target) { /* Per-turret tick owns firing. */ }

		static bool Visible(Actor self, Target target) => target.Type != TargetType.Actor || target.Actor.CanBeViewedByPlayer(self.Owner);
		bool Eligible(Actor self, Armament arm, Target target, bool force)
		{
			if (!target.IsValidFor(self) || !Visible(self, target) || arm.IsTraitDisabled || arm.IsTraitPaused ||
				!arm.Weapon.IsValidAgainst(target, self.World, self))
				return false;
			if (!ChooseArmamentsForTarget(target, force).Contains(arm))
				return false;
			return target.IsInRange(self.CenterPosition, arm.MaxRange()) &&
				(arm.Weapon.MinRange == WDist.Zero || !target.IsInRange(self.CenterPosition, arm.Weapon.MinRange));
		}

		protected override void Tick(Actor self)
		{
			base.Tick(self);
			IsAiming = false;
			foreach (var station in stations)
			{
				if (!self.IsInWorld || self.IsDead || self.WillDispose || IsTraitDisabled || IsTraitPaused ||
					positionable is Mobile mobile && !mobile.CanInteractWithGroundLayer(self))
				{
					station.Target = Target.Invalid;
					station.ScanTicks = 0;
					continue;
				}
				var target = RequestedTarget;
				var force = RequestedForceAttack;
				if (!station.Arms.Any(a => Eligible(self, a, target, force)))
				{
					force = false;
					if (autoTarget != null && !autoTarget.IsTraitDisabled && autoTarget.Stance >= UnitStance.Defend && Info.OpportunityFire)
					{
						if (--station.ScanTicks <= 0 || !station.Arms.Any(a => Eligible(self, a, station.Target, false)))
						{
							station.ScanTicks = scanInterval;
							station.Target = autoTarget.ScanForTarget(self, station.Arms,
								t => station.Arms.Any(a => Eligible(self, a, t, false)));
						}
						target = station.Target;
					}
					else
					{
						target = OpportunityTarget;
						force = OpportunityForceAttack;
					}
				}
				station.Target = target;
				var eligible = station.Arms.Where(a => Eligible(self, a, target, force)).ToArray();
				if (eligible.Length == 0)
					continue;
				IsAiming = true;
				if (station.Turret.FaceTarget(self, target))
					foreach (var arm in eligible)
						arm.CheckFire(self, facing, target);
			}
			base.FinishAttackTick(self);
		}
		public override void OnStopOrder(Actor self)
		{
			foreach (var station in stations)
			{
				station.Target = Target.Invalid;
				station.ScanTicks = 0;
			}
			base.OnStopOrder(self);
		}
	}
}
