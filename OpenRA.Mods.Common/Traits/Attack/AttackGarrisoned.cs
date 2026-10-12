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
		[Desc("Write per-station gate diagnostics (binding, AT state, eligibility rejections, order route) to debug.log. Dev/testing only.")]
		public readonly bool LogFireDiagnostics = false;
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
		internal string LastDiag;
		internal string LastScanDiag;
		internal string LastFireDiag;
		internal readonly List<AnimationWithOffset> Muzzles = [];
		public FirePortStation(int index) { PortIndex = index; }
	}

	internal sealed class PassengerWeaponStation
	{
		internal Armament[] Arms;
		internal Turreted Turret;
		internal Target Target = Target.Invalid;
		internal int ScanTicks;
		internal string LastDiag;
	}

	/// <summary>Uses AttackBase orders but never AttackFollow's shared opportunity target.</summary>
	public class AttackGarrisoned : AttackBase, IIndependentAutoTarget, IFirePortAttack,
		INotifyPassengerEntered, INotifyPassengerExited, INotifyFirePortOccupantEntered, INotifyFirePortOccupantExited,
		INotifyOwnerChanged, INotifyStanceChanged, INotifyActorDisposing, INotifyDamage, IRender
	{
		public new readonly AttackGarrisonedInfo Info;
		readonly Actor host;
		readonly FirePortStation[] stations;
		readonly List<Actor> reconcileOccupants = [];
		readonly HashSet<Actor> reconcileOccupantSet = [];
		INotifyAttack[] notifyAttacks;
		Cargo[] cargoContainers;
		IFirePortOccupantProvider[] occupantProviders;
		BodyOrientation coords;
		PassengerFirePortAttackActivity activeActivity;
		string lastRequestSignature;
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
			cargoContainers = self.TraitsImplementing<Cargo>().ToArray();
			occupantProviders = self.TraitsImplementing<IFirePortOccupantProvider>().ToArray();
			coords = self.TraitOrDefault<BodyOrientation>();
			base.Created(self);
		}
		protected override Func<IEnumerable<Armament>> InitializeGetArmaments(Actor self) =>
			() => stations.Where(s => s.Occupant != null).SelectMany(s => s.Arms);

		void Enter(Actor actor)
		{
			if (notifyAttacks == null || actor == null || actor.IsDead)
				return;

			FirePortStation station = null;
			foreach (var candidate in stations)
			{
				if (candidate.Occupant == actor)
					return;

				if (station == null && candidate.Occupant == null)
					station = candidate;
			}

			if (station == null)
				return;
			station.Occupant = actor;
			station.ScanTicks = station.PortIndex % Info.ScanInterval + 1;
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
			if (Info.LogFireDiagnostics)
				DiagRaw($"bind port{station.PortIndex} occupant={actor.Info.Name}#{actor.ActorID} arms={station.Arms.Length}({string.Join("+", station.Arms.Select(a => a.Info.Name))}) occAT={OccATState(station)} effAT={EffATKind(station)} roster:cargo={cargoContainers.Sum(c => c.Passengers.Count())},provider={occupantProviders.Sum(p => p.Occupants.Count())} activity={host.CurrentActivity?.GetType().Name ?? "none"}");
		}

		void Exit(FirePortStation station)
		{
			DiagRaw($"unbind port{station.PortIndex} occupant={station.Occupant?.Info.Name}#{station.Occupant?.ActorID}");
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
		void Clear(FirePortStation station)
		{
			station.RequestedTarget = station.OpportunityTarget = Target.Invalid;
			station.RequestedForce = station.OpportunityForce = station.Persistent = station.Retaliating = false;
			station.ScanTicks = station.PortIndex % Info.ScanInterval + 1;
			foreach (var weaponStation in station.WeaponStations)
			{
				weaponStation.Target = Target.Invalid;
				weaponStation.ScanTicks = 0;
			}
		}
		void AddReconcileOccupant(Actor actor)
		{
			if (actor == null || actor.IsDead || !reconcileOccupantSet.Add(actor))
				return;

			var index = 0;
			while (index < reconcileOccupants.Count)
			{
				var occupant = reconcileOccupants[index];
				if (occupant.ActorID > actor.ActorID)
					break;

				index++;
			}

			reconcileOccupants.Insert(index, actor);
		}
		void Reconcile()
		{
			reconcileOccupants.Clear();
			reconcileOccupantSet.Clear();
			foreach (var cargo in cargoContainers)
			{
				if (cargo.Passengers is IList<Actor> passengers)
					for (var i = 0; i < passengers.Count; i++)
						AddReconcileOccupant(passengers[i]);
				else
					foreach (var actor in cargo.Passengers)
						AddReconcileOccupant(actor);
			}

			foreach (var provider in occupantProviders)
			{
				if (provider.Occupants is IList<Actor> occupants)
					for (var i = 0; i < occupants.Count; i++)
						AddReconcileOccupant(occupants[i]);
				else
					foreach (var actor in provider.Occupants)
						AddReconcileOccupant(actor);
			}

			var changed = false;
			var hasFreeStation = false;
			foreach (var station in stations)
			{
				if (station.Occupant != null && !reconcileOccupantSet.Contains(station.Occupant))
				{
					Exit(station);
					changed = true;
				}

				if (station.Occupant == null)
					hasFreeStation = true;
			}

			// Reconcile entry/load and exits. Existing bindings never move. Overflow gets
			// a station only when a free port appears, not by modulo sharing.
			if (changed || hasFreeStation)
				foreach (var actor in reconcileOccupants)
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
		IEnumerable<Armament> Eligible(FirePortStation station, Target target, bool force, bool checkRange, bool checkCone = true, bool includePaused = false, Action<string> reject = null)
		{
			if (!host.IsInWorld || host.IsDead || host.WillDispose || station.Occupant == null || station.Occupant.IsDead)
			{
				reject?.Invoke("host-or-occupant-unavailable");
				yield break;
			}
			if (!Valid(target))
			{
				reject?.Invoke("invalid-target");
				yield break;
			}
			if (checkCone && !InCone(station, target))
			{
				reject?.Invoke("outside-port-cone");
				yield break;
			}
			if (!force && (target.RequiresForceFire || target.Type == TargetType.Terrain && !Info.TargetTerrainWithoutForceFire))
			{
				reject?.Invoke("requires-force-fire");
				yield break;
			}
			var owner = target.Type == TargetType.Actor ? target.Actor.Owner : target.FrozenActor?.Owner;
			var position = host.CenterPosition + Offset(station);
			foreach (var arm in station.Arms)
			{
				if (arm.IsTraitDisabled)
				{
					reject?.Invoke(arm.Info.Name + ":disabled");
					continue;
				}
				if (!includePaused && arm.IsTraitPaused)
				{
					reject?.Invoke(arm.Info.Name + ":paused");
					continue;
				}
				if (!arm.Weapon.IsValidAgainst(target, host.World, host))
				{
					reject?.Invoke(arm.Info.Name + ":weapon-invalid");
					continue;
				}
				if (owner != null && !(force ? arm.Info.ForceTargetRelationships : arm.Info.TargetRelationships).HasRelationship(host.Owner.RelationshipWith(owner)))
				{
					reject?.Invoke(arm.Info.Name + ":relationship");
					continue;
				}
				if (checkRange && (!target.IsInRange(position, arm.MaxRange())
					|| arm.Weapon.MinRange != WDist.Zero && target.IsInRange(position, arm.Weapon.MinRange)))
				{
					reject?.Invoke(arm.Info.Name + ":range");
					continue;
				}
				yield return arm;
			}
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
		/// <summary>The AutoTarget that may drive this station at a given stance threshold.
		/// A functional occupant AutoTarget keeps per-passenger targeting; an absent or
		/// disabled occupant AutoTarget falls back to the host's so occupants retain
		/// host-driven coverage. Occupant stance always gates when the trait can run.</summary>
		AutoTarget EffectiveAutoTarget(FirePortStation station, UnitStance minStance)
		{
			var at = station.AutoTarget;
			if (at == null || at.IsTraitDisabled)
				at = host.TraitOrDefault<AutoTarget>();
			return at != null && !at.IsTraitDisabled && at.Stance >= minStance ? at : null;
		}
		string OccATState(FirePortStation station) =>
			station.AutoTarget == null ? "none" : station.AutoTarget.IsTraitDisabled ? "disabled" : station.AutoTarget.Stance.ToString();
		string HostATState()
		{
			var hostAT = host.TraitOrDefault<AutoTarget>();
			return hostAT == null ? "none" : hostAT.IsTraitDisabled ? "disabled" : hostAT.Stance.ToString();
		}
		string EffATKind(FirePortStation station)
		{
			var occ = station.AutoTarget;
			if (occ != null && !occ.IsTraitDisabled)
				return "occupant:" + occ.Stance;
			return "host:" + HostATState();
		}
		void Diag(FirePortStation station, string state)
		{
			if (!Info.LogFireDiagnostics || station.LastDiag == state)
				return;
			station.LastDiag = state;
			TextNotificationsManager.Debug($"{host.Info.Name}#{host.ActorID} port{station.PortIndex}: {state}");
		}
		void DiagScan(FirePortStation station, string state)
		{
			if (!Info.LogFireDiagnostics || station.LastScanDiag == state)
				return;
			station.LastScanDiag = state;
			TextNotificationsManager.Debug($"{host.Info.Name}#{host.ActorID} port{station.PortIndex} scan: {state}");
		}
		void DiagFire(FirePortStation station, string state)
		{
			if (!Info.LogFireDiagnostics || station.LastFireDiag == state)
				return;
			station.LastFireDiag = state;
			TextNotificationsManager.Debug($"{host.Info.Name}#{host.ActorID} port{station.PortIndex} fire: {state}");
		}
		void DiagRaw(string message)
		{
			if (Info.LogFireDiagnostics)
				TextNotificationsManager.Debug($"{host.Info.Name}#{host.ActorID}: {message}");
		}
		internal void DiagActivity(string message) => DiagRaw(message);
		void DiagGroup(PassengerWeaponStation group, FirePortStation station, string state)
		{
			if (!Info.LogFireDiagnostics || group.LastDiag == state)
				return;
			group.LastDiag = state;
			TextNotificationsManager.Debug($"{host.Info.Name}#{host.ActorID} port{station.PortIndex} group: {state}");
		}
		Target Scan(FirePortStation station)
		{
			var hostTarget = host.TraitOrDefault<AutoTarget>();
			if (!host.IsInWorld || host.IsDead || host.WillDispose || IsTraitDisabled || IsTraitPaused
				|| hostTarget?.IsTraitDisabled == true || hostTarget?.Stance < UnitStance.Defend)
			{
				DiagScan(station, "host-gate");
				return Target.Invalid;
			}
			var eff = EffectiveAutoTarget(station, UnitStance.Defend);
			if (eff == null)
			{
				DiagScan(station, $"no-effective-at occAT={OccATState(station)}");
				return Target.Invalid;
			}
			var result = eff.ScanForTarget(host, station.Arms,
				t => Eligible(station, t, false, true).Any(), Info.TargetFrozenActors, new WDist(Offset(station).Length),
				(a, t) => Eligible(station, t, false, true).Contains(a));
			DiagScan(station, result.Type == TargetType.Invalid
				? $"empty occAT={OccATState(station)} eff={EffATKind(station)}"
				: $"hit {result.Type} occAT={OccATState(station)} eff={EffATKind(station)}");
			return result;
		}
		bool MayRetainOpportunity(FirePortStation station, Target target)
		{
			if (station.Persistent)
				return true;
			var hostTarget = host.TraitOrDefault<AutoTarget>();
			var autoTarget = EffectiveAutoTarget(station, UnitStance.ReturnFire);
			if (!Info.OpportunityFire || autoTarget == null
				|| hostTarget?.IsTraitDisabled == true || hostTarget?.Stance < UnitStance.ReturnFire)
				return false;
			if (station.Retaliating)
				return true;
			if (hostTarget?.Stance < UnitStance.Defend || !Valid(target))
				return false;
			return target.Type == TargetType.Actor
				? autoTarget.HasValidTargetPriority(host, target.Actor.Owner, target.Actor.GetEnabledTargetTypes())
				: target.Type == TargetType.FrozenActor && autoTarget.HasValidTargetPriority(host, target.FrozenActor.Owner, target.FrozenActor.TargetTypes);
		}
		public IReadOnlyList<Target> ForecastTargets() => stations.Select(s => s.Occupant == null ? Target.Invalid : Scan(s)).ToArray();
		public override IEnumerable<Armament> ChooseArmamentsForTarget(Target target, bool forceAttack)
		{
			if (IsTraitDisabled)
				return [];

			return stations.SelectMany(s => Eligible(s, target, forceAttack, false));
		}
		public override bool HasAnyValidWeapons(in Target target, bool checkForCenterTargetingWeapons = false, bool reloadingIsInvalid = false)
		{
			if (IsTraitDisabled)
				return false;

			return ChooseArmamentsForTarget(target, false).Any(a => (!checkForCenterTargetingWeapons || a.Weapon.TargetActorCenter)
				&& (!reloadingIsInvalid || !a.IsReloading));
		}
		public override WDist GetMaximumRangeVersusTarget(in Target target)
		{
			if (IsTraitDisabled)
				return WDist.Zero;

			return ChooseArmamentsForTarget(target, true).Select(a => a.MaxRange()).DefaultIfEmpty(WDist.Zero).Max();
		}
		public override WDist GetMinimumRangeVersusTarget(in Target target)
		{
			if (IsTraitDisabled)
				return WDist.Zero;

			return ChooseArmamentsForTarget(target, true).Select(a => a.Weapon.MinRange).DefaultIfEmpty(WDist.Zero).Min();
		}

		internal void Request(PassengerFirePortAttackActivity activity, Target target, bool force, string via = "activity-tick")
		{
			activeActivity = activity;
			var requested = 0;
			foreach (var station in stations)
				if (station.Occupant != null)
				{
					station.RequestedTarget = target;
					station.RequestedForce = force;
					requested++;
				}
			var signature = via + "|" + target.Type + "|" + force;
			if (lastRequestSignature != signature)
			{
				lastRequestSignature = signature;
				DiagRaw($"request via={via} target={target.Type}{(target.Type == TargetType.Actor ? ":" + target.Actor.Info.Name : "")} force={force} stations={requested} currentActivity={host.CurrentActivity?.GetType().Name ?? "none"}");
			}
		}
		internal void Release(PassengerFirePortAttackActivity activity)
		{
			if (activeActivity != activity)
				return;
			activeActivity = null;
			DiagRaw($"release persistent={Info.PersistentTargeting} activity-ended");
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
		public override void OnResolveAttackOrder(Actor self, Activity activity, in Target target, bool queued, bool forceAttack)
		{
			// A competing attack trait may pre-empt the queued activity before it can tick;
			// deliver the request to stations immediately so explicit orders never vanish.
			// Queued orders wait for the activity so they apply in order.
			if (!queued && activity is PassengerFirePortAttackActivity passengerActivity)
				Request(passengerActivity, target, forceAttack, "order-resolve");
		}
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
				if (station.RequestedTarget.Type == TargetType.Invalid
					&& EffectiveAutoTarget(station, UnitStance.ReturnFire) != null
					&& Eligible(station, target, false, true).Any())
				{
					var preferred = Scan(station);
					station.OpportunityTarget = preferred.Type != TargetType.Invalid ? preferred : target;
					station.OpportunityForce = station.Persistent = false;
					station.Retaliating = preferred.Type == TargetType.Invalid;
					DiagRaw($"retaliate port{station.PortIndex} attacker={info.Attacker.Info.Name}");
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
					var rescan = EffectiveAutoTarget(station, UnitStance.Defend)?.Info.DynamicWeaponPriority == true && !station.Persistent && !station.Retaliating
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
				var rejects = Info.LogFireDiagnostics ? new List<string>() : null;
				var arms = Eligible(station, target, force, true, reject: rejects == null ? (Action<string>)null : rejects.Add).ToArray();
				if (Info.LogFireDiagnostics)
					Diag(station, arms.Length > 0
						? $"{(hasRequest ? "ordered" : "opp")}:{target.Type} arms={arms.Length}"
						: $"{(hasRequest ? "ordered" : "opp")}-silent:{rejects.FirstOrDefault() ?? "none"} occAT={OccATState(station)} effAT={EffATKind(station)} hostAT={HostATState()}");
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
			if (!arm.CheckFire(station.Occupant, station.Facing, target))
			{
				DiagFire(station, $"checkfire-false arm={arm.Info.Name} delay={arm.FireDelay} burst={arm.Burst} paused={arm.IsTraitPaused} disabled={arm.IsTraitDisabled}");
				return;
			}
			DiagFire(station, $"fired arm={arm.Info.Name}");
			if (arm.Info.MuzzleSequence == null || station.Render == null)
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
						var autoTarget = EffectiveAutoTarget(station, UnitStance.Defend);
						var mayScan = Info.OpportunityFire && hostAuto?.IsTraitDisabled != true
							&& !(hostAuto?.Stance < UnitStance.Defend) && autoTarget != null;
						if (!mayScan)
						{
							group.Target = Target.Invalid;
							DiagGroup(group, station, $"mayScan-false occAT={OccATState(station)} effAT={EffATKind(station)} hostAT={(hostAuto == null ? "none" : hostAuto.IsTraitDisabled ? "disabled" : hostAuto.Stance.ToString())}");
						}
						else if (--group.ScanTicks <= 0 || !EligibleGroup(group.Target, false))
						{
							group.ScanTicks = Info.ScanInterval;
							group.Target = autoTarget.ScanForTarget(host, group.Arms, t => EligibleGroup(t, false),
									Info.TargetFrozenActors, new WDist(Offset(station).Length),
									(a, t) => Eligible(station, t, false, true).Contains(a)) ;
						}
						target = group.Target;
					}
				}
				group.Target = target;
				var arms = Eligible(station, target, force, true).Where(group.Arms.Contains).ToArray();
				DiagGroup(group, station, $"target={target.Type} arms={arms.Length}");
				if (arms.Length == 0)
					continue;
				IsAiming = true;
				// TakeCover uses Turreted for prone muzzle offsets, not independent aiming.
				// Without a matching AttackTurreted driver, aim these arms with the body.
				// Real turrets retain their facing, disabled and paused gates.
				var aimingTurret = group.Turret;
				if (aimingTurret is TakeCover)
				{
					if (aimingTurret.IsTraitDisabled || aimingTurret.IsTraitPaused)
						continue;

					// A TakeCover can also have a real driver: preserve that driver's gates.
					if (!occupant.TraitsImplementing<AttackTurreted>().Any(at =>
						((AttackTurretedInfo)at.Info).Turrets.Contains(aimingTurret.Name)))
						aimingTurret = null;
				}
				if (aimingTurret != null)
				{
					if (!aimingTurret.FaceTarget(occupant, target))
						continue;
				}
				else if (station.Facing != null)
					station.Facing.Facing = (target.CenterPosition - position).Yaw;
				foreach (var arm in arms)
					FireArm(station, arm, target, aimingTurret?.WorldOrientation.Yaw ?? (target.CenterPosition - position).Yaw);
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
		bool firstTick = true;
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
			if (firstTick)
			{
				firstTick = false;
				attack.DiagActivity($"passenger-activity first-tick target={CurrentTarget.Type} force={ForceAttack}");
			}
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
		protected override void OnLastRun(Actor self)
		{
			attack.DiagActivity($"passenger-activity ended canceled={IsCanceling} ran={!firstTick}");
			attack.Release(this);
		}
		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (lineColor != null)
				yield return new TargetLineNode(CurrentTarget, lineColor.Value);
		}
	}
}
