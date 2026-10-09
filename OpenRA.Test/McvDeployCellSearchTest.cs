using System.Collections.Generic;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	public class McvDeployCellSearchTest
	{
		[Test]
		public void LegacySearchRemainsDefault()
		{
			Assert.That(new McvExpansionManagerBotModuleInfo().SkipUnreachableDeployCells, Is.False);
		}

		[Test]
		public void FirstPassSkipsUnreachablePlaceableCandidate()
		{
			var blocked = new CPos(1, 0);
			var reachable = new CPos(2, 0);
			var result = McvDeployCellSearch.Find(new[] { blocked, reachable }, new CPos(20, 0), CPos.Zero,
				8, _ => true, c => c == reachable, out var tally);
			Assert.That(result, Is.EqualTo(reachable));
			Assert.That(tally, Is.EqualTo(new McvDeployCellTally(2, 2, 1)));
		}

		[Test]
		public void FallbackSkipsUnreachableNearestAndKeepsSearching()
		{
			var weighted = new CPos(15, 0);
			var nearest = new CPos(1, 0);
			var next = new CPos(2, 0);
			var checks = new Dictionary<CPos, int>();
			var result = McvDeployCellSearch.Find(new[] { weighted, nearest, next }, new CPos(30, 0), CPos.Zero,
				8, _ => true, c => { checks[c] = checks.GetValueOrDefault(c) + 1; return c != nearest; }, out var tally);
			Assert.That(result, Is.EqualTo(next));
			Assert.That(tally.Unreachable, Is.EqualTo(1));
			Assert.That(checks.Values, Is.All.EqualTo(1));
		}

		[Test]
		public void UnreachableFallbackDoesNotDiscardValidWeightedCandidate()
		{
			var weighted = new CPos(15, 0);
			var nearest = new CPos(1, 0);
			var result = McvDeployCellSearch.Find(new[] { weighted, nearest }, new CPos(30, 0), CPos.Zero,
				8, _ => true, c => c == weighted, out var tally);
			Assert.That(result, Is.EqualTo(weighted));
			Assert.That(tally, Is.EqualTo(new McvDeployCellTally(2, 2, 1)));
		}

		[TestCase(true)]
		[TestCase(false)]
		public void FailedSweepTalliesAllUniqueRejections(bool placementRejected)
		{
			var cell = new CPos(1, 0);
			var pathCalls = 0;
			var result = McvDeployCellSearch.Find(new[] { cell, cell, new CPos(2, 0) }, CPos.Zero, CPos.Zero,
				8, _ => !placementRejected, _ => { pathCalls++; return false; }, out var tally);
			Assert.That(result, Is.Null);
			Assert.That(tally.Scanned, Is.EqualTo(2));
			Assert.That(tally.PlacementRejected, Is.EqualTo(placementRejected ? 2 : 0));
			Assert.That(tally.Unreachable, Is.EqualTo(placementRejected ? 0 : 2));
			Assert.That(pathCalls, Is.EqualTo(placementRejected ? 0 : 2));
		}

		[Test]
		public void EqualSourceTargetPreservesSuppliedShuffleOrder()
		{
			var first = new CPos(15, 0);
			var result = McvDeployCellSearch.Find(new[] { first, new CPos(1, 0) }, CPos.Zero, CPos.Zero,
				8, _ => true, _ => true, out var tally);
			Assert.That(result, Is.EqualTo(first));
			Assert.That(tally.Scanned, Is.EqualTo(1));
		}

		[Test]
		public void EmptyAnnulusIsAnEmptyFailedSweep()
		{
			var result = McvDeployCellSearch.Find(System.Array.Empty<CPos>(), CPos.Zero, CPos.Zero,
				8, _ => true, _ => true, out var tally);
			Assert.That(result, Is.Null);
			Assert.That(tally, Is.EqualTo(new McvDeployCellTally(0, 0, 0)));
		}
	}
}
