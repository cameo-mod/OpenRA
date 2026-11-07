using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	public sealed class ProductionQueueEvictionReceiptTest
	{
		static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		static void Set(object obj, string name, object value)
		{
			for (var type = obj.GetType(); type != null; type = type.BaseType)
				if (type.GetField(name, InstanceFields | BindingFlags.DeclaredOnly) is FieldInfo field)
				{
					field.SetValue(obj, value);
					return;
				}

			throw new MissingFieldException(obj.GetType().FullName, name);
		}

		static T Info<T>(params (string Key, object Value)[] fields) where T : new()
		{
			var info = new T();
			foreach (var (key, value) in fields)
				Set(info, key, value);

			return info;
		}

		sealed class TestQueue : ProductionQueue
		{
			public IEnumerable<ActorInfo> Buildable = [];

			public TestQueue(ActorInitializer init, ProductionQueueInfo info)
				: base(init, info) { }

			public override IEnumerable<ActorInfo> BuildableItems() => Buildable;
			public void CancelUnbuildable() => CancelUnbuildableItems();
			public void AddForTest(ProductionItem item) => Queue.Add(item);
			public void SetPreviousBuildableNames(params string[] names)
				=> Set(this, "lastBuildableNames", names.ToHashSet());
			public void ClearForLifecycle() => ClearQueue();
			public void NotifyKilled() => ((INotifyKilled)this).Killed(Actor, default);
		}

		sealed class Fixture : IDisposable
		{
			readonly Sound previousSound = Game.Sound;
			public readonly TestQueue Queue;
			public readonly Player Owner;
			public readonly PlayerResources Resources;
			public readonly ActorInfo OldInfo;
			public readonly ActorInfo ReplacementInfo;

			public Fixture()
			{
				Game.Sound = (Sound)RuntimeHelpers.GetUninitializedObject(typeof(Sound));
				Owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
				Set(Owner, "Faction", Info<FactionInfo>((nameof(FactionInfo.InternalName), "test")));

				var queueInfo = Info<ProductionQueueInfo>((nameof(ProductionQueueInfo.Type), "Infantry"),
					(nameof(ProductionQueueInfo.CompleteUpgradedInProgress), false));
				var oldBuildable = Info<BuildableInfo>((nameof(BuildableInfo.Queue), new[] { "Infantry" }.ToFrozenSet()));
				var replacementBuildable = Info<BuildableInfo>((nameof(BuildableInfo.Queue), new[] { "Infantry" }.ToFrozenSet()));
			var replacedInQueue = Info<ReplacedInQueueInfo>((nameof(ReplacedInQueueInfo.Actors), new HashSet<string> { "replacement" }));
			OldInfo = new ActorInfo("old", oldBuildable, replacedInQueue);
			ReplacementInfo = new ActorInfo("replacement", replacementBuildable);
			var rules = new Ruleset(new Dictionary<string, ActorInfo>
				{
					[OldInfo.Name] = OldInfo,
					[ReplacementInfo.Name] = ReplacementInfo
				}, new Dictionary<string, OpenRA.GameRules.WeaponInfo>(), null, null, null, null, null, null);
			var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
			typeof(Map).GetProperty(nameof(Map.Rules)).SetValue(map, rules);
			var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
			Set(world, "Map", map);
			typeof(World).GetProperty(nameof(World.WorldTick)).SetValue(world, 100);
			var actor = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));
			Set(actor, "World", world);
			typeof(Actor).GetProperty(nameof(Actor.Owner)).SetValue(actor, Owner);

			Queue = new TestQueue(new ActorInitializer(actor, new TypeDictionary()), queueInfo);
			Resources = (PlayerResources)RuntimeHelpers.GetUninitializedObject(typeof(PlayerResources));
			Resources.Cash = 50;
			Resources.Resources = 5;
			Resources.ResourceCapacity = 100;
			Set(Queue, "playerResources", Resources);
		}

			public ProductionItem NewItem(bool started = true)
			{
				var item = new ProductionItem(Queue, "old", 100, null, null)
				{
					Started = started,
					ResourcesPaid = 10,
					RemainingCost = 40
				};
				return item;
			}

			public void Dispose() => Game.Sound = previousSound;
		}

		[Test]
		public void ActualUnbuildableCancellationRefundsAndRecordsOnlyRemovedItem()
		{
			using var f = new Fixture();
			var item = f.NewItem();
			f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");

			f.Queue.CancelUnbuildable();

			Assert.That(f.Queue.AllQueued(), Is.Empty);
			Assert.That(f.Resources.Resources, Is.EqualTo(15));
			Assert.That(f.Resources.Cash, Is.EqualTo(100));
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 1, 100), Is.True);
			Assert.That(f.Queue.TryConsumeUnbuildableEviction(item, f.Owner, 1, 100), Is.True);
			Assert.That(f.Queue.TryConsumeUnbuildableEviction(item, f.Owner, 1, 100), Is.False);
		}

		[Test]
		public void UpgradeMigrationDoesNotCreateEvictionReceipt()
		{
			using var f = new Fixture();
			var item = f.NewItem(started: false);
			f.Queue.AddForTest(item);
			f.Queue.Buildable = [f.ReplacementInfo];
			f.Queue.SetPreviousBuildableNames("old");

			f.Queue.CancelUnbuildable();

			Assert.That(f.Queue.AllQueued().Select(i => i.Item), Is.EqualTo(new[] { "replacement" }));
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 1, 100), Is.False);
			Assert.That(f.Resources.Cash, Is.EqualTo(50));
		}

		[Test]
		public void UnchangedBuildableSetAndNoOpEndProductionCreateNoReceipt()
		{
			using var f = new Fixture();
			var item = f.NewItem();
			f.Queue.AddForTest(item);
			f.Queue.Buildable = [f.OldInfo];
			f.Queue.SetPreviousBuildableNames("old");

			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.AllQueued(), Is.EqualTo(new[] { item }));
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 1, 100), Is.False);

			var infiniteItem = f.NewItem();
			infiniteItem.Infinite = true;
			f.Queue.EndProduction(infiniteItem);
			Assert.That(f.Queue.AllQueued().Count(), Is.EqualTo(2));
			Assert.That(f.Queue.AllQueued().Last().Infinite, Is.True);
			Assert.That(f.Queue.TryBindUnbuildableEviction(infiniteItem, f.Owner, 1, 100), Is.False);
		}

		[Test]
		public void DuplicateEvictionRetiresOldCandidateAndFailsClosed()
		{
			using var f = new Fixture();
			var item = f.NewItem();
			f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 2, 100), Is.True);

			f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");
			typeof(World).GetProperty(nameof(World.WorldTick)).SetValue(f.Queue.Actor.World, 101);
			f.Queue.CancelUnbuildable();

			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 2, 101), Is.False);
		}

		[Test]
		public void LifecycleOwnerRoundTripAndEpisodeChangeInvalidateReceipt()
		{
			using var f = new Fixture();
			var item = f.NewItem();
			f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 3, 100), Is.True);

			f.Queue.NotifyKilled();
			var otherOwner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
			typeof(Actor).GetProperty(nameof(Actor.Owner)).SetValue(f.Queue.Actor, otherOwner);
			typeof(Actor).GetProperty(nameof(Actor.Owner)).SetValue(f.Queue.Actor, f.Owner);
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 3, 101), Is.False);

			var nextItem = f.NewItem();
			f.Queue.AddForTest(nextItem);
			f.Queue.SetPreviousBuildableNames("old");
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(nextItem, f.Owner, 3, 101), Is.True);
			Assert.That(f.Queue.TryBindUnbuildableEviction(nextItem, f.Owner, 4, 101), Is.False);
		}

		[Test]
		public void ReceiptBindsAt64ExpiresAt65AndRetirementIsExplicit()
		{
			using var f = new Fixture();
			var item = f.NewItem();
			f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(item, f.Owner, 5, 164), Is.True);
			Assert.That(f.Queue.RetireUnbuildableEviction(item, 164), Is.True);
			Assert.That(f.Queue.TryConsumeUnbuildableEviction(item, f.Owner, 5, 164), Is.False);

			var expiredItem = f.NewItem();
			f.Queue.AddForTest(expiredItem);
			f.Queue.SetPreviousBuildableNames("old");
			typeof(World).GetProperty(nameof(World.WorldTick)).SetValue(f.Queue.Actor.World, 200);
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(expiredItem, f.Owner, 5, 265), Is.False);
		}

		[Test]
		public void OverflowDoesNotOverwriteLiveReceiptsAndCapacityRecovers()
		{
			using var f = new Fixture();
			var items = Enumerable.Range(0, 1025).Select(_ => f.NewItem()).ToArray();
			foreach (var item in items)
				f.Queue.AddForTest(item);
			f.Queue.SetPreviousBuildableNames("old");
			f.Queue.CancelUnbuildable();

			// Cancellation walks the queue in reverse: the last 1,024 items are retained,
			// while the first over-capacity item remains deliberately unbacked.
			Assert.That(f.Queue.TryBindUnbuildableEviction(items[0], f.Owner, 6, 100), Is.False);
			Assert.That(f.Queue.TryBindUnbuildableEviction(items[1], f.Owner, 6, 100), Is.True);

			var next = f.NewItem();
			f.Queue.AddForTest(next);
			f.Queue.SetPreviousBuildableNames("old");
			typeof(World).GetProperty(nameof(World.WorldTick)).SetValue(f.Queue.Actor.World, 165);
			f.Queue.CancelUnbuildable();
			Assert.That(f.Queue.TryBindUnbuildableEviction(next, f.Owner, 6, 165), Is.True);
		}
	}
}
