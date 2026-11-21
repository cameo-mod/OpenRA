using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	public sealed class ActorMapTriggerLifecycleTest
	{
		const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		static FieldInfo Field(object target, string name) => target.GetType().GetField(name, Fields);
		static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
		static object Get(object target, string name) => Field(target, name).GetValue(target);

		// The trigger tests need one empty spatial bin, not a live World or game.
		static ActorMap EmptyMap()
		{
			var map = (ActorMap)RuntimeHelpers.GetUninitializedObject(typeof(ActorMap));
			foreach (var name in new[] { "cellTriggers", "cellTriggerInfluence", "proximityTriggers",
				"cellTriggerSnapshot", "proximityTriggerSnapshot", "addActorPosition", "removeActorPosition",
				"actorBins", "removalBins" })
				Set(map, name, Activator.CreateInstance(Field(map, name).FieldType));

			Set(map, "info", new ActorMapInfo());
			Set(map, "rows", 1);
			Set(map, "cols", 2);
			Set(map, "actorShouldBeRemoved", new Predicate<Actor>(((HashSet<Actor>)Get(map, "removeActorPosition")).Contains));
			var influenceType = Field(map, "influence").FieldType.GetElementType();
			Set(map, "influence", Array.CreateInstance(influenceType, 1));
			var binType = Field(map, "bins").FieldType.GetElementType();
			var bins = Array.CreateInstance(binType, 2);
			bins.SetValue(Activator.CreateInstance(binType, true), 0);
			bins.SetValue(Activator.CreateInstance(binType, true), 1);
			Set(map, "bins", bins);
			return map;
		}

		static object Trigger(ActorMap map, string field, int id) => ((IDictionary)Get(map, field))[id];
		static void AddCurrentActor(object trigger, Actor actor) => ((HashSet<Actor>)Get(trigger, "currentActors")).Add(actor);
		sealed class StubSpace : IOccupySpace
		{
			public WPos Position;
			public WPos CenterPosition => Position;
			public CPos TopLeft => CPos.Zero;
			public (CPos Cell, SubCell SubCell)[] OccupiedCells() => [];
		}

		static uint nextActorId = 1;
		static Actor ActorStub()
		{
			var actor = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));
			Set(actor, "ActorID", nextActorId++);
			return actor;
		}
		static Actor ActorAt(StubSpace space)
		{
			var actor = ActorStub();
			Set(actor, "<OccupiesSpace>k__BackingField", space);
			return actor;
		}

		static Actor[] ActorsInBin(ActorMap map, int bin)
			=> ((IEnumerable)Get(((Array)Get(map, "bins")).GetValue(bin), "Actors")).Cast<Actor>().ToArray();

		static void Tick(ActorMap map) => ((ITick)map).Tick(null);

		[Test]
		public void MovingOrRemovingActorsTouchesOnlyRecordedBinsAndPreservesOrder()
		{
			var map = EmptyMap();
			var one = new StubSpace();
			var two = new StubSpace();
			var three = new StubSpace();
			var a = ActorAt(one);
			var b = ActorAt(two);
			var c = ActorAt(three);
			map.AddPosition(a, one);
			map.AddPosition(b, two);
			map.AddPosition(c, three);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.EqualTo(new[] { a, b, c }));

			two.Position = new WPos(10240, 0, 0);
			map.UpdatePosition(b, two);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.EqualTo(new[] { a, c }));
			Assert.That(ActorsInBin(map, 1), Is.EqualTo(new[] { b }));

			map.RemovePosition(a, one);
			map.RemovePosition(c, three);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.Empty);
			Assert.That(ActorsInBin(map, 1), Is.EqualTo(new[] { b }));
			Assert.That(((IDictionary)Get(map, "actorBins")).Count, Is.EqualTo(1));
		}

		[Test]
		public void DuplicateAndSameTickAddRemoveMatchRemoveAllThenAdd()
		{
			var map = EmptyMap();
			var space = new StubSpace();
			var a = ActorAt(space);
			map.AddPosition(a, space);
			Tick(map);
			map.AddPosition(a, space);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.EqualTo(new[] { a, a }));
			space.Position = new WPos(10240, 0, 0);
			map.UpdatePosition(a, space);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.Empty);
			Assert.That(ActorsInBin(map, 1), Is.EqualTo(new[] { a }));

			var other = ActorAt(new StubSpace());
			map.AddPosition(other, null);
			map.RemovePosition(other, null);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.EqualTo(new[] { other }));
			map.RemovePosition(other, null);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.Empty);
		}

		[Test]
		public void MovingActorDirtiesTriggersInBothOldAndNewBins()
		{
			var map = EmptyMap();
			var space = new StubSpace();
			var actor = ActorAt(space);
			map.AddPosition(actor, space);
			Tick(map);
			var oldExits = 0;
			var newExits = 0;
			var oldId = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => oldExits++);
			var newId = map.AddProximityTrigger(new WPos(10240, 0, 0), WDist.Zero, WDist.Zero, null, _ => newExits++);
			var oldTrigger = Trigger(map, "proximityTriggers", oldId);
			var newTrigger = Trigger(map, "proximityTriggers", newId);
			AddCurrentActor(oldTrigger, ActorStub());
			AddCurrentActor(newTrigger, ActorStub());
			Set(oldTrigger, "Dirty", false);
			Set(newTrigger, "Dirty", false);
			space.Position = new WPos(10240, 0, 0);
			map.UpdatePosition(actor, space);
			Tick(map);
			Assert.That(oldExits, Is.EqualTo(1));
			Assert.That(newExits, Is.EqualTo(1));
		}

		[Test]
		public void RemovalAlsoCleansAnActorAddedToMultipleBins()
		{
			var map = EmptyMap();
			var space = new StubSpace();
			var actor = ActorAt(space);
			map.AddPosition(actor, space);
			Tick(map);
			space.Position = new WPos(10240, 0, 0);
			map.AddPosition(actor, space);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.EqualTo(new[] { actor }));
			Assert.That(ActorsInBin(map, 1), Is.EqualTo(new[] { actor }));
			map.RemovePosition(actor, space);
			Tick(map);
			Assert.That(ActorsInBin(map, 0), Is.Empty);
			Assert.That(ActorsInBin(map, 1), Is.Empty);
			Assert.That(((IDictionary)Get(map, "actorBins")).Count, Is.Zero);
		}

		[Test]
		public void RemovedCellBeforeTickNeverFiresAndKeepsNoId()
		{
			var map = EmptyMap();
			var exits = 0;
			var id = map.AddCellTrigger([], null, _ => exits++);
			AddCurrentActor(Trigger(map, "cellTriggers", id), ActorStub());
			map.RemoveCellTrigger(id);
			map.RemoveCellTrigger(id);
			Tick(map);
			Assert.That(exits, Is.Zero, "Cell removal intentionally does not emit exits.");
			Assert.That(((IDictionary)Get(map, "cellTriggers")).Contains(id), Is.False);
		}

		[Test]
		public void ProximityRemovalDisposesOnceAndStaleUpdateCannotReindex()
		{
			var map = EmptyMap();
			var exits = 0;
			var id = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => exits++);
			AddCurrentActor(Trigger(map, "proximityTriggers", id), ActorStub());
			map.UpdateProximityTrigger(id, WPos.Zero, WDist.Zero, WDist.Zero);
			map.RemoveProximityTrigger(id);
			map.RemoveProximityTrigger(id);
			map.UpdateProximityTrigger(id, WPos.Zero, WDist.Zero, WDist.Zero);
			Tick(map);
			Assert.That(exits, Is.EqualTo(1));
			Assert.That(((IDictionary)Get(map, "proximityTriggers")).Contains(id), Is.False);
			var bin = ((Array)Get(map, "bins")).GetValue(0);
			Assert.That(((IList)Get(bin, "ProximityTriggers")).Count, Is.Zero);
		}

		[Test]
		public void CellCallbacksCanAddAndRemoveWithoutTickingRemovedOrNewEntries()
		{
			var map = EmptyMap();
			var removed = -1;
			var added = -1;
			var first = map.AddCellTrigger([], null, _ =>
			{
				map.RemoveCellTrigger(removed);
				added = map.AddCellTrigger([], null, null);
			});
			removed = map.AddCellTrigger([], null, _ => Assert.Fail("Removed trigger fired."));
			AddCurrentActor(Trigger(map, "cellTriggers", first), ActorStub());
			Tick(map);
			Assert.That(((IDictionary)Get(map, "cellTriggers")).Contains(removed), Is.False);
			Assert.That((bool)Get(Trigger(map, "cellTriggers", added), "Dirty"), Is.True);
			Tick(map);
			Assert.That((bool)Get(Trigger(map, "cellTriggers", added), "Dirty"), Is.False);
		}

		[Test]
		public void ProximityCallbacksPreserveSurvivorOrderAndDeferNewTriggers()
		{
			var map = EmptyMap();
			var calls = new List<string>();
			var removed = -1;
			var added = -1;
			var first = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ =>
			{
				calls.Add("first");
				map.RemoveProximityTrigger(removed);
				added = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, null);
			});
			removed = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, null);
			var survivor = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => calls.Add("survivor"));
			AddCurrentActor(Trigger(map, "proximityTriggers", first), ActorStub());
			AddCurrentActor(Trigger(map, "proximityTriggers", survivor), ActorStub());
			Tick(map);
			Assert.That(calls, Is.EqualTo(new[] { "first", "survivor" }));
			Assert.That(((IDictionary)Get(map, "proximityTriggers")).Contains(removed), Is.False);
			Assert.That((bool)Get(Trigger(map, "proximityTriggers", added), "Dirty"), Is.True);
			Tick(map);
			Assert.That((bool)Get(Trigger(map, "proximityTriggers", added), "Dirty"), Is.False);
		}
	}
}
