from pathlib import Path
p=Path('OpenRA.Test/OpenRA.Mods.Common/AttackGarrisonedTest.cs')
license=Path('OpenRA.Mods.Common/Traits/Attack/AttackGarrisoned.cs').read_text().split('using System;')[0]
p.write_text(license+'''using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
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
		sealed class ProviderInfo : TraitInfo<Provider> { }
		sealed class Provider : IFirePortOccupantProvider
		{
			public readonly List<Actor> Actors = [];
			public IEnumerable<Actor> Occupants => Actors;
		}
		sealed class RecorderInfo : TraitInfo<Recorder> { }
		sealed class Recorder : INotifyAttack
		{
			public readonly List<string> Events = [];
			public void PreparingAttack(Actor self, in Target target, Armament armament, Barrel barrel) { }
			public void Attacking(Actor self, in Target target, Armament armament, Barrel barrel) =>
				Events.Add($"{self.ActorID}:{armament.Actor.ActorID}:{target.Actor.ActorID}:{armament.Actor.CenterPosition}:{armament.Actor.Orientation.Yaw.Angle}");
		}
		sealed class Fixture
		{
			public readonly World World;
			public readonly Player Own, Enemy;
			public readonly Actor Host, Rifle, Rocket, Infantry, Tank;
			public readonly AttackGarrisoned Attack;
			public readonly Provider Provider;
			public readonly Recorder Recorder;
			public Fixture(WVec[] offsets = null, WAngle[] cones = null, WAngle[] yaws = null)
			{
				Own = Player("own", "enemy"); Enemy = Player("enemy", "own");
				var hostInfo = Info<AttackGarrisonedInfo>(("PortOffsets", offsets ?? new[] { WVec.Zero, WVec.Zero }), ("PortCones", cones), ("PortYaws", yaws));
				var body = Info<BodyOrientationInfo>(("QuantizedFacings", 0), ("UseClassicPerspectiveFudge", false));
				ActorInfo Passenger(string name, string weapon, string prefer) => new(name,
					new SpaceInfo(), body, new AttackFollowInfo(), Info<ArmamentInfo>(("Weapon", weapon)),
					new AutoTargetInfo(), Info<AutoTargetPriorityInfo>(("ValidTargets", new BitSet<TargetableType>(prefer)), ("Priority", 10)),
					Info<AutoTargetPriorityInfo>(("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle")), ("Priority", 1)));
				var actors = new Dictionary<string, ActorInfo>
				{
					["host"] = new("host", new SpaceInfo(), body, hostInfo, new ProviderInfo(), new RecorderInfo(), new AutoTargetInfo()),
					["rifle"] = Passenger("rifle", "rifle", "Infantry"),
					["rocket"] = Passenger("rocket", "rocket", "Vehicle"),
					["infantry"] = new("infantry", new SpaceInfo(), Info<TargetableInfo>(("TargetTypes", new BitSet<TargetableType>("Infantry")))),
					["tank"] = new("tank", new SpaceInfo(), Info<TargetableInfo>(("TargetTypes", new BitSet<TargetableType>("Vehicle")))),
				};
				var weapons = new Dictionary<string, OpenRA.GameRules.WeaponInfo>
				{
					["rifle"] = Info<OpenRA.GameRules.WeaponInfo>(("Range", new WDist(4096)), ("ReloadDelay", 3), ("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle"))),
					["rocket"] = Info<OpenRA.GameRules.WeaponInfo>(("Range", new WDist(8192)), ("ReloadDelay", 3), ("ValidTargets", new BitSet<TargetableType>("Infantry", "Vehicle"))),
				};
				var rules = new Ruleset(actors, weapons, null, null, null, null, null, null);
				World = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
				Set(World, "TraitDict", new TraitDictionary());
				var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
				typeof(Map).GetProperty("Rules").SetValue(map, rules);
				Set(World, "Map", map);
				Set(World, "SharedRandom", new MersenneTwister(17));
				var index = DispatchProxy.Create<IActorMap, SpatialIndex>();
				Set(World, "ActorMap", index);
				Host = Create("host", Own, WPos.Zero);
				Rifle = Create("rifle", Own, WPos.Zero);
				Rocket = Create("rocket", Own, WPos.Zero);
				Infantry = Create("infantry", Enemy, new WPos(2048, 0, 0));
				Tank = Create("tank", Enemy, new WPos(3072, 0, 0));
				((SpatialIndex)(object)index).Targets.AddRange([Infantry, Tank]);
				Attack = Host.Trait<AttackGarrisoned>();
				Provider = Host.Trait<Provider>(); Recorder = Host.Trait<Recorder>();
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
				Provider.Actors.Add(actor);
				if (cargo) ((INotifyPassengerEntered)Attack).OnPassengerEntered(Host, actor);
				else ((INotifyFirePortOccupantEntered)Attack).OnFirePortOccupantEntered(Host, actor);
			}
			public void Exit(Actor actor, bool cargo = false)
			{
				Provider.Actors.Remove(actor);
				if (cargo) ((INotifyPassengerExited)Attack).OnPassengerExited(Host, actor);
				else ((INotifyFirePortOccupantExited)Attack).OnFirePortOccupantExited(Host, actor);
			}
			public void Tick()
			{
				((ITick)Attack).Tick(Host);
				foreach (var actor in new[] { Rifle, Rocket })
					foreach (var arm in actor.TraitsImplementing<Armament>()) ((ITick)arm).Tick(actor);
			}
		}

		[TestCase(false)]
		[TestCase(true)]
		public void IndependentPrioritiesFireSimultaneouslyWithoutPortRng(bool cargo)
		{
			var f = new Fixture(); f.Enter(f.Rifle, cargo); f.Enter(f.Rocket, cargo);
			var rng = f.World.SharedRandom.Last;
			f.Tick();
			Assert.That(f.Attack.Stations.Select(s => s.OpportunityTarget.Actor), Is.EqualTo(new[] { f.Infantry, f.Tank }));
			Assert.That(f.Recorder.Events.Count, Is.EqualTo(2), "One host notification per actual fire");
			Assert.That(f.World.SharedRandom.Last, Is.EqualTo(rng));
			Assert.That(f.Host.Trait<AutoTarget>().ActiveAttackBases, Is.Empty);
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
''',encoding='utf-8',newline='\n')
