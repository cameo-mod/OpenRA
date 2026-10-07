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
using OpenRA.Activities;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public sealed class FirePort
	{
		public WVec Offset;
		public WAngle Yaw;
		public WAngle Cone;
	}
	/// <summary>Independent exclusive fire stations for cargo and garrison occupants.</summary>
	public class AttackGarrisonedInfo : AttackFollowInfo
	{
		[FieldLoader.Require]
		public readonly WVec[] PortOffsets = null;
		public readonly WAngle[] PortYaws = null;
		public readonly WAngle[] PortCones = null;
		[PaletteReference]
		public readonly string MuzzlePalette = "effect";
		[Desc("Fixed synchronized interval between passenger opportunity scans. No random port or scan selection.")]
		public readonly int ScanInterval = 5;
		[Desc("Explicit acknowledgement that occupants exceeding available ports cannot fire.")]
		public readonly bool NoFireOverflow = false;
		public IReadOnlyList<FirePort> Ports { get; private set; }

		public override object Create(ActorInitializer init) => new AttackGarrisoned(init.Self, this);
		public override void RulesetLoaded(Ruleset rules, ActorInfo actor)
		{
			if (PortOffsets == null || PortOffsets.Length == 0)
				throw new YamlException($"{actor.Name}: AttackGarrisoned needs at least one port.");
			if (PortYaws != null && PortYaws.Length != PortOffsets.Length)
				throw new YamlException($"{actor.Name}: PortYaws length must match PortOffsets.");
			if (PortCones != null && PortCones.Length != PortOffsets.Length)
				throw new YamlException($"{actor.Name}: PortCones length must match PortOffsets.");
			if (ScanInterval < 1)
				throw new YamlException($"{actor.Name}: ScanInterval must be positive.");
			Ports = PortOffsets.Select((offset, i) => new FirePort
			{
				Offset = offset,
				Yaw = PortYaws?[i] ?? WAngle.Zero,
				Cone = PortCones?[i] ?? new WAngle(512),
			}).ToArray();
			base.RulesetLoaded(rules, actor);
		}
	}

	/// <summary>One stable port binding; synchronized through the containing trait's station hash.</summary>
	public sealed class FirePortStation
	{
		public readonly int PortIndex;
		public Actor Occupant { get; internal set; }
		internal Armament[] Arms = [];
		internal PassengerWeaponStation[] WeaponStations = [];
		public IReadOnlyList<Target> WeaponTargets => WeaponStations.Select(s => s.Target).ToArray();
		internal AutoTarget AutoTarget;
		internal IFacing Facing;
		internal IPositionable Position;
		internal RenderSprites Render;
		public Target RequestedTarget { get; internal set; }
		public Target OpportunityTarget { get; internal set; }
		internal bool RequestedForce, OpportunityForce, Persistent, Retaliating;
		internal int ScanTicks;
		internal readonly List<AnimationWithOffset> Muzzles = [];
		public FirePortStation(int index) { PortIndex = index; }
	}

	internal sealed class PassengerWeaponStation
	{
		internal Armament[] Arms;
		internal Turreted Turret;
		internal Target Target = Target.Invalid;
		internal int ScanTicks;
	}

	/// <summary>Uses AttackBase orders but never AttackFollow's shared opportunity target.</summary>
	public class AttackGarrisoned : AttackBase, IIndependentAutoTarget, IFirePortAttack,
		INotifyPassengerEntered, INotifyPassengerExited, INotifyFirePortOccupantEntered, INotifyFirePortOccupantExited,
		INotifyOwnerChanged, INotifyStanceChanged, INotifyActorDisposing, INotifyDamage, IRender
	{
		public new readonly AttackGarrisonedInfo Info;
		readonly Actor host;
		readonly FirePortStation[] stations;
		INotifyAttack[] notifyAttacks;
		BodyOrientation coords;
		PassengerFirePortAttackActivity activeActivity;
		public IReadOnlyList<FirePortStation> Stations => stations;

		public AttackGarrisoned(Actor self, AttackGarrisonedInfo info) : base(self, info)
		{
			host = self;
			Info = info;
			stations = Enumerable.Range(0, info.PortOffsets.Length).Select(i => new FirePortStation(i)).ToArray();
		}

		[VerifySync]
		public int StationHash
		{
			get
			{
				unchecked
				{
					var hash = 17;
					if (activeActivity != null)
					{
						hash = hash * 31 + Sync.HashTarget(activeActivity.CurrentTarget);
						hash = hash * 31 + (activeActivity.ForceAttack ? 1 : 0);
					}
					foreach (var port in stations)
					{
						hash = hash * 31 + port.PortIndex;
						hash = hash * 31 + (int)(port.Occupant?.ActorID ?? 0);
						hash = hash * 31 + Sync.HashTarget(port.RequestedTarget);
						hash = hash * 31 + (int)port.RequestedTarget.Type;
						hash = hash * 31 + (int)(port.RequestedTarget.FrozenActor?.ID ?? 0);
						hash = hash * 31 + Sync.HashTarget(port.OpportunityTarget);
						hash = hash * 31 + (int)port.OpportunityTarget.Type;
						hash = hash * 31 + (int)(port.OpportunityTarget.FrozenActor?.ID ?? 0);
						hash = hash * 31 + port.ScanTicks;
						foreach (var weaponStation in port.WeaponStations)
							hash = (hash * 31 + Sync.HashTarget(weaponStation.Target)) * 31 + weaponStation.ScanTicks;
						hash = hash * 31 + (port.RequestedForce ? 1 : 0) + (port.OpportunityForce ? 2 : 0) + (port.Persistent ? 4 : 0) + (port.Retaliating ? 8 : 0);
					}
					return hash;
				}
			}
		}

		protected override void Created(Actor self)
		{
			notifyAttacks = self.TraitsImplementing<INotifyAttack>().ToArray();
			coords = self.TraitOrDefault<BodyOrientation>();
			base.Created(self);
		}
		protected override Func<IEnumerable<Armament>> InitializeGetArmaments(Actor self) =>
			() => stations.Where(s => s.Occupant != null).SelectMany(s => s.Arms);

		void Enter(Actor actor)
		{
			if (notifyAttacks == null || actor == null || actor.IsDead || stations.Any(p => p.Occupant == actor))
				return;
			var station = stations.FirstOrDefault(p => p.Occupant == null);
			if (station == null)
				return;
			station.Occupant = actor;
			station.Arms = actor.TraitsImplementing<Armament>().Where(a => Info.Armaments.Contains(a.Info.Name)).ToArray();
			station.WeaponStations = station.Arms.GroupBy(a => a.Turret)
				.Select(g => new PassengerWeaponStation { Turret = g.Key, Arms = g.ToArray() }).ToArray();
			station.AutoTarget = actor.TraitOrDefault<AutoTarget>();
			station.Facing = actor.TraitOrDefault<IFacing>();
			station.Position = actor.TraitOrDefault<IPositionable>();
			station.Render = actor.TraitOrDefault<RenderSprites>();
			foreach (var arm in station.Arms)
				arm.AddNotifyAttacks(host, notifyAttacks);
			if (activeActivity != null)
			{
				station.RequestedTarget = activeActivity.CurrentTarget;
				station.RequestedForce = activeActivity.ForceAttack;
			}
		}

		void Exit(FirePortStation station)
		{
			foreach (var arm in station.Arms)
				arm.RemoveNotifyAttacks(notifyAttacks);
			station.Occupant = null;
			station.Arms = [];
			station.WeaponStations = [];
			station.AutoTarget = null;
			station.Facing = null;
			station.Position = null;
			station.Render = null;
			station.Muzzles.Clear();
			Clear(station);
		}
		void Exit(Actor actor)
		{
			foreach (var station in stations)
				if (station.Occupant == actor)
					Exit(station);
		}
		static void Clear(FirePortStation station)
		{
			station.RequestedTarget = station.OpportunityTarget = Target.Invalid;
			station.RequestedForce = station.OpportunityForce = station.Persistent = station.Retaliating = false;
			station.ScanTicks = 0;
			foreach (var weaponStation in station.WeaponStations)
			{
				weaponStation.Target = Target.Invalid;
				weaponStation.ScanTicks = 0;
			}
		}
		void Reconcile()
		{
			var occupants = host.TraitsImplementing<Cargo>().SelectMany(c => c.Passengers)
				.Concat(host.TraitsImplementing<IFirePortOccupantProvider>().SelectMany(p => p.Occupants)).Where(a => !a.IsDead).Distinct().ToArray();
			var changed = false;
			foreach (var station in stations)
				if (station.Occupant != null && !occupants.Contains(station.Occupant))
				{
					Exit(station);
					changed = true;
				}
			// Reconcile entry/load and exits. Existing bindings never move. Overflow gets
			// a station only when a free port appears, not by modulo sharing.
			if (changed || stations.Any(s => s.Occupant == null))
				foreach (var actor in occupants.OrderBy(a => a.ActorID))
					Enter(actor);
		}
		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor actor) => Enter(actor);
		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor actor) => Exit(actor);
		void INotifyFirePortOccupantEntered.OnFirePortOccupantEntered(Actor self, Actor actor) => Enter(actor);
		void INotifyFirePortOccupantExited.OnFirePortOccupantExited(Actor self, Actor actor) => Exit(actor);

		WVec Offset(FirePortStation station)
		{
			var orientation = coords?.QuantizeOrientation(host.Orientation) ?? host.Orientation;
			var offset = Info.PortOffsets[station.PortIndex].Rotate(orientation);
			return coords?.LocalToWorld(offset) ?? offset;
		}
		bool InCone(FirePortStation station, in Target target)
		{
			if (Info.PortCones == null)
				return true;
			var yaw = (target.CenterPosition - (host.CenterPosition + Offset(station))).Yaw;
			var portYaw = (facing?.Facing ?? host.Orientation.Yaw) + (Info.PortYaws?[station.PortIndex] ?? WAngle.Zero);
			var delta = Math.Min((yaw - portYaw).Angle, (portYaw - yaw).Angle);
			return delta <= Info.PortCones[station.PortIndex].Angle;
		}
		bool Valid(in Target target) => (target.Type != TargetType.Actor || target.Actor.CanBeViewedByPlayer(host.Owner))
			&& (target.Type != TargetType.FrozenActor || Info.TargetFrozenActors) && target.IsValidFor(host);
		internal Target RefreshTarget(Target target)
		{
			// Frozen targets are remembered snapshots. Do not replace them with a
			// hidden live actor (the generic bot Recalculate helper does so).
			if (target.Type != TargetType.FrozenActor)
				target = target.Recalculate(host.Owner, out _);
			return Valid(target) ? target : Target.Invalid;
		}
		IEnumerable<Armament> Eligible(FirePortStation station, Target target, bool force, bool checkRange, bool checkCone = true, bool includePaused = false)
		{
			if (!host.IsInWorld || host.IsDead || host.WillDispose || station.Occupant == null || station.Occupant.IsDead
				|| !Valid(target) || checkCone && !InCone(station, target))
				yield break;
			if (!force && (target.RequiresForceFire || target.Type == TargetType.Terrain && !Info.TargetTerrainWithoutForceFire))
				yield break;
			var owner = target.Type == TargetType.Actor ? target.Actor.Owner : target.FrozenActor?.Owner;
			var position = host.CenterPosition + Offset(station);
			foreach (var arm in station.Arms)
				if (!arm.IsTraitDisabled && (includePaused || !arm.IsTraitPaused) && arm.Weapon.IsValidAgainst(target, host.World, host)
					&& (owner == null || (force ? arm.Info.ForceTargetRelationships : arm.Info.TargetRelationships).HasRelationship(host.Owner.RelationshipWith(owner)))
					&& (!checkRange || target.IsInRange(position, arm.MaxRange())
						&& (arm.Weapon.MinRange == WDist.Zero || !target.IsInRange(position, arm.Weapon.MinRange))))
					yield return arm;
		}
		public bool CanFireFromPort(int port, in Target target) => !IsTraitDisabled && !IsTraitPaused
			&& port >= 0 && port < stations.Length && Eligible(stations[port], target, false, true).Any();
		public bool CanFireFromAnyPort(in Target target)
		{
			for (var i = 0; i < stations.Length; i++)
				if (CanFireFromPort(i, target))
					return true;
			return false;
		}
		internal bool CanFireFromAnyPort(Target target, bool force) => !IsTraitDisabled && !IsTraitPaused
			&& stations.Any(s => Eligible(s, target, force, true).Any());
		internal IEnumerable<Armament> MovementArmaments(Target target, bool force) =>
			stations.SelectMany(s => Eligible(s, target, force, false, false));
		internal bool WaitingForResupply(Target target, bool force) => !Info.AbortOnResupply
			&& stations.Any(s => Eligible(s, target, force, false, false, true).Any(a => a.IsTraitPaused));
		internal WDist MovementMaximumRange(Target target, bool force) => stations.SelectMany(s =>
			Eligible(s, target, force, false, false).Select(a => new WDist(Math.Max(0, a.MaxRange().Length - Offset(s).Length))))
			.DefaultIfEmpty(WDist.Zero).Max();
		internal WAngle? TurnTowardPort(Target target, bool force)
		{
			foreach (var station in stations)
				if (Eligible(station, target, force, true, false).Any() && !InCone(station, target))
					return (target.CenterPosition - (host.CenterPosition + Offset(station))).Yaw
						- (Info.PortYaws?[station.PortIndex] ?? WAngle.Zero);
			return null;
		}
		public IEnumerable<Armament> ArmamentsAgainst(in Target target)
		{
			var t = target;
			return IsTraitDisabled || IsTraitPaused ? [] : stations.SelectMany(s => Eligible(s, t, false, true));
		}
		Target Scan(FirePortStation station)
		{
			var hostTarget = host.TraitOrDefault<AutoTarget>();
			if (!host.IsInWorld || host.IsDead || host.WillDispose || IsTraitDisabled || IsTraitPaused
				|| hostTarget?.IsTraitDisabled == true || hostTarget?.Stance < UnitStance.Defend)
				return Target.Invalid;
			return station.AutoTarget?.ScanForTarget(host, station.Arms,
				t => Eligible(station, t, false, true).Any(), Info.TargetFrozenActors, new WDist(Offset(station).Length),
				(a, t) => Eligible(station, t, false, true).Contains(a)) ?? Target.Invalid;
		}
		bool MayRetainOpportunity(FirePortStation station, Target target)
		{
			if (station.Persistent)
				return true;
			var hostTarget = host.TraitOrDefault<AutoTarget>();
			if (!Info.OpportunityFire || station.AutoTarget == null || station.AutoTarget.IsTraitDisabled
				|| hostTarget?.IsTraitDisabled == true || hostTarget?.Stance < UnitStance.ReturnFire
				|| station.AutoTarget.Stance < UnitStance.ReturnFire)
				return false;
			if (station.Retaliating)
				return true;
			if (hostTarget?.Stance < UnitStance.Defend || !Valid(target))
				return false;
			return target.Type == TargetType.Actor
				? station.AutoTarget.HasValidTargetPriority(host, target.Actor.Owner, target.Actor.GetEnabledTargetTypes())
				: target.Type == TargetType.FrozenActor && station.AutoTarget.HasValidTargetPriority(host, target.FrozenActor.Owner, target.FrozenActor.TargetTypes);
		}
		public IReadOnlyList<Target> ForecastTargets() => stations.Select(s => s.Occupant == null ? Target.Invalid : Scan(s)).ToArray();
		public override IEnumerable<Armament> ChooseArmamentsForTarget(Target target, bool forceAttack) =>
			stations.SelectMany(s => Eligible(s, target, forceAttack, false));
		public override bool HasAnyValidWeapons(in Target target, bool checkForCenterTargetingWeapons = false, bool reloadingIsInvalid = false) =>
			ChooseArmamentsForTarget(target, false).Any(a => (!checkForCenterTargetingWeapons || a.Weapon.TargetActorCenter)
				&& (!reloadingIsInvalid || !a.IsReloading));
		public override WDist GetMaximumRangeVersusTarget(in Target target) =>
			ChooseArmamentsForTarget(target, true).Select(a => a.MaxRange()).DefaultIfEmpty(WDist.Zero).Max();
		public override WDist GetMinimumRangeVersusTarget(in Target target) =>
			ChooseArmamentsForTarget(target, true).Select(a => a.Weapon.MinRange).DefaultIfEmpty(WDist.Zero).Min();

		internal void Request(PassengerFirePortAttackActivity activity, Target target, bool force)
		{
			activeActivity = activity;
			foreach (var station in stations)
				if (station.Occupant != null)
				{
					station.RequestedTarget = target;
					station.RequestedForce = force;
				}
		}
		internal void Release(PassengerFirePortAttackActivity activity)
		{
			if (activeActivity != activity)
				return;
			activeActivity = null;
			foreach (var station in stations)
			{
				if (Info.PersistentTargeting && Valid(station.RequestedTarget))
				{
					station.OpportunityTarget = station.RequestedTarget;
					station.OpportunityForce = station.RequestedForce;
					station.Persistent = true;
					station.Retaliating = false;
				}
				station.RequestedTarget = Target.Invalid;
				station.RequestedForce = false;
			}
		}
		public override Activity GetAttackActivity(Actor self, AttackSource source, in Target target, bool allowMove, bool forceAttack, Color? targetLineColor = null) =>
			new PassengerFirePortAttackActivity(self, this, target, allowMove, forceAttack, targetLineColor);
		public override void DoAttack(Actor self, in Target target) { /* Stations fire during their own synchronized tick. */ }
		public override void OnStopOrder(Actor self)
		{
			activeActivity = null;
			foreach (var station in stations)
				Clear(station);
			base.OnStopOrder(self);
		}
		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			activeActivity = null;
			foreach (var station in stations)
				Clear(station);
			Reconcile();
		}
		void INotifyActorDisposing.Disposing(Actor self)
		{
			foreach (var station in stations)
				if (station.Occupant != null)
					Exit(station);
		}
		void INotifyStanceChanged.StanceChanged(Actor self, AutoTarget autoTarget, UnitStance oldStance, UnitStance newStance)
		{
			if (newStance >= oldStance)
				return;
			foreach (var station in stations)
				if (!station.OpportunityForce)
				{
					station.OpportunityTarget = Target.Invalid;
					station.Persistent = false;
				}
		}
		void INotifyDamage.Damaged(Actor self, AttackInfo info)
		{
			var auto = self.TraitOrDefault<AutoTarget>();
			if (IsTraitDisabled || IsTraitPaused || !Info.OpportunityFire || info.Damage.Value < 0
				|| auto == null || auto.IsTraitDisabled || auto.Stance < UnitStance.ReturnFire
				|| info.Attacker == null || info.Attacker.AppearsFriendlyTo(self))
				return;
			var target = Target.FromActor(info.Attacker);
			foreach (var station in stations)
				if (station.RequestedTarget.Type == TargetType.Invalid && station.AutoTarget != null
					&& !station.AutoTarget.IsTraitDisabled && station.AutoTarget.Stance >= UnitStance.ReturnFire
					&& Eligible(station, target, false, true).Any())
				{
					var preferred = Scan(station);
					station.OpportunityTarget = preferred.Type != TargetType.Invalid ? preferred : target;
					station.OpportunityForce = station.Persistent = false;
					station.Retaliating = preferred.Type == TargetType.Invalid;
				}
		}

		protected override void Tick(Actor self)
		{
			Reconcile();
			IsAiming = false;
			foreach (var station in stations)
			{
				foreach (var muzzle in station.Muzzles.ToArray())
					muzzle.Animation.Tick();
				if (!self.IsInWorld || self.IsDead || self.WillDispose || IsTraitDisabled || IsTraitPaused)
				{
					Clear(station);
					continue;
				}
				if (station.Occupant == null)
					continue;
				var requested = RefreshTarget(station.RequestedTarget);
				station.RequestedTarget = Valid(requested) ? requested : Target.Invalid;
				var hasRequest = station.RequestedTarget.Type != TargetType.Invalid;
				var target = station.RequestedTarget;
				var force = station.RequestedForce;
				if (!hasRequest)
				{
					target = RefreshTarget(station.OpportunityTarget);
					force = station.OpportunityForce;
					var rescan = station.AutoTarget?.Info.DynamicWeaponPriority == true && !station.Persistent && !station.Retaliating
						&& --station.ScanTicks <= 0;
					if (rescan || !MayRetainOpportunity(station, target) || !Eligible(station, target, force, true).Any())
					{
						target = Target.Invalid;
						if ((rescan || --station.ScanTicks <= 0) && Info.OpportunityFire)
						{
							station.ScanTicks = Info.ScanInterval;
							target = Scan(station);
						}
						force = station.OpportunityForce = station.Persistent = station.Retaliating = false;
					}
					station.OpportunityTarget = target;
				}
				if (station.WeaponStations.Any(g => g.Turret != null))
				{
					FireWeaponStations(station);
					continue;
				}
				var arms = Eligible(station, target, force, true).ToArray();
				if (arms.Length == 0)
					continue;
				IsAiming = true;
				var position = self.CenterPosition + Offset(station);
				var yaw = (target.CenterPosition - position).Yaw;
				if (station.Facing != null)
					station.Facing.Facing = yaw;
				station.Position?.SetCenterPosition(station.Occupant, position);
				foreach (var arm in arms)
					FireArm(station, arm, target, yaw);
			}
			base.Tick(self);
		}
		void FireArm(FirePortStation station, Armament arm, Target target, WAngle yaw)
		{
			if (!arm.CheckFire(station.Occupant, station.Facing, target) || arm.Info.MuzzleSequence == null || station.Render == null)
				return;
			var animation = new Animation(host.World, station.Render.GetImage(station.Occupant), () => yaw);
			var muzzle = new AnimationWithOffset(animation, () => Offset(station), () => false,
				p => RenderUtils.ZOffsetFromCenter(host, p, 1024));
			station.Muzzles.Add(muzzle);
			animation.PlayThen(arm.Info.MuzzleSequence, () => station.Muzzles.Remove(muzzle));
		}
		void FireWeaponStations(FirePortStation station)
		{
			var occupant = station.Occupant;
			var position = host.CenterPosition + Offset(station);
			station.Position?.SetCenterPosition(occupant, position);
			foreach (var group in station.WeaponStations)
			{
				bool EligibleGroup(Target t, bool force) => Eligible(station, t, force, true).Any(group.Arms.Contains);
				var target = station.RequestedTarget;
				var force = station.RequestedForce;
				if (!EligibleGroup(target, force))
				{
					force = station.Persistent && station.OpportunityForce;
					if ((station.Persistent || station.Retaliating) && MayRetainOpportunity(station, station.OpportunityTarget)
						&& EligibleGroup(station.OpportunityTarget, force))
						target = station.OpportunityTarget;
					else
					{
						force = false;
						var hostAuto = host.TraitOrDefault<AutoTarget>();
						var mayScan = Info.OpportunityFire && hostAuto?.IsTraitDisabled != true
							&& !(hostAuto?.Stance < UnitStance.Defend) && station.AutoTarget != null
							&& !station.AutoTarget.IsTraitDisabled && station.AutoTarget.Stance >= UnitStance.Defend;
						if (!mayScan)
							group.Target = Target.Invalid;
						else if (--group.ScanTicks <= 0 || !EligibleGroup(group.Target, false))
						{
							group.ScanTicks = Info.ScanInterval;
							group.Target = station.AutoTarget.ScanForTarget(host, group.Arms, t => EligibleGroup(t, false),
									Info.TargetFrozenActors, new WDist(Offset(station).Length),
									(a, t) => Eligible(station, t, false, true).Contains(a)) ;
						}
						target = group.Target;
					}
				}
				group.Target = target;
				var arms = Eligible(station, target, force, true).Where(group.Arms.Contains).ToArray();
				if (arms.Length == 0)
					continue;
				IsAiming = true;
				if (group.Turret != null)
				{
					if (!group.Turret.FaceTarget(occupant, target))
						continue;
				}
				else if (station.Facing != null)
					station.Facing.Facing = (target.CenterPosition - position).Yaw;
				foreach (var arm in arms)
					FireArm(station, arm, target, group.Turret?.WorldOrientation.Yaw ?? (target.CenterPosition - position).Yaw);
			}
		}

		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer renderer) =>
			stations.SelectMany(s => s.Muzzles).SelectMany(m => m.Render(self, renderer.Palette(Info.MuzzlePalette)));
		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer renderer) { yield break; }
	}

	/// <summary>Broadcasts an explicit order to stations; opportunity targets remain station-local.</summary>
	public sealed class PassengerFirePortAttackActivity : Activity
	{
		readonly AttackGarrisoned attack;
		readonly IMove move;
		readonly Color? lineColor;
		readonly Player owner;
		public Target CurrentTarget { get; private set; }
		public bool ForceAttack { get; }
		public PassengerFirePortAttackActivity(Actor self, AttackGarrisoned attack, Target target, bool allowMove, bool force, Color? color)
		{
			this.attack = attack;
			owner = self.Owner;
			move = allowMove ? self.TraitOrDefault<IMove>() : null;
			CurrentTarget = target;
			ForceAttack = force;
			lineColor = color;
			ActivityType = ActivityType.Attack;
			ChildHasPriority = false;
		}
		public override bool Tick(Actor self)
		{
			if (IsCanceling || attack.IsTraitDisabled || self.Owner != owner)
				return true;
			if (attack.IsTraitPaused)
				return false;
			CurrentTarget = attack.RefreshTarget(CurrentTarget);
			if (CurrentTarget.Type == TargetType.Invalid)
				return true;
			attack.Request(this, CurrentTarget, ForceAttack);
			if (!TickChild(self))
				return false;
			if (attack.CanFireFromAnyPort(CurrentTarget, ForceAttack))
				return false;
			var arms = attack.MovementArmaments(CurrentTarget, ForceAttack).ToArray();
			var max = attack.MovementMaximumRange(CurrentTarget, ForceAttack);
			if (arms.Length == 0 && attack.WaitingForResupply(CurrentTarget, ForceAttack))
				return false;
			if (move == null || max == WDist.Zero)
				return true;
			var yaw = attack.TurnTowardPort(CurrentTarget, ForceAttack);
			if (yaw != null)
				QueueChild(new OpenRA.Mods.Common.Activities.Turn(self, yaw.Value));
			else
				QueueChild(move.MoveWithinRange(CurrentTarget, arms.Select(a => a.Weapon.MinRange).Min(), max));
			return false;
		}
		protected override void OnLastRun(Actor self) => attack.Release(this);
		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (lineColor != null)
				yield return new TargetLineNode(CurrentTarget, lineColor.Value);
		}
	}
}
