using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	public class ProductionQueueEvictionReceiptTest
	{
		static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

		static (ProductionQueue Queue, Player Owner) CreateQueue()
		{
			var queue = (ProductionQueue)RuntimeHelpers.GetUninitializedObject(typeof(ProductionQueue));
			var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
			var actor = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));
			typeof(Actor).GetProperty(nameof(Actor.Owner)).SetValue(actor, owner);
			typeof(ProductionQueue).GetField("<Actor>k__BackingField", InstanceNonPublic).SetValue(queue, actor);

			var receiptsField = typeof(ProductionQueue).GetField("unbuildableEvictionReceipts", InstanceNonPublic);
			receiptsField.SetValue(queue, Activator.CreateInstance(receiptsField.FieldType));
			return (queue, owner);
		}

		static ProductionItem NewItem()
			=> (ProductionItem)RuntimeHelpers.GetUninitializedObject(typeof(ProductionItem));

		static void Record(ProductionQueue queue, ProductionItem item, Player owner, int tick)
		{
			var method = typeof(ProductionQueue).GetMethod("RecordUnbuildableEviction", InstanceNonPublic);
			method.Invoke(queue, new object[] { item, owner, tick });
		}

		[Test]
		public void ReceiptBindsAtAge64AndCanBeConsumedOnlyOnce()
		{
			var (queue, owner) = CreateQueue();
			var item = NewItem();
			Record(queue, item, owner, 100);

			Assert.That(queue.TryBindUnbuildableEviction(item, owner, 7, 164), Is.True);
			Assert.That(queue.TryConsumeUnbuildableEviction(item, owner, 7, 164), Is.True);
			Assert.That(queue.TryConsumeUnbuildableEviction(item, owner, 7, 164), Is.False);
		}

		[Test]
		public void ReceiptExpiresAtAge65()
		{
			var (queue, owner) = CreateQueue();
			var item = NewItem();
			Record(queue, item, owner, 100);

			Assert.That(queue.TryBindUnbuildableEviction(item, owner, 7, 165), Is.False);
		}

		[Test]
		public void FullReceiptStoreDoesNotOverwriteAndRecoversAfterExpiry()
		{
			var (queue, owner) = CreateQueue();
			var items = Enumerable.Range(0, 1025).Select(_ => NewItem()).ToArray();
			for (var i = 0; i < 1024; i++)
				Record(queue, items[i], owner, 100);

			Record(queue, items[1024], owner, 101);
			Assert.That(queue.TryBindUnbuildableEviction(items[1024], owner, 8, 101), Is.False);
			Assert.That(queue.TryBindUnbuildableEviction(items[0], owner, 8, 101), Is.True);
			Assert.That(queue.TryConsumeUnbuildableEviction(items[0], owner, 8, 101), Is.True);

			Record(queue, items[1024], owner, 165);
			Assert.That(queue.TryBindUnbuildableEviction(items[1024], owner, 8, 165), Is.True);
		}

		[Test]
		public void ReceiptCannotBeLookedUpThroughAnotherQueue()
		{
			var (queue, owner) = CreateQueue();
			var (otherQueue, _) = CreateQueue();
			var item = NewItem();
			Record(queue, item, owner, 100);

			Assert.That(otherQueue.TryBindUnbuildableEviction(item, owner, 7, 101), Is.False);
			Assert.That(queue.TryBindUnbuildableEviction(item, owner, 7, 101), Is.True);
		}

		[Test]
		public void WrongOwnerRetiresCandidate()
		{
			var (queue, owner) = CreateQueue();
			var wrongOwner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
			var item = NewItem();
			Record(queue, item, owner, 100);

			Assert.That(queue.TryBindUnbuildableEviction(item, wrongOwner, 7, 101), Is.False);
			Assert.That(queue.TryBindUnbuildableEviction(item, owner, 7, 101), Is.False);
		}
	}
}
