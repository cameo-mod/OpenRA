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
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.HitShapes;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Test
{
	// Uses real actors, armaments, priorities, station scans and notifications. Only
	// the spatial index and outer World shell are substituted to avoid a renderer,
	// assets and local server. This is not a second implementation of targeting.
	[TestFixture]
	public sealed class AttackGarrisonedTest
	{
		static void Set(object obj, string name, object value) => obj.GetType().GetField(name,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(obj, value);
		static T Info<T>(params (string Key, object Value)[] fields) where T : new()
		{
			var info = new T();
			foreach (var (key, value) in fields)
				Set(info, key, value);
			return info;
		}

		public class SpatialIndex : DispatchProxy
		{
			public readonly List<Actor> Targets = [];
			protected override object Invoke(MethodInfo method, object[] args) => method.Name == "ActorsInBox"
				? Targets : throw new NotSupportedException(method.Name);
		}
		sealed class SpaceInfo : TraitInfo, IOccupySpaceInfo
		{
			public bool SharesCell => false;
			public IReadOnlyDictionary<CPos, SubCell> OccupiedCells(ActorInfo info, CPos location, SubCell subCell = SubCell.Any) =>
				new Dictionary<CPos, SubCell> { [location] = subCell };
			public override object Create(ActorInitializer init) => new Space();
		}
		sealed class Space : IPositionable, IFacing, IDefaultVisibility
		{
			public bool Visible = true;
			public WPos CenterPosition { get; private set; }
			public CPos TopLeft => CPos.Zero;
			public WAngle TurnSpeed => new(32);
			public WAngle Facing { get; set; }
			public WRot Orientation => WRot.FromYaw(Facing);
			public (CPos Cell, SubCell SubCell)[] OccupiedCells() => [];
			public bool IsVisible(Actor self, Player byPlayer) => Visible;
			public bool CanExistInCell(CPos location) => true;
			public bool IsLeavingCell(CPos location, SubCell subCell = SubCell.Any) => false;
			public bool CanEnterCell(CPos location, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All) => true;
			public SubCell GetValidSubCell(SubCell preferred = SubCell.Any) => SubCell.FullCell;
			public SubCell GetAvailableSubCell(CPos location, SubCell preferredSubCell = SubCell.Any, Actor ignoreActor = null, BlockedByActor check = BlockedByActor.All) => SubCell.FullCell;
			public void SetPosition(Actor self, CPos cell, SubCell subCell = SubCell.Any) { }
			public void SetPosition(Actor self, WPos pos) => CenterPosition = pos;
			public void SetCenterPosition(Actor self, WPos pos) => CenterPosition = pos;
		}
		sealed class RecorderInfo : TraitInfo<Recorder> { }
		public sealed class Recorder : INotifyAttack
		{
			public readonly List<string> Events = [];
			public void PreparingAttack(Actor self, in Target target, Armament armament, Barrel barrel) { }
			public void Attacking(Actor self, in Target target, Armament armament, Barrel barrel) =>
				Events.Add($"{self.ActorID}:{armament.Actor.ActorID}:{target.Actor?.ActorID ?? target.FrozenActor?.ID}:{armament.Actor.CenterPosition}:{armament.Actor.Orientation.Yaw.Angle}:{target.CenterPosition}");
		}
		public sealed class Fixture
		{
			public readonly World World;
			public readonly Player Own, Enemy;
			public readonly Actor Host, Rifle, Rocket, Infantry, Tank;
			public readonly AttackGarrisoned Attack;
			public readonly Recorder Recorder;
			public Fixture(WVec[] offsets = null, WAngle[] cones = null, WAngle[] yaws = null, bool cargo = false, AttackGarrisonedInfo schema = null, int rifleMinRange = 0, bool dynamicPriority = false)
			{
				Log.AddChannel("debug", null);
				Own = Player("own", "enemy"); Enemy = Player("enemy", "own");
				var hostInfo = Info<AttackGarrisonedInfo>(("PortOffsets", offsets ?? new[] { WVec.Zero, WVec.Zero }), ("PortCones", cones), ("PortYaws", yaws),
					("PauseOnCondition", new BooleanExpression("paused")), ("RequiresCondition", new BooleanExpression("!disabled")));
				if (schema != null) { Set(schema, "PortOffsets", hostInfo.PortOffsets); hostInfo = schema; }
				var body = Info<BodyOrientationInfo>(("QuantizedFacings", 0), ("UseClassicPerspectiveFudge", false));
				ActorInfo Passenger(string name, string weapon, string prefer) => new(name,
					new SpaceInfo(), body, new AttackFollowInfo(), Info<ArmamentInfo>(("Weapon", weapon), ("PauseOnCondition", new BooleanExpression("empty"))),
					Info<PassengerInfo>(("Weight", 1)), new OpenRA.Mods.AS.Traits.GarrisonerInfo(),
					Info<AutoTargetInfo>(("DynamicWeaponPriority", dynamicPriority)), Info<AutoTargetPriorityInfo>(("ValidTargets", new BitSet<TargetableType>(prefer)), ("Priority", 10)),
					Info<AutoTargetPriorityInfo>(("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle")), ("Priority", 1)));
				var actors = new Dictionary<string, ActorInfo>
				{
					["host"] = new("host", new SpaceInfo(), body, hostInfo,
						cargo ? Info<CargoInfo>(("MaxWeight", 8)) : Info<OpenRA.Mods.AS.Traits.GarrisonableInfo>(("MaxWeight", 8)),
						new RecorderInfo(), new AutoTargetInfo()),
					["rifle"] = Passenger("rifle", "rifle", "Infantry"),
					["rocket"] = Passenger("rocket", "rocket", "Vehicle"),
					["infantry"] = new("infantry", new SpaceInfo(), Info<TargetableInfo>(("TargetTypes", new BitSet<TargetableType>("Infantry")))),
					["tank"] = new("tank", new SpaceInfo(), Info<TargetableInfo>(("TargetTypes", new BitSet<TargetableType>("Vehicle")))),
				};
				actors["multi"] = new ActorInfo("multi", new SpaceInfo(), body,
					Info<AttackMultiTurretedInfo>(("Turrets", ImmutableArray.Create("primary", "secondary"))),
					Info<TurretedInfo>(("Turret", "primary")), Info<TurretedInfo>(("Turret", "secondary")),
					Info<ArmamentInfo>(("Weapon", "rifle"), ("Name", "primary"), ("Turret", "primary")),
					Info<ArmamentInfo>(("Weapon", "rocket"), ("Name", "secondary"), ("Turret", "secondary")),
					Info<PassengerInfo>(("Weight", 1)), new OpenRA.Mods.AS.Traits.GarrisonerInfo(),
					new RecorderInfo(), Info<AutoTargetInfo>(("DynamicWeaponPriority", dynamicPriority)),
					Info<AutoTargetPriorityInfo>(("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle"))));
				var weapons = new Dictionary<string, OpenRA.GameRules.WeaponInfo>
				{
					["rifle"] = Info<OpenRA.GameRules.WeaponInfo>(("Range", new WDist(4096)), ("ReloadDelay", 3), ("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle")), ("Warheads", ImmutableArray.Create<IWarhead>(Info<TargetDamageWarhead>(("Damage", 10))))),
					["rocket"] = Info<OpenRA.GameRules.WeaponInfo>(("Range", new WDist(8192)), ("ReloadDelay", 3), ("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle")), ("Warheads", ImmutableArray.Create<IWarhead>(Info<TargetDamageWarhead>(("Damage", 20))))),
				};
				if (dynamicPriority)
				{
					foreach (var weapon in weapons.Values) Set(weapon.Warheads[0], "ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle"));
					foreach (var (name, armor) in new[] { ("infantry", "Infantry"), ("tank", "Heavy") })
						actors[name] = new ActorInfo(name, new SpaceInfo(), body,
							Info<TargetableInfo>(("TargetTypes", new BitSet<TargetableType>(name == "tank" ? "Vehicle" : "Infantry"))),
							Info<HealthInfo>(("HP", 1000)), Info<ArmorInfo>(("Type", armor)),
							Info<HitShapeInfo>(("Type", new CircleShape(new WDist(100)))));
					Set(weapons["rifle"].Warheads[0], "Versus", new Dictionary<string, int> { ["Infantry"] = 80, ["Heavy"] = 120 }.ToFrozenDictionary());
					Set(weapons["rocket"].Warheads[0], "Versus", new Dictionary<string, int> { ["Infantry"] = 120, ["Heavy"] = 80 }.ToFrozenDictionary());
				}
				Set(weapons["rifle"], "MinRange", new WDist(rifleMinRange));
				var rules = new Ruleset(actors, weapons, null, null, null, null, null, null);
				World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
				Set(World, "TraitDict", new TraitDictionary());
				var frameEnd = new Queue<Action<World>>(); Set(World, "frameEndActions", frameEnd);
				var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
				typeof(Map).GetProperty("Rules").SetValue(map, rules);
				Set(World, "Map", map);
				Set(World, "SharedRandom", new MersenneTwister(17));
				var index = DispatchProxy.Create<IActorMap, SpatialIndex>();
				Set(World, "ActorMap", index);
				Host = Create("host", Own, WPos.Zero);
				if (dynamicPriority) { Set(Own, "PlayerActor", Host); Set(Enemy, "PlayerActor", Host); }
				while (frameEnd.Count > 0) frameEnd.Dequeue()(World);
				Rifle = Create("rifle", Own, WPos.Zero);
				Rocket = Create("rocket", Own, WPos.Zero);
				Infantry = Create("infantry", Enemy, new WPos(2048, 0, 0));
				Tank = Create("tank", Enemy, new WPos(3072, 0, 0));
				((SpatialIndex)(object)index).Targets.AddRange([Infantry, Tank]);
				Attack = Host.Trait<AttackGarrisoned>();
				Recorder = Host.Trait<Recorder>();
			}
			static Player Player(string name, string enemy)
			{
				var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
				Set(p, "Playable", true); Set(p, "IsBot", true); Set(p, "Faction", new FactionInfo());
				p.PlayerMask = new LongBitSet<PlayerBitMask>(name);
				p.EnemyPlayersMask = new LongBitSet<PlayerBitMask>(enemy);
				return p;
			}
			public Actor Create(string name, Player owner, WPos position)
			{
				var actor = new Actor(World, name, new TypeDictionary { new OwnerInit(owner) });
				actor.Initialize(false); actor.IsInWorld = true;
				actor.Trait<Space>().SetCenterPosition(actor, position);
				return actor;
			}
			public void Enter(Actor actor, bool cargo = false)
			{
				if (Host.TraitOrDefault<Cargo>() is Cargo container) container.Load(Host, actor);
				else Host.Trait<OpenRA.Mods.AS.Traits.Garrisonable>().Load(Host, actor);
			}
			public void Exit(Actor actor, bool cargo = false)
			{
				if (Host.TraitOrDefault<Cargo>() is Cargo container) container.Unload(Host, actor);
				else Host.Trait<OpenRA.Mods.AS.Traits.Garrisonable>().Unload(Host, actor);
			}
			public void Tick()
			{
				((ITick)Attack).Tick(Host);
				foreach (var actor in new[] { Rifle, Rocket })
					foreach (var arm in actor.TraitsImplementing<Armament>()) ((ITick)arm).Tick(actor);
			}
			public Actor Recreate()
			{
				var inits = new TypeDictionary { new OwnerInit(Own) };
				if (Host.TraitOrDefault<Cargo>() is Cargo cargo)
					inits.Add(new RuntimeCargoInit(cargo.Info, cargo.Passengers.ToArray()));
				else
				{
					var garrison = Host.Trait<OpenRA.Mods.AS.Traits.Garrisonable>();
					inits.Add(new OpenRA.Mods.AS.Traits.RuntimeGarrisonInit(garrison.Info, garrison.Garrisoners.ToArray()));
				}
				((INotifyActorDisposing)Attack).Disposing(Host);
				var next = new Actor(World, "host", inits); next.Initialize(false); next.IsInWorld = true;
				var queue = (Queue<Action<World>>)typeof(World).GetField("frameEndActions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(World);
				while (queue.Count > 0) queue.Dequeue()(World);
				return next;
			}
		}

		[TestCase(false)]
		[TestCase(true)]
		public void IndependentPrioritiesFireSimultaneouslyWithoutPortRng(bool cargo)
		{
			var f = new Fixture(cargo: cargo); f.Enter(f.Rifle, cargo); f.Enter(f.Rocket, cargo);
			var rng = f.World.SharedRandom.Last;
			f.Tick();
			Assert.That(f.Attack.Stations.Select(s => s.OpportunityTarget.Actor), Is.EqualTo(new[] { f.Infantry, f.Tank }));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(2), "One host notification per actual fire");
			Assert.That(f.World.SharedRandom.Last, Is.EqualTo(rng));
			Assert.That(f.Host.Trait<AutoTarget>().ActiveAttackBases, Is.Empty);
		}
		[Test]
		public void MultiTurretsAimAndFireAtDifferentTargetsInOneTick()
		{
			var f = new Fixture(dynamicPriority: true);
			var multi = f.Create("multi", f.Own, WPos.Zero);
			var attack = multi.Trait<AttackMultiTurreted>();
			var rng = f.World.SharedRandom.Last;
			((ITick)attack).Tick(multi);
			Assert.That(attack.TurretTargets.Select(t => t.Actor), Is.EqualTo(new[] { f.Tank, f.Infantry }));
			Assert.That(multi.Trait<Recorder>().Events.Count, Is.EqualTo(2));
			Assert.That(f.World.SharedRandom.Last, Is.EqualTo(rng));
		}
		[TestCase(false)]
		[TestCase(true)]
		public void PassengerTurretsFireIndependentlyAndRespectStanceImmediately(bool cargo)
		{
			var f = new Fixture(dynamicPriority: true, cargo: cargo);
			var multi = f.Create("multi", f.Own, WPos.Zero);
			f.Enter(multi);
			f.Tick();
			Assert.That(f.Attack.Stations[0].WeaponTargets.Select(t => t.Actor), Is.EqualTo(new[] { f.Tank, f.Infantry }));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(2));
			multi.Trait<AutoTarget>().SetStance(multi, UnitStance.HoldFire);
			foreach (var arm in multi.TraitsImplementing<Armament>())
				for (var i = 0; i < 5; i++) ((ITick)arm).Tick(multi);
			f.Tick();
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(2));
			Assert.That(f.Attack.Stations[0].WeaponTargets.Select(t => t.Type), Is.All.EqualTo(TargetType.Invalid));
			f.Exit(multi);
			Assert.That(f.Attack.Stations[0].WeaponTargets, Is.Empty);
		}
		sealed class CustomArmorWarhead : TargetDamageWarhead
		{
			public override WeaponTargetScore TargetingVersus(Actor victim, HitShape shape) => new(25, 1);
		}
		[Test]
		public void ScoringUsesCustomWarheadArmorPolicyInsteadOfAuthoredRows()
		{
			var f = new Fixture(dynamicPriority: true);
			var arm = f.Rifle.Trait<Armament>();
			var warhead = Info<CustomArmorWarhead>(("Damage", 10), ("ValidTargets", new BitSet<TargetableType>("Vehicle")));
			Set(arm.Weapon, "Warheads", ImmutableArray.Create<IWarhead>(warhead));
			Assert.That(WeaponTargetScore.Against(arm, f.Rifle, f.Tank).CompareTo(new WeaponTargetScore(25, 1)), Is.Zero);
		}
		[Test]
		public void DynamicPassengerRescansWhenBetterVisibleTargetArrives()
		{
			var f = new Fixture(dynamicPriority: true);
			f.Tank.Trait<Space>().Visible = false;
			f.Enter(f.Rifle);
			f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Infantry));
			f.Tank.Trait<Space>().Visible = true;
			for (var i = 0; i < 6; i++) f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Tank));
		}
		[TestCase(AttackSource.AutoTarget, true)]
		[TestCase(AttackSource.AttackMove, true)]
		[TestCase(AttackSource.Default, false)]
		public void AutomaticApproachYieldsToReadyTargetButExplicitOrderDoesNot(AttackSource source, bool retarget)
		{
			var f = new Fixture(dynamicPriority: true);
			f.Tank.Trait<Space>().SetCenterPosition(f.Tank, new WPos(6000, 0, 0));
			var attack = f.Rifle.Trait<AttackFollow>();
			var activity = new AttackFollow.AttackActivity(f.Rifle, source, Target.FromActor(f.Tank), false, false);
			activity.Tick(f.Rifle);
			Assert.That(attack.RequestedTarget.Actor, Is.SameAs(retarget ? f.Infantry : f.Tank));
		}
		[Test]
		public void ReadySniperEngagementWinsBeforeStrongerOutOfRangeDemolitionTarget()
		{
			var f = new Fixture(dynamicPriority: true);
			var multi = f.Create("multi", f.Own, WPos.Zero);
			var cannon = multi.TraitsImplementing<Armament>().First();
			Set(cannon.Weapon, "Range", new WDist(1024));
			var chosen = multi.Trait<AutoTarget>().ScanForTarget(multi, true, true, true);
			Assert.That(chosen.Actor, Is.SameAs(f.Infantry));
		}
		[Test]
		public void DynamicVersusOverridesClassWeightsAndDistanceForRealPassengers()
		{
			var f = new Fixture(dynamicPriority: true); f.Enter(f.Rifle); f.Enter(f.Rocket);
			Assert.That(f.Rifle.Trait<AutoTarget>().Info.DynamicWeaponPriority, Is.True);
			Assert.That(WeaponTargetScore.Against(f.Rifle.Trait<Armament>(), f.Rifle, f.Tank).CompareTo(new WeaponTargetScore(120, 1)), Is.Zero);
			f.Tick();
			Assert.That(f.Attack.Stations.Select(s => s.OpportunityTarget.Actor), Is.EqualTo(new[] { f.Tank, f.Infantry }));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(2));
		}
		[Test]
		public void DynamicVersusNeverSelectsHiddenBetterTarget()
		{
			var f = new Fixture(dynamicPriority: true); f.Enter(f.Rifle);
			f.Tank.Trait<Space>().Visible = false;
			f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Infantry));
		}
		[Test]
		public void SelectedArmamentsAndEachPortRangeFilterBeforePriority()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket);
			f.Infantry.Trait<Space>().SetCenterPosition(f.Infantry, new WPos(6000, 0, 0));
			f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Tank));
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Actor, Is.SameAs(f.Tank));
			Assert.That(f.Attack.CanFireFromPort(0, Target.FromActor(f.Infantry)), Is.False);
			Assert.That(f.Attack.CanFireFromPort(1, Target.FromActor(f.Infantry)), Is.True);
		}
		[Test]
		public void ExitAndOverflowKeepBindingsStableAndRemoveHooks()
		{
			var f = new Fixture(); var third = f.Create("rifle", f.Own, WPos.Zero);
			f.Enter(f.Rifle); f.Enter(f.Rocket); f.Enter(third); f.Tick();
			Assert.That(f.Attack.Stations.Select(s => s.Occupant), Is.EqualTo(new[] { f.Rifle, f.Rocket }));
			f.Exit(f.Rifle); f.Tick();
			Assert.That(f.Attack.Stations.Select(s => s.Occupant), Is.EqualTo(new[] { third, f.Rocket }));
			Assert.That(f.Attack.Stations.Any(s => s.Occupant == f.Rifle), Is.False);
			var count = f.Recorder.Events.Count;
			for (var i = 0; i < 4; i++) ((ITick)f.Rifle.Trait<Armament>()).Tick(f.Rifle);
			f.Rifle.Trait<Armament>().CheckFire(f.Rifle, f.Rifle.Trait<IFacing>(), Target.FromActor(f.Infantry));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(count));
		}
		[Test]
		public void HostOrdersBlockSubstitutionAndNewEntrantsInheritThenStopClears()
		{
			var f = new Fixture(); f.Enter(f.Rifle);
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromActor(f.Infantry), false, false);
			activity.Tick(f.Host); f.Enter(f.Rocket); f.Tick();
			Assert.That(f.Attack.Stations.All(s => s.RequestedTarget.Actor == f.Infantry), Is.True);
			f.Attack.OnStopOrder(f.Host);
			Assert.That(f.Attack.Stations.All(s => s.RequestedTarget.Type == TargetType.Invalid && s.OpportunityTarget.Type == TargetType.Invalid), Is.True);
		}
		[Test]
		public void InvisibleTargetsAreNotRetainedOrForecastAndPassengerStanceIsRespected()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket); f.Tick();
			f.Infantry.Trait<Space>().Visible = false; f.Tank.Trait<Space>().Visible = false;
			f.Tick();
			Assert.That(f.Attack.ForecastTargets().All(t => t.Type == TargetType.Invalid), Is.True);
			Assert.That(f.Attack.Stations.All(s => s.OpportunityTarget.Type == TargetType.Invalid), Is.True);
			f.Infantry.Trait<Space>().Visible = true; f.Tank.Trait<Space>().Visible = true;
			f.Rifle.Trait<AutoTarget>().SetStance(f.Rifle, UnitStance.HoldFire);
			for (var i = 0; i < 6; i++) f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Actor, Is.SameAs(f.Tank));
		}
		[Test]
		public void OffWorldCarrierCannotFireOrExposeCapabilityWhileItIsCargoItself()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket); f.Tick();
			f.Host.IsInWorld = false;
			var count = f.Recorder.Events.Count;
			for (var i = 0; i < 6; i++) f.Tick();
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(count));
			Assert.That(f.Attack.ForecastTargets().All(t => t.Type == TargetType.Invalid), Is.True);
			Assert.That(f.Attack.CanFireFromAnyPort(Target.FromActor(f.Tank)), Is.False);
			Assert.That(f.Attack.Stations.All(s => s.OpportunityTarget.Type == TargetType.Invalid && s.RequestedTarget.Type == TargetType.Invalid), Is.True);
		}
		[Test]
		public void ReturnFireNeedsAnAttackerAndDefendRetainsEachPassengersPriority()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket);
			f.Host.Trait<AutoTarget>().SetStance(f.Host, UnitStance.ReturnFire);
			f.Rifle.Trait<AutoTarget>().SetStance(f.Rifle, UnitStance.ReturnFire);
			f.Rocket.Trait<AutoTarget>().SetStance(f.Rocket, UnitStance.ReturnFire);
			f.Tick(); Assert.That(f.Recorder.Events, Is.Empty);
			((INotifyDamage)f.Attack).Damaged(f.Host, new AttackInfo { Attacker = f.Tank, Damage = new Damage(10) });
			f.Tick(); Assert.That(f.Recorder.Events.Count, Is.EqualTo(2));
			f.Host.Trait<AutoTarget>().SetStance(f.Host, UnitStance.Defend);
			f.Rifle.Trait<AutoTarget>().SetStance(f.Rifle, UnitStance.Defend);
			f.Rocket.Trait<AutoTarget>().SetStance(f.Rocket, UnitStance.Defend);
			((INotifyDamage)f.Attack).Damaged(f.Host, new AttackInfo { Attacker = f.Tank, Damage = new Damage(10) });
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Infantry));
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Actor, Is.SameAs(f.Tank));
		}
		[Test]
		public void ResupplyFlagKeepsTheExplicitOrderWhileAllWeaponsArePaused()
		{
			var f = new Fixture(schema: Info<AttackGarrisonedInfo>(("AbortOnResupply", false)));
			f.Enter(f.Rifle); f.Rifle.GrantCondition("empty");
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromActor(f.Infantry), false, false);
			Assert.That(activity.Tick(f.Host), Is.False);
			f.Tick(); Assert.That(f.Recorder.Events, Is.Empty);
			Assert.That(f.Attack.Stations[0].RequestedTarget.Actor, Is.SameAs(f.Infantry));
		}
		[Test]
		public void IneligiblePreferredTargetCannotUseAnUnselectedPassengerWeapon()
		{
			var f = new Fixture();
			Set(f.Rifle.Trait<Armament>().Weapon, "ValidTargets", new BitSet<TargetableType>("Vehicle"));
			var info = Info<ArmamentInfo>(("Weapon", "rocket"), ("Name", "not-selected"));
			info.RulesetLoaded(f.World.Map.Rules, f.Rifle.Info);
			var extra = new Armament(f.Rifle, info); f.Rifle.AddTrait(extra); ((INotifyCreated)extra).Created(f.Rifle);
			f.Enter(f.Rifle); f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Tank));
			Assert.That(f.Attack.ArmamentsAgainst(Target.FromActor(f.Infantry)), Is.Empty);
		}
		[Test]
		public void FrozenExplicitTargetUsesRememberedPositionWithoutRefreshingHiddenLiveState()
		{
			var schema = Info<AttackGarrisonedInfo>(("TargetFrozenActors", true));
			var f = new Fixture(schema: schema); f.Enter(f.Rifle);
			var remembered = new WPos(2048, 0, 0);
			var frozen = (FrozenActor)RuntimeHelpers.GetUninitializedObject(typeof(FrozenActor));
			Set(frozen, "actor", f.Tank); Set(frozen, "CenterPosition", remembered);
			Set(frozen, "targetablePositions", new List<WPos> { remembered });
			typeof(FrozenActor).GetProperty("Owner").SetValue(frozen, f.Enemy);
			typeof(FrozenActor).GetProperty("Visible").SetValue(frozen, true);
			typeof(FrozenActor).GetProperty("TargetTypes").SetValue(frozen, new BitSet<TargetableType>("Vehicle"));
			f.Tank.Trait<Space>().Visible = false;
			f.Tank.Trait<Space>().SetCenterPosition(f.Tank, new WPos(50000, 0, 0));
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromFrozenActor(frozen), false, false);
			activity.TickOuter(f.Host); f.Tick();
			Assert.That(f.Attack.Stations[0].RequestedTarget.Type, Is.EqualTo(TargetType.FrozenActor));
			Assert.That(f.Attack.Stations[0].RequestedTarget.CenterPosition, Is.EqualTo(remembered));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(1));
		}
		[Test]
		public void ForcedRequestHonorsMinimumRangeAndNeverSubstitutesOpportunity()
		{
			var f = new Fixture(rifleMinRange: 1024); f.Enter(f.Rifle); f.Enter(f.Rocket);
			f.Infantry.Trait<Space>().SetCenterPosition(f.Infantry, new WPos(512, 0, 0));
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromActor(f.Infantry), false, true);
			activity.TickOuter(f.Host); f.Tick();
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(1));
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Type, Is.EqualTo(TargetType.Invalid));
			Assert.That(f.Attack.ArmamentsAgainst(Target.FromActor(f.Infantry)).Select(a => a.Actor), Is.EqualTo(new[] { f.Rocket }));
		}
		[Test]
		public void CancellationPersistsTheExplicitTargetIndependently()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket);
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromActor(f.Infantry), false, false);
			activity.TickOuter(f.Host); activity.Cancel(f.Host); activity.TickOuter(f.Host);
			Assert.That(f.Attack.Stations.All(s => s.RequestedTarget.Type == TargetType.Invalid && s.OpportunityTarget.Actor == f.Infantry), Is.True);
			f.Tick();
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Actor, Is.SameAs(f.Infantry));
		}
		[TestCase(false)]
		[TestCase(true)]
		public void RuntimeContainerRestorationRecreatesBindingsAndTransfersNotifyHooks(bool cargo)
		{
			var f = new Fixture(cargo: cargo); f.Enter(f.Rifle); f.Enter(f.Rocket);
			var next = f.Recreate(); var attack = next.Trait<AttackGarrisoned>();
			Assert.That(attack.Stations.Select(s => s.Occupant), Is.EqualTo(new[] { f.Rifle, f.Rocket }));
			Assert.That(f.Attack.Stations.All(s => s.Occupant == null), Is.True);
			((ITick)attack).Tick(next);
			Assert.That(f.Recorder.Events, Is.Empty);
			Assert.That(next.Trait<Recorder>().Events.Count, Is.EqualTo(2));
		}
		[Test]
		public void AliasCreatesTheCanonicalRuntime()
		{
			var f = new Fixture(schema: new OpenRA.Mods.AS.Traits.AttackOpenToppedInfo());
			Assert.That(f.Host.Trait<AttackGarrisoned>().GetType(), Is.EqualTo(typeof(AttackGarrisoned)));
		}
		[Test]
		public void ConeAndOffsetAreAppliedBeforeScanAndAtTheShotOrigin()
		{
			var f = new Fixture([new WVec(0, 1024, 0), WVec.Zero], [new WAngle(64), new WAngle(64)], [new WVec(1, 0, 0).Yaw, new WVec(-1, 0, 0).Yaw]);
			f.Enter(f.Rifle); f.Enter(f.Rocket);
			f.Infantry.Trait<Space>().SetCenterPosition(f.Infantry, new WPos(5000, 0, 0));
			f.Tick();
			Assert.That(f.Attack.Stations[0].OpportunityTarget.Actor, Is.SameAs(f.Infantry));
			Assert.That(f.Rifle.CenterPosition, Is.EqualTo(new WPos(1024, 0, 0)));
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Type, Is.EqualTo(TargetType.Invalid));
			f.Tank.Trait<Space>().SetCenterPosition(f.Tank, new WPos(-2048, 0, 0));
			for (var i = 0; i < 6; i++) f.Tick();
			Assert.That(f.Attack.Stations[1].OpportunityTarget.Actor, Is.SameAs(f.Tank));
			Assert.That(f.Rocket.Trait<IFacing>().Facing, Is.EqualTo(new WVec(-1, 0, 0).Yaw));
		}
		[Test]
		public void PauseAndDisableClearAllStationStateAndBlockCapability()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket); f.Tick();
			var paused = f.Host.GrantCondition("paused"); var count = f.Recorder.Events.Count; f.Tick();
			Assert.That(f.Attack.Stations.All(s => s.OpportunityTarget.Type == TargetType.Invalid && s.RequestedTarget.Type == TargetType.Invalid), Is.True);
			Assert.That(f.Attack.CanFireFromAnyPort(Target.FromActor(f.Tank)), Is.False);
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(count));
			f.Host.RevokeCondition(paused); f.Tick();
			f.Host.GrantCondition("disabled"); f.Tick();
			Assert.That(f.Attack.CanFireFromAnyPort(Target.FromActor(f.Tank)), Is.False);
		}
		[Test]
		public void CapturedHostCannotResumeAnOldOwnersActivity()
		{
			var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket);
			var activity = f.Attack.GetAttackActivity(f.Host, AttackSource.Default, Target.FromActor(f.Infantry), false, true);
			activity.Tick(f.Host); f.Host.Owner = f.Enemy;
			((INotifyOwnerChanged)f.Attack).OnOwnerChanged(f.Host, f.Own, f.Enemy);
			Assert.That(activity.Tick(f.Host), Is.True);
			Assert.That(f.Attack.Stations.All(s => s.RequestedTarget.Type == TargetType.Invalid), Is.True);
		}
		[Test]
		public void InvalidGeometryFailsRulesLoadingAndOptionalGeometryIsUnrestricted()
		{
			Assert.Throws<YamlException>(() => new Fixture([WVec.Zero], [WAngle.Zero, WAngle.Zero]));
			Assert.Throws<YamlException>(() => new Fixture([WVec.Zero], yaws: [WAngle.Zero, WAngle.Zero]));
			var f = new Fixture(); f.Enter(f.Rifle);
			f.Infantry.Trait<Space>().SetCenterPosition(f.Infantry, new WPos(-2048, 0, 0));
			Assert.That(f.Attack.CanFireFromPort(0, Target.FromActor(f.Infantry)), Is.True);
			Assert.That(typeof(OpenRA.Mods.AS.Traits.AttackOpenToppedInfo).BaseType, Is.EqualTo(typeof(AttackGarrisonedInfo)));
		}
		[Test]
		public void ReadOnlyForecastDoesNotChangeStateOrRngAndScriptRepeats()
		{
			string[] Run()
			{
				var f = new Fixture(); f.Enter(f.Rifle); f.Enter(f.Rocket);
				var hash = f.Attack.StationHash; var rng = f.World.SharedRandom.Last;
				f.Attack.ForecastTargets(); f.Attack.ForecastTargets();
				Assert.That(f.Attack.StationHash, Is.EqualTo(hash)); Assert.That(f.World.SharedRandom.Last, Is.EqualTo(rng));
				for (var i = 0; i < 15; i++) { if (i == 5) f.Exit(f.Rifle); if (i == 9) f.Enter(f.Rifle); f.Tick(); }
				return f.Recorder.Events.ToArray();
			}
			Assert.That(Run(), Is.EqualTo(Run()));
		}
	}
}
