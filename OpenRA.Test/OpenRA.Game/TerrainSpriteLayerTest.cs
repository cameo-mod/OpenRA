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

using System.IO;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class TerrainSpriteLayerTest
	{
		sealed class StubVertexBuffer : IVertexBuffer<Vertex>
		{
			public void Bind() { }
			public void SetData(Vertex[] vertices, int length) { }
			public void SetData(ref Vertex[] vertices, int length) { }
			public void SetData(Vertex[] vertices, int offset, int start, int length) { }
			public void Dispose() { }
		}

		static TerrainSpriteLayer.Batch NewBatch()
		{
			return new TerrainSpriteLayer.Batch(4, false, count => new StubVertexBuffer());
		}

		static Sheet NewSheet()
		{
			return new Sheet(SheetType.Indexed, new Size(1, 1));
		}

		[TestCase(TestName = "Batch accepts distinct sheets up to the sampler limit")]
		public void BatchFillsToSheetLimit()
		{
			var batch = NewBatch();
			for (var i = 0; i < SpriteRenderer.SheetCount; i++)
			{
				var sheet = NewSheet();
				Assert.That(batch.CanAdd(sheet, null), Is.True);
				Assert.That(batch.GetOrAddSheetIndex(sheet), Is.EqualTo(i));
			}

			Assert.That(batch.SheetCount, Is.EqualTo(SpriteRenderer.SheetCount));
		}

		[TestCase(TestName = "Batch rejects a sheet past the sampler limit instead of overflowing")]
		public void BatchRejectsOverflowSheet()
		{
			var batch = NewBatch();
			for (var i = 0; i < SpriteRenderer.SheetCount; i++)
				batch.GetOrAddSheetIndex(NewSheet());

			var extra = NewSheet();
			Assert.That(batch.CanAdd(extra, null), Is.False);
			Assert.Throws<InvalidDataException>(() => batch.GetOrAddSheetIndex(extra));
		}

		[TestCase(TestName = "Batch counts each required sheet once including the secondary")]
		public void BatchCountsSecondarySheetOnce()
		{
			var batch = NewBatch();
			var primary = NewSheet();
			var secondary = NewSheet();

			Assert.That(batch.NewSheetCount(primary, secondary), Is.EqualTo(2));
			Assert.That(batch.NewSheetCount(primary, primary), Is.EqualTo(1));
			Assert.That(batch.NewSheetCount(primary, null), Is.EqualTo(1));
			Assert.That(batch.NewSheetCount(null, null), Is.EqualTo(0));

			batch.GetOrAddSheetIndex(primary);
			Assert.That(batch.NewSheetCount(primary, secondary), Is.EqualTo(1));
			Assert.That(batch.NewSheetCount(primary, null), Is.EqualTo(0));

			batch.GetOrAddSheetIndex(secondary);
			Assert.That(batch.NewSheetCount(primary, secondary), Is.EqualTo(0));
			Assert.That(batch.CanAdd(NewSheet(), NewSheet()), Is.True);
		}

		[TestCase(TestName = "Batch keeps null sheet at index zero without consuming capacity")]
		public void BatchNullSheetIsFree()
		{
			var batch = NewBatch();
			for (var i = 0; i < SpriteRenderer.SheetCount; i++)
				batch.GetOrAddSheetIndex(NewSheet());

			Assert.That(batch.CanAdd(null, null), Is.True);
			Assert.That(batch.GetOrAddSheetIndex(null), Is.EqualTo(0));
			Assert.That(batch.SheetCount, Is.EqualTo(SpriteRenderer.SheetCount));
		}
	}
}
