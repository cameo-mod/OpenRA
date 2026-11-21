using System;
using System.Collections;
using System.Collections.Generic;
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
				"cellTriggerOrder", "proximityTriggerOrder", "cellTriggerSnapshot", "proximityTriggerSnapshot",
				"addActorPosition", "removeActorPosition" })
				Set(map, name, Activator.CreateInstance(Field(map, name).FieldType));

			Set(map, "info", new ActorMapInfo());
			Set(map, "rows", 1);
			Set(map, "cols", 1);
			Set(map, "actorShouldBeRemoved", (Predicate<Actor>)(_ => false));
			var influenceType = Field(map, "influence").FieldType.GetElementType();
			Set(map, "influence", Array.CreateInstance(influenceType, 1));
			var binType = Field(map, "bins").FieldType.GetElementType();
			var bins = Array.CreateInstance(binType, 1);
			bins.SetValue(Activator.CreateInstance(binType, true), 0);
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

		static Actor ActorInBin(ActorMap map, WPos position)
		{
			var actor = ActorStub();
			Set(actor, "<OccupiesSpace>k__BackingField", new StubSpace { Position = position });
			Set(actor, "<IsInWorld>k__BackingField", true);
			var bin = ((Array)Get(map, "bins")).GetValue(0);
			((IList)Get(bin, "Actors")).Add(actor);
			return actor;
		}

		static void Tick(ActorMap map) => ((ITick)map).Tick(null);

		[Test]
		public void CellRegistrationOrderSurvivesRemovalAndLaterAddition()
		{
			var map = EmptyMap();
			var order = new List<string>();
			var a = map.AddCellTrigger([], null, _ => order.Add("A"));
			var b = map.AddCellTrigger([], null, _ => order.Add("B"));
			var c = map.AddCellTrigger([], null, _ => order.Add("C"));
			map.RemoveCellTrigger(b);
			var d = map.AddCellTrigger([], null, _ => order.Add("D"));
			foreach (var id in new[] { a, c, d })
				AddCurrentActor(Trigger(map, "cellTriggers", id), ActorStub());
			Tick(map);
			Assert.That(order, Is.EqualTo(new[] { "A", "C", "D" }));
		}

		[Test]
		public void ProximityRegistrationOrderSurvivesRemovalAndLaterAddition()
		{
			var map = EmptyMap();
			var order = new List<string>();
			var a = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => order.Add("A"));
			var b = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => order.Add("B"));
			var c = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => order.Add("C"));
			map.RemoveProximityTrigger(b);
			var d = map.AddProximityTrigger(WPos.Zero, WDist.Zero, WDist.Zero, null, _ => order.Add("D"));
			foreach (var id in new[] { a, c, d })
				AddCurrentActor(Trigger(map, "proximityTriggers", id), ActorStub());
			Tick(map);
			Assert.That(order, Is.EqualTo(new[] { "A", "C", "D" }));
		}

		[Test]
		public void SelfRemovalExitsOnlyPreviouslyActiveAndNotifiedEntrants()
		{
			var map = EmptyMap();
			var old = ActorStub();
			var entered = new List<Actor>();
			var exited = new List<Actor>();
			var b = ActorInBin(map, WPos.Zero);
			var c = ActorInBin(map, new WPos(100, 0, 0));
			var id = -1;
			id = map.AddProximityTrigger(WPos.Zero, new WDist(1024), WDist.Zero,
				a => { entered.Add(a); map.RemoveProximityTrigger(id); }, a => exited.Add(a));
			AddCurrentActor(Trigger(map, "proximityTriggers", id), old);
			Tick(map);
			Assert.That(entered, Is.EqualTo(new[] { b }));
			Assert.That(exited, Is.EquivalentTo(new[] { old, b }));
			Assert.That(exited, Does.Not.Contain(c));
			Tick(map);
			Assert.That(exited.Count, Is.EqualTo(2));
		}

		[Test]
		public void LiveSelfUpdateRemainsDirtyUntilNextTick()
		{
			var map = EmptyMap();
			var actor = ActorInBin(map, WPos.Zero);
			var exits = 0;
			var id = -1;
			id = map.AddProximityTrigger(WPos.Zero, new WDist(1024), WDist.Zero,
				_ => map.UpdateProximityTrigger(id, new WPos(8192, 0, 0), new WDist(1024), WDist.Zero),
				_ => exits++);
			Tick(map);
			Assert.That((bool)Get(Trigger(map, "proximityTriggers", id), "Dirty"), Is.True);
			Assert.That(((HashSet<Actor>)Get(Trigger(map, "proximityTriggers", id), "currentActors")).Contains(actor), Is.True);
			Tick(map);
			Assert.That(exits, Is.EqualTo(1));
			Assert.That((bool)Get(Trigger(map, "proximityTriggers", id), "Dirty"), Is.False);
		}

		[Test]
		public void CellCallbackDirtyRemainsUntilNextTick()
		{
			var map = EmptyMap();
			var exits = 0;
			var id = -1;
			id = map.AddCellTrigger([], null, _ =>
			{
				exits++;

				// AddInfluence and RemoveInfluence dirty a cell trigger exactly like this.
				Set(Trigger(map, "cellTriggers", id), "Dirty", true);
			});
			AddCurrentActor(Trigger(map, "cellTriggers", id), ActorStub());
			Tick(map);
			Assert.That(exits, Is.EqualTo(1));
			Assert.That((bool)Get(Trigger(map, "cellTriggers", id), "Dirty"), Is.True, "A dirty flag set by a callback must survive the tick.");
			Tick(map);
			Assert.That(exits, Is.EqualTo(1));
			Assert.That((bool)Get(Trigger(map, "cellTriggers", id), "Dirty"), Is.False);
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

			// Without a member the removed trigger could never fire, so the guard would go untested.
			AddCurrentActor(Trigger(map, "cellTriggers", removed), ActorStub());
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
