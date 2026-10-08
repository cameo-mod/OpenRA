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
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace OpenRA.Graphics
{
	public sealed class TerrainSpriteLayer : IDisposable
	{
		// PERF: we can reuse the IndexBuffer as all layers have the same size.
		static readonly Lock IndexBuffersLock = new();
		static readonly ConditionalWeakTable<World, IndexBufferRc> IndexBuffers = [];
		readonly IndexBufferRc indexBufferWrapper;

		public readonly BlendMode BlendMode;

		// The GPU sampler limit caps each draw at SpriteRenderer.SheetCount sheets,
		// but a layer may reference more distinct sheets across the whole map.
		// Cells are partitioned into batches that each fit within the limit.
		sealed class Batch : IDisposable
		{
			public readonly Sheet[] Sheets = new Sheet[SpriteRenderer.SheetCount];
			public int SheetCount;
			public readonly Vertex[] Vertices;
			public readonly IVertexBuffer<Vertex> VertexBuffer;
			public readonly HashSet<int> DirtyRows = [];
			public readonly bool[] IgnoreTint;

			public Batch(int vertexCount, bool lighting)
			{
				Vertices = new Vertex[vertexCount];
				VertexBuffer = Game.Renderer.Context.CreateEmptyVertexBuffer<Vertex>(vertexCount);
				if (lighting)
					IgnoreTint = new bool[vertexCount];
			}

			public int GetOrAddSheetIndex(Sheet sheet)
			{
				if (sheet == null)
					return 0;

				for (var i = 0; i < SheetCount; i++)
					if (Sheets[i] == sheet)
						return i;

				if (SheetCount >= Sheets.Length)
					throw new InvalidDataException("Sheet overflow");

				Sheets[SheetCount] = sheet;
				return SheetCount++;
			}

			public bool Contains(Sheet sheet)
			{
				for (var i = 0; i < SheetCount; i++)
					if (Sheets[i] == sheet)
						return true;

				return false;
			}

			public int NewSheetCount(Sheet sheet, Sheet secondary)
			{
				var needed = 0;
				if (sheet != null && !Contains(sheet))
					needed++;

				if (secondary != null && secondary != sheet && !Contains(secondary))
					needed++;

				return needed;
			}

			public bool CanAdd(Sheet sheet, Sheet secondary)
			{
				return SheetCount + NewSheetCount(sheet, secondary) <= Sheets.Length;
			}

			public void Dispose()
			{
				VertexBuffer.Dispose();
			}
		}

		readonly List<Batch> batches = [];
		readonly int[] batchForCell;
		readonly Sprite emptySprite;

		readonly int indexRowStride;
		readonly int vertexRowStride;
		readonly bool restrictToBounds;

		readonly WorldRenderer worldRenderer;
		readonly Map map;

		readonly PaletteReference[] palettes;

		public TerrainSpriteLayer(World world, WorldRenderer wr, Sprite emptySprite, BlendMode blendMode, bool restrictToBounds)
		{
			worldRenderer = wr;
			this.restrictToBounds = restrictToBounds;
			this.emptySprite = emptySprite;
			BlendMode = blendMode;
			map = world.Map;

			vertexRowStride = 4 * map.MapSize.Width;
			indexRowStride = 6 * map.MapSize.Width;
			batchForCell = new int[map.MapSize.Width * map.MapSize.Height];
			Array.Fill(batchForCell, -1);
			batches.Add(new Batch(vertexRowStride * map.MapSize.Height, wr.TerrainLighting != null));

			lock (IndexBuffersLock)
			{
				indexBufferWrapper = IndexBuffers.GetValue(world, world => new IndexBufferRc(world));
				indexBufferWrapper.AddRef();
			}

			palettes = new PaletteReference[map.MapSize.Width * map.MapSize.Height];
			wr.PaletteInvalidated += UpdatePaletteIndices;

			if (wr.TerrainLighting != null)
				wr.TerrainLighting.CellChanged += UpdateTint;
		}

		void UpdatePaletteIndices()
		{
			foreach (var batch in batches)
			{
				for (var i = 0; i < batch.Vertices.Length; i++)
				{
					var v = batch.Vertices[i];
					var p = palettes[i / 4]?.TextureIndex ?? 0;
					var c = (uint)((p & 0xFFFF) << 16) | (v.C & 0xFFFF);
					batch.Vertices[i] = new Vertex(v.X, v.Y, v.Z, v.S, v.T, v.U, v.V, c, v.R, v.G, v.B, v.A);
				}

				for (var row = 0; row < map.MapSize.Height; row++)
					batch.DirtyRows.Add(row);
			}
		}

		public void Clear(CPos cell)
		{
			Update(cell, null, null, 1f, 1f, true);
		}

		public void Update(CPos cell, ISpriteSequence sequence, PaletteReference palette, int frame)
		{
			Update(cell, sequence.GetSprite(frame), palette, sequence.Scale, sequence.GetAlpha(frame), sequence.IgnoreWorldTint);
		}

		public void Update(CPos cell, Sprite sprite, PaletteReference palette, float scale = 1f, float alpha = 1f, bool ignoreTint = false)
		{
			var xyz = Vector3.Zero;
			if (sprite != null)
			{
				var cellOrigin = map.CenterOfCell(cell) - new WVec(0, 0, map.Grid.Ramps[map.Ramp[cell]].CenterHeightOffset);
				xyz = worldRenderer.Screen3DPosition(cellOrigin) + scale * (sprite.Offset - 0.5f * sprite.Size);
			}

			Update(cell.ToMPos(map.Grid.Type), sprite, palette, xyz, scale, alpha, ignoreTint);
		}

		void UpdateTint(MPos uv)
		{
			var cellIndex = uv.V * map.MapSize.Width + uv.U;
			var b = cellIndex >= 0 && cellIndex < batchForCell.Length ? batchForCell[cellIndex] : -1;
			if (b < 0)
				return;

			var batch = batches[b];
			var offset = vertexRowStride * uv.V + 4 * uv.U;
			if (batch.IgnoreTint[offset])
			{
				for (var i = 0; i < 4; i++)
				{
					var v = batch.Vertices[offset + i];
					batch.Vertices[offset + i] = new Vertex(v.X, v.Y, v.Z, v.S, v.T, v.U, v.V, v.C, v.A * Vector3.One, v.A);
				}

				return;
			}

			// Allow the terrain tint to vary linearly across the cell to smooth out the staircase effect
			// This is done by sampling the lighting the corners of the sprite, even though those pixels are
			// transparent for isometric tiles
			var tl = worldRenderer.TerrainLighting;
			var pos = map.CenterOfCell(uv.ToCPos(map));
			var step = map.Grid.TileScale / 2;
			var weights = new[]
			{
				tl.TintAt(pos + new WVec(-step, -step, 0)),
				tl.TintAt(pos + new WVec(step, -step, 0)),
				tl.TintAt(pos + new WVec(step, step, 0)),
				tl.TintAt(pos + new WVec(-step, step, 0))
			};

			// Apply tint directly to the underlying vertices
			// This saves us from having to re-query the sprite information, which has not changed
			for (var i = 0; i < 4; i++)
			{
				var v = batch.Vertices[offset + i];
				batch.Vertices[offset + i] = new Vertex(v.X, v.Y, v.Z, v.S, v.T, v.U, v.V, v.C, v.A * weights[i], v.A);
			}

			batch.DirtyRows.Add(uv.V);
		}

		Batch FindBatch(MPos uv, Sheet sheet, Sheet secondary)
		{
			var cellIndex = uv.V * map.MapSize.Width + uv.U;
			var b = batchForCell[cellIndex];
			if (b >= 0 && batches[b].CanAdd(sheet, secondary))
				return batches[b];

			// Keep cells in their current batch if possible; otherwise prefer
			// a batch that already contains both sheets, then any with room.
			Batch candidate = null;
			var needsNewSheet = -1;
			foreach (var batch in batches)
			{
				var fresh = batch.NewSheetCount(sheet, secondary);
				if (batch.SheetCount + fresh > batch.Sheets.Length)
					continue;

				if (fresh == 0)
					return batch;

				if (candidate == null || fresh < needsNewSheet)
				{
					candidate = batch;
					needsNewSheet = fresh;
				}
			}

			if (candidate != null)
				return candidate;

			candidate = new Batch(vertexRowStride * map.MapSize.Height, worldRenderer.TerrainLighting != null);
			batches.Add(candidate);
			return candidate;
		}

		public void Update(MPos uv, Sprite sprite, PaletteReference palette, in Vector3 pos, float scale, float alpha, bool ignoreTint)
		{
			// The vertex buffer does not have geometry for cells outside the map
			if (!map.Tiles.Contains(uv))
				return;

			var batch = batches[0];
			int2 samplers;
			if (sprite != null)
			{
				if (sprite.BlendMode != BlendMode)
					throw new InvalidDataException("Attempted to add sprite with a different blend mode");

				var secondarySheet = (sprite as SpriteWithSecondaryData)?.SecondarySheet;
				batch = FindBatch(uv, sprite.Sheet, secondarySheet);
				samplers = new int2(batch.GetOrAddSheetIndex(sprite.Sheet), batch.GetOrAddSheetIndex(secondarySheet));

				// PERF: Remove useless palette assignments for RGBA sprites
				// HACK: This is working around the limitation that palettes are defined on traits rather than on sequences,
				// and can be removed once this has been fixed
				if (sprite.Channel == TextureChannel.RGBA && !(palette?.HasColorShift ?? false))
					palette = null;
			}
			else
			{
				var b = batchForCell[uv.V * map.MapSize.Width + uv.U];
				if (b >= 0)
					batch = batches[b];

				sprite = emptySprite;
				samplers = int2.Zero;
			}

			var cellIndex = uv.V * map.MapSize.Width + uv.U;
			var previous = batchForCell[cellIndex];
			if (previous >= 0 && batches[previous] != batch)
			{
				// The cell is moving batches: erase the stale quad in the old batch.
				var oldBatch = batches[previous];
				var oldOffset = vertexRowStride * uv.V + 4 * uv.U;
				Util.FastCreateQuad(oldBatch.Vertices, Vector3.Zero, emptySprite, int2.Zero, 0, oldOffset, emptySprite.Size, Vector3.One, 1f);
				oldBatch.DirtyRows.Add(uv.V);
				if (oldBatch.IgnoreTint != null)
					oldBatch.IgnoreTint[oldOffset] = false;
			}

			var offset = vertexRowStride * uv.V + 4 * uv.U;
			Util.FastCreateQuad(batch.Vertices, pos, sprite, samplers, palette?.TextureIndex ?? 0, offset, scale * sprite.Size, alpha * Vector3.One, alpha);
			batchForCell[cellIndex] = batches.IndexOf(batch);
			palettes[cellIndex] = palette;

			if (worldRenderer.TerrainLighting != null)
			{
				batch.IgnoreTint[offset] = ignoreTint;
				UpdateTint(uv);
			}

			batch.DirtyRows.Add(uv.V);
		}

		public void Draw(Viewport viewport)
		{
			var cells = restrictToBounds ? viewport.VisibleCellsInsideBounds : viewport.AllVisibleCells;

			// Only draw the rows that are visible.
			var firstRow = cells.CandidateMapCoords.TopLeft.V.Clamp(0, map.MapSize.Height);
			var lastRow = (cells.CandidateMapCoords.BottomRight.V + 1).Clamp(firstRow, map.MapSize.Height);

			Game.Renderer.Flush();

			foreach (var batch in batches)
			{
				// Flush any visible changes to the GPU
				for (var row = firstRow; row <= lastRow; row++)
				{
					if (!batch.DirtyRows.Remove(row))
						continue;

					var rowOffset = vertexRowStride * row;
					batch.VertexBuffer.SetData(batch.Vertices, rowOffset, rowOffset, vertexRowStride);
				}

				Game.Renderer.WorldSpriteRenderer.DrawVertexBuffer(
					batch.VertexBuffer, indexBufferWrapper.Buffer, indexRowStride * firstRow,
					indexRowStride * (lastRow - firstRow), batch.Sheets, BlendMode);
			}

			Game.Renderer.Flush();
		}

		public void Dispose()
		{
			worldRenderer.PaletteInvalidated -= UpdatePaletteIndices;
			if (worldRenderer.TerrainLighting != null)
				worldRenderer.TerrainLighting.CellChanged -= UpdateTint;

			foreach (var batch in batches)
				batch.VertexBuffer.Dispose();

			lock (IndexBuffersLock)
				indexBufferWrapper.Dispose();
		}

		sealed class IndexBufferRc : IDisposable
		{
			public IIndexBuffer Buffer;
			int count;

			public IndexBufferRc(World world)
			{
				Buffer = Game.Renderer.Context.CreateIndexBuffer(
					Util.CreateQuadIndices(world.Map.MapSize.Width * world.Map.MapSize.Height));
			}

			public void AddRef() { count++; }

			public void Dispose()
			{
				count--;
				if (count == 0)
					Buffer.Dispose();
			}
		}
	}
}
