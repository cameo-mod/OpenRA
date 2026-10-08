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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public enum UnitStance { HoldFire, ReturnFire, Defend, AttackAnything }

	[RequireExplicitImplementation]
	public interface IActivityNotifyStanceChanged : IActivityInterface
	{
		void StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance);
	}

	[RequireExplicitImplementation]
	public interface INotifyStanceChanged
	{
		void StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance);
	}

	[Desc("The actor will automatically engage the enemy when it is in range.")]
	public class AutoTargetInfo : ConditionalTraitInfo, Requires<AttackBaseInfo>, IEditorActorOptions
	{
		[Desc("It will try to hunt down the enemy if it is set to AttackAnything.")]
		public readonly bool AllowMovement = true;

		[Desc("Rank visible weapon targets by Versus, then distance and ActorID, ignoring class priority weights.")]
		public readonly bool DynamicWeaponPriority = false;

		[Desc("It will try to pivot to face the enemy if stance is not HoldFire.")]
		public readonly bool AllowTurning = true;

		[Desc("It will attack-move if possible when retaliating.")]
		public readonly bool AttackMoveOnRetaliate = true;

		[Desc("Scan for new targets when idle.")]
		public readonly bool ScanOnIdle = true;

		[Desc("Set to a value >1 to override weapons maximum range for this.")]
		public readonly int ScanRadius = -1;

		[Desc("Possible values are HoldFire, ReturnFire, Defend and AttackAnything.",
			"Used for computer-controlled players, both Lua-scripted and regular Skirmish AI alike.")]
		public readonly UnitStance InitialStanceAI = UnitStance.AttackAnything;

		[Desc("Possible values are HoldFire, ReturnFire, Defend and AttackAnything. Used for human players.")]
		public readonly UnitStance InitialStance = UnitStance.Defend;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while in the HoldFire stance.")]
		public readonly string HoldFireCondition = null;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while in the ReturnFire stance.")]
		public readonly string ReturnFireCondition = null;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while in the Defend stance.")]
		public readonly string DefendCondition = null;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while in the AttackAnything stance.")]
		public readonly string AttackAnythingCondition = null;

		[FieldLoader.Ignore]
		public FrozenDictionary<UnitStance, string> ConditionByStance = FrozenDictionary<UnitStance, string>.Empty;

		[Desc("Allow the player to change the unit stance.")]
		public readonly bool EnableStances = true;

		[Desc("Ticks to wait until next AutoTarget: attempt.")]
		public readonly int MinimumScanTimeInterval = 3;

		[Desc("Ticks to wait until next AutoTarget: attempt.")]
		public readonly int MaximumScanTimeInterval = 8;

		[Desc("Display order for the stance dropdown in the map editor")]
		public readonly int EditorStanceDisplayOrder = 1;

		public override object Create(ActorInitializer init) { return new AutoTarget(init, this); }

		public override void RulesetLoaded(Ruleset rules, ActorInfo info)
		{
			base.RulesetLoaded(rules, info);

			var conditionByStance = new Dictionary<UnitStance, string>();
			if (HoldFireCondition != null)
				conditionByStance[UnitStance.HoldFire] = HoldFireCondition;

			if (ReturnFireCondition != null)
				conditionByStance[UnitStance.ReturnFire] = ReturnFireCondition;

			if (DefendCondition != null)
				conditionByStance[UnitStance.Defend] = DefendCondition;

			if (AttackAnythingCondition != null)
				conditionByStance[UnitStance.AttackAnything] = AttackAnythingCondition;

			ConditionByStance = conditionByStance.ToFrozenDictionary();
		}

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			// Indexed by UnitStance
			var stances = new[] { "holdfire", "returnfire", "defend", "attackanything" };

			var labels = new Dictionary<string, string>()
			{
				{ "holdfire", "Hold Fire" },
				{ "returnfire", "Return Fire" },
				{ "defend", "Defend" },
				{ "attackanything", "Attack Anything" },
			};

			yield return new EditorActorDropdown("Stance", EditorStanceDisplayOrder, _ => labels,
				(actor, _) =>
				{
					var init = actor.GetInitOrDefault<StanceInit>(this);
					var botOwned = actor.Owner.Bot != null || !actor.Owner.Playable;
					var stance = init?.Value;
					stance ??= botOwned ? InitialStanceAI : InitialStance;
					return stances[(int)stance];
				},
				(actor, value) => actor.ReplaceInit(new StanceInit(this, (UnitStance)stances.IndexOf(value))));
		}
	}

	public class AutoTarget : ConditionalTrait<AutoTargetInfo>, INotifyIdle, INotifyDamage, ITick, IResolveOrder, ISync, INotifyOwnerChanged
	{
		public readonly IEnumerable<AttackBase> ActiveAttackBases;

		readonly bool allowMovement;
		readonly IMove move;

		[VerifySync]
		int nextScanTime = 0;

		public UnitStance Stance { get; private set; }
		public bool AllowMove => allowMovement && Stance > UnitStance.Defend;

		[VerifySync]
		public Actor Aggressor;

		// NOT SYNCED: do not refer to this anywhere other than UI code
		public UnitStance PredictedStance;
		IOverrideAutoTarget[] overrideAutoTarget;
		INotifyStanceChanged[] notifyStanceChanged;
		IEnumerable<AutoTargetPriorityInfo> activeTargetPriorities;
		int conditionToken = Actor.InvalidConditionToken;

		public void SetStance(Actor self, UnitStance value)
		{
			if (Stance == value)
				return;

			var oldStance = Stance;
			Stance = PredictedStance = value;
			ApplyStanceCondition(self);

			foreach (var nsc in notifyStanceChanged)
				nsc.StanceChanged(self, this, oldStance, Stance);

			if (self.CurrentActivity != null)
				foreach (var a in self.CurrentActivity.ActivitiesImplementing<IActivityNotifyStanceChanged>())
					a.StanceChanged(self, this, oldStance, Stance);
		}

		void ApplyStanceCondition(Actor self)
		{
			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);

			if (Info.ConditionByStance.TryGetValue(Stance, out var condition))
				conditionToken = self.GrantCondition(condition);
		}

		public AutoTarget(ActorInitializer init, AutoTargetInfo info)
			: base(info)
		{
			var self = init.Self;
			ActiveAttackBases = self.TraitsImplementing<AttackBase>().ToArray().Where(t => !t.IsTraitDisabled && t is not IIndependentAutoTarget);

			Stance = init.GetValue<StanceInit, UnitStance>(self.Owner.IsBot || !self.Owner.Playable ? info.InitialStanceAI : info.InitialStance);

			PredictedStance = Stance;

			move = self.TraitOrDefault<IMove>();
			allowMovement = Info.AllowMovement && move != null;
		}

		protected override void Created(Actor self)
		{
			// AutoTargetPriority and their Priorities are fixed - so we can safely cache them with ToArray.
			// IsTraitEnabled can change over time, and so must appear after the ToArray so it gets re-evaluated each time.
			activeTargetPriorities =
				self.TraitsImplementing<AutoTargetPriority>()
					.OrderByDescending(ati => ati.Info.Priority).ToArray()
					.Where(t => !t.IsTraitDisabled).Select(atp => atp.Info);

			overrideAutoTarget = self.TraitsImplementing<IOverrideAutoTarget>().ToArray();
			notifyStanceChanged = self.TraitsImplementing<INotifyStanceChanged>().ToArray();
			ApplyStanceCondition(self);

			base.Created(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			SetStance(self, self.Owner.IsBot || !self.Owner.Playable ? Info.InitialStanceAI : Info.InitialStance);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "SetUnitStance" && Info.EnableStances)
				SetStance(self, (UnitStance)order.ExtraData);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || !self.IsIdle || Stance < UnitStance.ReturnFire)
				return;

			// Don't retaliate against healers
			if (e.Damage.Value < 0)
				return;

			var attacker = e.Attacker;
			if (attacker == null)
				return;

			if (attacker.Disposed)
				return;

			// Don't change targets when there is a target overriding auto-targeting
			foreach (var oat in overrideAutoTarget)
				if (oat.TryGetAutoTargetOverride(self, out _))
					return;

			var owner = self.Owner;
			if (owner == null)
				return;

			if (!attacker.IsInWorld)
			{
				// If the aggressor is in a transport, then attack the transport instead
				var passenger = attacker.TraitOrDefault<Passenger>();
				if (passenger != null && passenger.Transport != null)
					attacker = passenger.Transport;
			}

			// Don't fire at an invisible enemy when we can't move to reveal it
			if (!AllowMove && !attacker.CanBeViewedByPlayer(owner))
				return;

			// Not a lot we can do about things we can't hurt... although maybe we should automatically run away?
			var attackerAsTarget = Target.FromActor(attacker);
			if (!ActiveAttackBases.Any(a => a.HasAnyValidWeapons(attackerAsTarget)))
				return;

			// Don't retaliate against own units force-firing on us. It's usually not what the player wanted.
			if (attacker.AppearsFriendlyTo(self))
				return;

			// Respect AutoAttack priorities.
			if (Stance > UnitStance.ReturnFire)
			{
				var autoTarget = ScanForTarget(self, AllowMove, true);

				if (autoTarget.Type != TargetType.Invalid)
					attacker = autoTarget.Actor;
			}

			Aggressor = attacker;
			if (Info.AttackMoveOnRetaliate && AllowMove && Aggressor != null)
				self.QueueActivity(new AttackMoveActivity(self, () => move.MoveWithinRange(Target.FromCell(self.World, Aggressor.Location), WDist.FromCells(2))));
			else
				Attack(Target.FromActor(Aggressor), AllowMove);
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (IsTraitDisabled || !Info.ScanOnIdle || Stance < UnitStance.Defend)
				return;

			var allowTurn = Info.AllowTurning && Stance > UnitStance.HoldFire;
			ScanAndAttack(self, AllowMove, allowTurn);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			if (nextScanTime > 0)
				--nextScanTime;
		}

		public Target ScanForTarget(Actor self, bool allowMove, bool allowTurn, bool ignoreScanInterval = false)
		{
			if ((ignoreScanInterval || nextScanTime <= 0) && ActiveAttackBases.Any())
			{
				foreach (var oat in overrideAutoTarget)
					if (oat.TryGetAutoTargetOverride(self, out var existingTarget))
						return existingTarget;

				if (!ignoreScanInterval)
					nextScanTime = self.World.SharedRandom.Next(Info.MinimumScanTimeInterval, Info.MaximumScanTimeInterval);

				foreach (var ab in ActiveAttackBases)
				{
					// If we can't attack right now, there's no need to try and find a target.
					var attackStances = ab.UnforcedAttackTargetStances();
					if (attackStances != PlayerRelationship.None)
					{
						var range = Info.ScanRadius > 0 ? WDist.FromCells(Info.ScanRadius) : ab.GetMaximumRange();
						var target = ChooseTarget(self, ab, attackStances, range, allowMove, allowTurn);
						if (target.Type != TargetType.Invalid)
							return target;
					}
				}
			}

			return Target.Invalid;
		}

		/// <summary>Read-only station scan using this occupant's priorities and selected arms.
		/// The station owns its synchronized cadence; this does not touch nextScanTime or RNG.</summary>
		public Target ScanForTarget(Actor origin, IReadOnlyList<Armament> stationArmaments,
			Func<Target, bool> canFire, bool targetFrozenActors = false, WDist extraRange = default, Func<Armament, Target, bool> armFilter = null)
		{
			if (IsTraitDisabled || Stance < UnitStance.Defend || stationArmaments.Count == 0)
				return Target.Invalid;
			var stances = PlayerRelationship.None;
			var range = WDist.Zero;
			foreach (var armament in stationArmaments)
			{
				if (armament.IsTraitDisabled || armament.IsTraitPaused)
					continue;
				stances |= armament.Info.TargetRelationships;
				if (range < armament.MaxRange())
					range = armament.MaxRange();
			}
			return stances == PlayerRelationship.None ? Target.Invalid
				: ChooseTarget(origin, null, stances, range + extraRange, false, false, stationArmaments, canFire, targetFrozenActors, armFilter);
		}

		/// <summary>Visible, currently reachable weapon targets, without overrides or RNG.</summary>
		public Target ScanForInRangeTarget(Actor self, AttackBase attack)
		{
			if (IsTraitDisabled || Stance < UnitStance.Defend || attack.IsTraitDisabled || attack.IsTraitPaused)
				return Target.Invalid;
			var stances = attack.UnforcedAttackTargetStances();
			return stances == PlayerRelationship.None ? Target.Invalid : ChooseTarget(self, attack, stances,
				Info.ScanRadius > 0 ? WDist.FromCells(Info.ScanRadius) : attack.GetMaximumRange(), false, true);
		}

		public void ScanAndAttack(Actor self, bool allowMove, bool allowTurn)
		{
			var target = ScanForTarget(self, allowMove, allowTurn);
			if (target.Type != TargetType.Invalid)
				Attack(target, allowMove);
		}

		void Attack(in Target target, bool allowMove)
		{
			foreach (var ab in ActiveAttackBases)
				ab.AttackTarget(target, AttackSource.AutoTarget, false, allowMove);
		}

		public bool HasValidTargetPriority(Actor self, Player owner, BitSet<TargetableType> targetTypes)
		{
			if (owner == null || Stance <= UnitStance.ReturnFire)
				return false;

			return activeTargetPriorities.Any(ati =>
			{
				// Incompatible relationship
				if (!ati.ValidRelationships.HasRelationship(self.Owner.RelationshipWith(owner)))
					return false;

				// Incompatible target types
				if (!ati.ValidTargets.Overlaps(targetTypes) || ati.InvalidTargets.Overlaps(targetTypes))
					return false;

				return true;
			});
		}

		Target ChooseTarget(Actor self, AttackBase ab, PlayerRelationship attackStances, WDist scanRange, bool allowMove, bool allowTurn,
			IReadOnlyList<Armament> stationArmaments = null, Func<Target, bool> canFire = null, bool targetFrozenActors = false, Func<Armament, Target, bool> armFilter = null)
		{
			var chosenTarget = Target.Invalid;
			var chosenTargetPriority = int.MinValue;
			var chosenTargetRange = 0;
			var chosenWeaponScore = WeaponTargetScore.Neutral;
			var chosenDistanceSquared = long.MaxValue;
			var chosenActorId = uint.MaxValue;
			var chosenInRange = false;

			var activePriorities = activeTargetPriorities.ToList();
			if (activePriorities.Count == 0 || self.Owner == null)
				return Target.Invalid;

			var targetsInRange = self.World.FindActorsInCircle(self.CenterPosition, scanRange)
				.Select(Target.FromActor);

			if (!Info.DynamicWeaponPriority && (allowMove || targetFrozenActors || ab?.Info.TargetFrozenActors == true))
				targetsInRange = targetsInRange
					.Concat(self.Owner.FrozenActorLayer.FrozenActorsInCircle(self.World, self.CenterPosition, scanRange)
					.Select(Target.FromFrozenActor));

			// PERF: Avoid allocating a new list for each target.
			List<AutoTargetPriorityInfo> validPriorities = [];

			foreach (var target in targetsInRange)
			{
				BitSet<TargetableType> targetTypes;
				Player owner;
				if (target.Type == TargetType.Actor)
				{
					// PERF: Most units can only attack enemy units. If this is the case but the target is not an enemy, we
					// can bail early and avoid the more expensive targeting checks and armament selection. For groups of
					// allied units, this helps significantly reduce the cost of auto target scans. This is important as
					// these groups will continuously rescan their allies until an enemy finally comes into range.
					if (attackStances == PlayerRelationship.Enemy && !target.Actor.AppearsHostileTo(self))
						continue;

					// Check whether we can auto-target this actor
					targetTypes = target.Actor.GetEnabledTargetTypes();
					if (target.Actor.Owner == null)
						continue;
					if (PreventsAutoTarget(self, target.Actor) || !target.Actor.CanBeViewedByPlayer(self.Owner))
						continue;

					owner = target.Actor.Owner;
				}
				else if (target.Type == TargetType.FrozenActor)
				{
					// Dynamic effectiveness reads only currently visible armor, never frozen live backing state.
					if (Info.DynamicWeaponPriority)
						continue;
					if (attackStances == PlayerRelationship.Enemy && self.Owner.RelationshipWith(target.FrozenActor.Owner) == PlayerRelationship.Ally)
						continue;

					// Bot-controlled units aren't yet capable of understanding visibility changes
					// Prevent that bot-controlled units endlessly fire at frozen actors.
					// TODO: Teach the AI to support long range artillery units with units that provide line of sight
					if (self.Owner.IsBot && target.FrozenActor.Actor == null)
						continue;

					targetTypes = target.FrozenActor.TargetTypes;
					owner = target.FrozenActor.Owner;
				}
				else
					continue;

				validPriorities.Clear();
				foreach (var ati in activePriorities)
				{
					// Already have a higher priority target
					if (!Info.DynamicWeaponPriority && ati.Priority < chosenTargetPriority)
						continue;

					// Incompatible relationship
					if (!ati.ValidRelationships.HasRelationship(self.Owner.RelationshipWith(owner)))
						continue;

					// Incompatible target types
					if (!ati.ValidTargets.Overlaps(targetTypes) || ati.InvalidTargets.Overlaps(targetTypes))
						continue;

					validPriorities.Add(ati);
				}

				if (validPriorities.Count == 0)
					continue;

				// Make sure that we can actually fire on the actor
				var armaments = stationArmaments ?? ab.ChooseArmamentsForTarget(target, false);
				if (!allowMove && stationArmaments == null)
				{
					// PERF: This lambda captures, contain it within a local function to prevent
					// the compiler allocating the helper class at the top of the loop.
					static Func<Armament, bool> IsInRange(Actor self, Target target) =>
						arm =>
							target.IsInRange(self.CenterPosition, arm.MaxRange()) &&
							!target.IsInRange(self.CenterPosition, arm.Weapon.MinRange);

					armaments = armaments.Where(IsInRange(self, target));
				}

				if (!armaments.Any())
					continue;

				if (canFire != null ? !canFire(target) : !allowTurn && !ab.TargetInFiringArc(self, target, ab.Info.FacingTolerance))
					continue;

				if (Info.DynamicWeaponPriority)
				{
					var bestScore = new WeaponTargetScore(0, 1);
					var found = false;
					var inRange = false;
					foreach (var arm in armaments)
					{
						if (arm.IsTraitDisabled || arm.IsTraitPaused || armFilter != null && !armFilter(arm, target) ||
							!arm.Info.TargetRelationships.HasRelationship(self.Owner.RelationshipWith(owner)) ||
							!arm.Weapon.IsValidAgainst(target, self.World, arm.Actor))
							continue;
						var readyRange = target.IsInRange(self.CenterPosition, arm.MaxRange()) &&
							(arm.Weapon.MinRange == WDist.Zero || !target.IsInRange(self.CenterPosition, arm.Weapon.MinRange));
						if (!allowMove && stationArmaments == null && !readyRange)
							continue;
						// Shoot a currently reachable ranged target before approaching a demolition target.
						if (inRange && !readyRange)
							continue;
						var score = WeaponTargetScore.Against(arm, arm.Actor, target.Actor);
						if (!found || readyRange && !inRange || score.CompareTo(bestScore) > 0)
							bestScore = score;
						found = true;
						inRange |= readyRange;
					}
					if (!found)
						continue;
					var delta = target.CenterPosition - self.CenterPosition;
					var distanceSquared = (long)delta.X * delta.X + (long)delta.Y * delta.Y + (long)delta.Z * delta.Z;
					var id = target.Actor.ActorID;
					if (chosenTarget.Type == TargetType.Invalid || inRange && !chosenInRange ||
						inRange == chosenInRange && WeaponTargetScore.Prefer(bestScore, distanceSquared, id,
							chosenWeaponScore, chosenDistanceSquared, chosenActorId))
					{
						chosenTarget = target;
						chosenWeaponScore = bestScore;
						chosenDistanceSquared = distanceSquared;
						chosenActorId = id;
						chosenInRange = inRange;
					}
					continue;
				}

				// Evaluate whether we want to target this actor
				var targetRange = (target.CenterPosition - self.CenterPosition).Length;
				foreach (var ati in validPriorities)
				{
					if (chosenTarget.Type == TargetType.Invalid || chosenTargetPriority < ati.Priority
						|| (chosenTargetPriority == ati.Priority && targetRange < chosenTargetRange))
					{
						chosenTarget = target;
						chosenTargetPriority = ati.Priority;
						chosenTargetRange = targetRange;
					}
				}
			}

			return chosenTarget;
		}

		static bool PreventsAutoTarget(Actor attacker, Actor target)
		{
			foreach (var deat in target.TraitsImplementing<IDisableEnemyAutoTarget>())
				if (deat.DisableEnemyAutoTarget(target, attacker))
					return true;

			return false;
		}
	}

	public class StanceInit : ValueActorInit<UnitStance>, ISingleInstanceInit
	{
		public StanceInit(TraitInfo info, UnitStance value)
			: base(info, value) { }
	}
}



