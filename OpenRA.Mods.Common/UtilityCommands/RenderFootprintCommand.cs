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
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.UtilityCommands
{
	// Shared logic for analysing whether an (isometric) building's sprite fits within its footprint cells.
	//
	// The mod uses a Rectangular MapGrid (TileScale 1024), so the world->screen projection is a
	// straight orthographic map and a footprint of Dimensions NxM occupies a screen rectangle of
	// TileSize*N by TileSize*M pixels with its top-left at the actor's TopLeft cell.
	//
	// The analyser decodes the built/idle sprite CPU-side (no GL/window required), reproduces the engine's
	// sprite placement maths, and reports how far the opaque silhouette spills past the footprint on each edge.
	// Spilling left/right/below the footprint overlaps neighbouring cells and is the failure we care about;
	// spilling above the top edge (spires, antennae, smoke) is normal for isometric art and is allowed.
	sealed class FootprintAnalyzer
	{
		readonly int TileSize;
		const int TileScale = 1024;

		// Maps a sprite's logical TextureChannel index to the byte offset within a BGRA sheet texel,
		// matching how Util.FastCopyIntoChannel packs indexed sprites into the sheet.
		static readonly int[] ChannelMasks = [2, 1, 0, 3];

		public sealed class Result
		{
			public string Actor;
			public string Image;
			public string Sequence;
			public int DimX, DimY;
			public float Scale;
			public int SpriteW, SpriteH;
			public float OffsetX, OffsetY;

			// Opaque silhouette bounding box in screen pixels (footprint top-left == 0,0).
			public bool HasOpaque;
			public int MinX, MinY, MaxX, MaxY;

			// Overflow past the footprint in screen pixels (0 == fits on that edge).
			public int OverLeft, OverRight, OverBottom, OverTop;

			public bool Fits;
			public float RecommendedScale;
			public string Error;

			// Proposed fix: a Scale plus a LocalCenterOffset delta (world units, to ADD to the building's
			// existing LocalCenterOffset) that recentres the base horizontally and fits it inside the footprint.
			public float SolveScale;
			public int SolveLocalDX, SolveLocalDY;
			public int CurLocalX, CurLocalY, CurLocalZ;

			// Undersize metrics: occupied footprint cells that the base does not cover at all, and the
			// span of cells that ARE covered (a suggested smaller footprint, for review only).
			public int EmptyOccupiedCells;
			public int OccupiedCells;
			public int SuggestedDimX, SuggestedDimY;

			public int TileSize;
			public int FootW => DimX * TileSize;
			public int FootH => DimY * TileSize;
		}

		readonly ModData modData;
		readonly string tileset;
		readonly List<MiniYamlNode> sequenceNodes;

		public FootprintAnalyzer(ModData modData)
		{
			this.modData = modData;
			tileset = modData.DefaultTerrainInfo.Keys.First();
			TileSize = modData.DefaultTerrainInfo[tileset].TileSize.Width;

			// Parse every image's sequence definition once (cheap - YAML only, no sprite decode).
			// Inheritance (Inherits:) is resolved by MiniYaml.Load.
			sequenceNodes = MiniYaml.Load(modData.DefaultFileSystem, modData.Manifest.Sequences, null);
		}

		MiniYamlNode FindImageNode(string image)
		{
			return sequenceNodes.FirstOrDefault(n =>
				!n.Key.StartsWith(ActorInfo.AbstractActorPrefix) &&
				string.Equals(n.Key, image, StringComparison.OrdinalIgnoreCase));
		}

		static string PickSequence(IReadOnlyDictionary<string, ISpriteSequence> seqs)
		{
			// Prefer the standing body sprite, then a damaged variant, then anything that isn't the cameo icon.
			foreach (var name in new[] { "idle", "damaged-idle", "build", "make" })
				if (seqs.ContainsKey(name))
					return name;

			return seqs.Keys.FirstOrDefault(k => k != "icon") ?? seqs.Keys.FirstOrDefault();
		}

		public Result Analyze(ActorInfo actorInfo, float? scaleOverride, WVec? localOffsetOverride, out byte[] pngBgra, out int pngW, out int pngH)
		{
			pngBgra = null;
			pngW = pngH = 0;

			var r = new Result { Actor = actorInfo.Name, TileSize = TileSize };
			try
			{
				var building = actorInfo.TraitInfoOrDefault<BuildingInfo>();
				if (building == null)
				{
					r.Error = "no Building trait";
					return r;
				}

				r.DimX = building.Dimensions.X;
				r.DimY = building.Dimensions.Y;

				var rsi = actorInfo.TraitInfoOrDefault<RenderSpritesInfo>();
				var image = (rsi?.Image ?? actorInfo.Name).ToLowerInvariant();
				r.Image = image;

				var node = FindImageNode(image);
				if (node == null)
				{
					r.Error = $"image `{image}` has no sequences";
					return r;
				}

				// Load only this image's sprites into a private cache so we skip the (~16s) full preload.
				var rc = modData.Manifest.RendererConstants;
				using var cache = new SpriteCache(
					modData.DefaultFileSystem, modData.SpriteLoaders,
					rc.SequenceBgraSheetSize, rc.SequenceIndexedSheetSize, modData.SpriteCachePool);

				var seqs = modData.SpriteSequenceLoader.ParseSequences(modData, tileset, cache, node);
				if (seqs.Count == 0)
				{
					r.Error = $"image `{image}` defines no sequences";
					return r;
				}

				var seqName = PickSequence(seqs);
				r.Sequence = seqName;
				var seq = seqs[seqName];

				cache.LoadReservations(modData);
				seq.ResolveSprites(cache);

				var sprite = seq.GetSprite(0);
				if (sprite == null)
				{
					r.Error = $"sequence `{seqName}` resolved to a null sprite";
					return r;
				}

				var scale = scaleOverride ?? seq.Scale;
				r.Scale = scale;

				r.CurLocalX = building.LocalCenterOffset.X;
				r.CurLocalY = building.LocalCenterOffset.Y;
				r.CurLocalZ = building.LocalCenterOffset.Z;

				var lc = localOffsetOverride ?? building.LocalCenterOffset;

				// Actor CenterPosition for a building placed at cell (0,0):
				//   CenterOfCell(0,0) + ((CenterOfCell(N,M) - CenterOfCell(1,1)) / 2 + LocalCenterOffset)
				//   = (512*N + lc.X, 512*M + lc.Y, lc.Z)
				var centerXw = 512 * r.DimX + lc.X;
				var centerYw = 512 * r.DimY + lc.Y;
				var centerZw = lc.Z;

				var f = (float)TileSize / TileScale;
				var projX = f * centerXw;
				var projY = f * (centerYw - centerZw);

				// Sprite quad (engine maths): top-left = proj + scale*(Offset - Size/2), size = scale*Size.
				var sw = Math.Abs(sprite.Bounds.Width);
				var sh = Math.Abs(sprite.Bounds.Height);
				r.SpriteW = sw;
				r.SpriteH = sh;
				r.OffsetX = sprite.Offset.X;
				r.OffsetY = sprite.Offset.Y;

				var spriteTLx = projX + scale * (sprite.Offset.X - sw / 2f);
				var spriteTLy = projY + scale * (sprite.Offset.Y - sh / 2f);

				// Decode the silhouette from the (CPU-side) sheet buffer: index 0 == transparent.
				var sheet = sprite.Sheet;
				var data = sheet.GetData();
				var stride = 4 * sheet.Size.Width;
				var ch = ChannelMasks[(int)sprite.Channel];
				var srcLeft = Math.Min(sprite.Bounds.Left, sprite.Bounds.Right);
				var srcTop = Math.Min(sprite.Bounds.Top, sprite.Bounds.Bottom);

				var footW = r.FootW;
				var footH = r.FootH;

				var worldMinX = Math.Min(0, (int)Math.Floor(spriteTLx));
				var worldMaxX = Math.Max(footW, (int)Math.Ceiling(spriteTLx + scale * sw));
				var worldMinY = Math.Min(0, (int)Math.Floor(spriteTLy));
				var worldMaxY = Math.Max(footH, (int)Math.Ceiling(spriteTLy + scale * sh));

				const int Margin = 12;
				var originX = Margin - worldMinX;
				var originY = Margin - worldMinY;
				var canvasW = worldMaxX - worldMinX + 2 * Margin;
				var canvasH = worldMaxY - worldMinY + 2 * Margin;

				var canvas = new byte[4 * canvasW * canvasH];
				for (var i = 0; i < canvas.Length; i += 4)
				{
					canvas[i] = 255; canvas[i + 1] = 255; canvas[i + 2] = 255; canvas[i + 3] = 255;
				}

				void Blend(int cx, int cy, byte b, byte g, byte rr, float a)
				{
					if (cx < 0 || cy < 0 || cx >= canvasW || cy >= canvasH)
						return;
					var o = 4 * (cy * canvasW + cx);
					canvas[o] = (byte)(b * a + canvas[o] * (1 - a));
					canvas[o + 1] = (byte)(g * a + canvas[o + 1] * (1 - a));
					canvas[o + 2] = (byte)(rr * a + canvas[o + 2] * (1 - a));
				}

				// Footprint cells.
				for (var gy = 0; gy < r.DimY; gy++)
				{
					for (var gx = 0; gx < r.DimX; gx++)
					{
						var occupied = building.Footprint.TryGetValue(new CVec(gx, gy), out var type) &&
							type != FootprintCellType.Empty;

						for (var py = 0; py < TileSize; py++)
						{
							for (var px = 0; px < TileSize; px++)
							{
								var edge = px == 0 || py == 0 || px == TileSize - 1 || py == TileSize - 1;
								var cx = gx * TileSize + px + originX;
								var cy = gy * TileSize + py + originY;
								if (edge)
									Blend(cx, cy, 160, 160, 160, 1f);
								else if (occupied)
									Blend(cx, cy, 205, 240, 205, 1f);
								else
									Blend(cx, cy, 235, 235, 235, 1f);
							}
						}
					}
				}

				// Sprite silhouette - iterate destination pixels and sample the source (handles any scale).
				var dstX0 = (int)Math.Floor(spriteTLx);
				var dstX1 = (int)Math.Ceiling(spriteTLx + scale * sw);
				var dstY0 = (int)Math.Floor(spriteTLy);
				var dstY1 = (int)Math.Ceiling(spriteTLy + scale * sh);

				// "Ground base only": pixels above the footprint's top edge (wy < 0) are an allowed northern
				// spire/overhang and never count as overflow. Overflow is measured from the body (wy >= 0) only.
				var hasBody = false;
				int bMinX = 0, bMaxX = 0, bMaxY = 0;
				var cellHasArt = new bool[r.DimX * r.DimY];

				for (var wy = dstY0; wy < dstY1; wy++)
				{
					var srcY = (int)Math.Floor((wy + 0.5f - spriteTLy) / scale);
					if (srcY < 0 || srcY >= sh)
						continue;

					for (var wx = dstX0; wx < dstX1; wx++)
					{
						var srcX = (int)Math.Floor((wx + 0.5f - spriteTLx) / scale);
						if (srcX < 0 || srcX >= sw)
							continue;

						var idx = data[(srcTop + srcY) * stride + ch + 4 * (srcLeft + srcX)];
						if (idx == 0)
							continue;

						if (!r.HasOpaque)
						{
							r.HasOpaque = true;
							r.MinX = r.MaxX = wx;
							r.MinY = r.MaxY = wy;
						}
						else
						{
							r.MinX = Math.Min(r.MinX, wx);
							r.MaxX = Math.Max(r.MaxX, wx);
							r.MinY = Math.Min(r.MinY, wy);
							r.MaxY = Math.Max(r.MaxY, wy);
						}

						if (wy < 0)
						{
							// Northern spire/overhang above the footprint - always allowed.
							Blend(wx + originX, wy + originY, 230, 150, 80, 0.85f); // blue
							continue;
						}

						if (!hasBody)
						{
							hasBody = true;
							bMinX = bMaxX = wx;
							bMaxY = wy;
						}
						else
						{
							bMinX = Math.Min(bMinX, wx);
							bMaxX = Math.Max(bMaxX, wx);
							bMaxY = Math.Max(bMaxY, wy);
						}

						if (wx < 0 || wx >= footW || wy >= footH)
							Blend(wx + originX, wy + originY, 30, 30, 220, 0.85f); // red - real overflow
						else
						{
							cellHasArt[wy / TileSize * r.DimX + wx / TileSize] = true;
							Blend(wx + originX, wy + originY, 70, 70, 70, 0.85f); // gray - fits
						}
					}
				}

				// Top spire extent is reported (informational) from the full silhouette; it never fails the fit.
				r.OverTop = r.HasOpaque ? Math.Max(0, -r.MinY) : 0;

				if (hasBody)
				{
					r.OverLeft = Math.Max(0, -bMinX);
					r.OverRight = Math.Max(0, bMaxX + 1 - footW);
					r.OverBottom = Math.Max(0, bMaxY + 1 - footH);

					// Suggest a scale (about the projection centre) that pulls every failing edge inside the footprint.
					var candidates = new List<float>();
					if (r.OverRight > 0 && bMaxX + 1 - projX > 0.01f)
						candidates.Add(scale * (footW - projX) / (bMaxX + 1 - projX));
					if (r.OverLeft > 0 && projX - bMinX > 0.01f)
						candidates.Add(scale * projX / (projX - bMinX));
					if (r.OverBottom > 0 && bMaxY + 1 - projY > 0.01f)
						candidates.Add(scale * (footH - projY) / (bMaxY + 1 - projY));

					r.RecommendedScale = candidates.Count > 0 ? candidates.Min() : scale;
				}
				else
					r.RecommendedScale = scale;

				r.Fits = r.OverLeft == 0 && r.OverRight == 0 && r.OverBottom == 0;

				// Solve for a (Scale, LocalCenterOffset delta) that fits the base.
				// "Ground base only" semantics: the base may rise ABOVE the footprint's top edge freely (it
				// becomes an allowed spire), so vertical overflow is fixed by translating UP, never by scaling.
				// Scale is therefore driven purely by base WIDTH; left/right off-centring is fixed by translating.
				r.SolveScale = scale;
				if (hasBody)
				{
					var bodyW = bMaxX - bMinX + 1;
					var mid = (bMinX + bMaxX) / 2f;

					// Shrink only if the base is genuinely wider than the footprint (2px margin); never upscale.
					var k = bodyW > footW - 2 ? (footW - 2f) / bodyW : 1f;
					var sSolve = scale * k;

					// Horizontal recentre: land the scaled base midpoint on footW/2.
					var dpxX = footW / 2f - projX - k * (mid - projX);

					// Vertical: push up only if the scaled base would still drop below the footprint bottom.
					var newBottom = projY + k * (bMaxY - projY);
					var dpxY = Math.Min(0f, footH - 1f - newBottom);

					r.SolveScale = sSolve;
					r.SolveLocalDX = (int)Math.Round(dpxX / f);
					r.SolveLocalDY = (int)Math.Round(dpxY / f);
				}

				// Undersize metrics: which occupied footprint cells the base never covers, and the span of
				// covered cells (a suggested smaller footprint - reported for review, never auto-applied).
				int minCol = r.DimX, maxCol = -1, minRow = r.DimY, maxRow = -1;
				for (var gy = 0; gy < r.DimY; gy++)
				{
					for (var gx = 0; gx < r.DimX; gx++)
					{
						var occupied = building.Footprint.TryGetValue(new CVec(gx, gy), out var type) &&
							type != FootprintCellType.Empty;
						if (!occupied)
							continue;

						r.OccupiedCells++;
						if (cellHasArt[gy * r.DimX + gx])
						{
							minCol = Math.Min(minCol, gx);
							maxCol = Math.Max(maxCol, gx);
							minRow = Math.Min(minRow, gy);
							maxRow = Math.Max(maxRow, gy);
						}
						else
							r.EmptyOccupiedCells++;
					}
				}

				r.SuggestedDimX = maxCol >= minCol ? maxCol - minCol + 1 : r.DimX;
				r.SuggestedDimY = maxRow >= minRow ? maxRow - minRow + 1 : r.DimY;

				pngBgra = canvas;
				pngW = canvasW;
				pngH = canvasH;
			}
			catch (Exception e)
			{
				r.Error = e.Message;
			}

			return r;
		}
	}

	sealed class RenderFootprintCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--render-footprint";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 2;
		}

		[Desc("ACTOR [SCALE] [--local-offset X,Y,Z] [--out FILE.png]",
			"Render an isometric building's idle sprite over its footprint cells to verify it fits, headless (no GL). ",
			"SCALE and --local-offset override the sprite scale / building LocalCenterOffset to preview a candidate fix.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			var actorName = args[1].ToLowerInvariant();

			float? scale = null;
			WVec? localOffset = null;
			var outFile = $"{actorName}-footprint.png";
			for (var i = 2; i < args.Length; i++)
			{
				if (args[i] == "--out" && i + 1 < args.Length)
					outFile = args[++i];
				else if (args[i] == "--local-offset" && i + 1 < args.Length)
					localOffset = FieldLoader.GetValue<WVec>("local-offset", args[++i]);
				else if (float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
					scale = s;
			}

			if (!modData.DefaultRules.Actors.TryGetValue(actorName, out var actorInfo))
				throw new InvalidOperationException($"Actor `{actorName}` does not exist.");

			var analyzer = new FootprintAnalyzer(modData);
			var r = analyzer.Analyze(actorInfo, scale, localOffset, out var bgra, out var w, out var h);

			if (r.Error != null)
				throw new InvalidOperationException($"{actorName}: {r.Error}");

			new Png(bgra, SpriteFrameType.Bgra32, w, h).Save(outFile);

			Console.WriteLine($"Actor:      {r.Actor}  (image {r.Image}, sequence {r.Sequence})");
			Console.WriteLine($"Footprint:  {r.DimX}x{r.DimY} cells  =>  {r.FootW}x{r.FootH} px");
			Console.WriteLine($"Sprite:     {r.SpriteW}x{r.SpriteH} px  offset ({r.OffsetX},{r.OffsetY})  scale {r.Scale:0.###}");
			if (r.HasOpaque)
				Console.WriteLine($"Silhouette: x[{r.MinX}..{r.MaxX}] y[{r.MinY}..{r.MaxY}] px");
			Console.WriteLine($"Overflow:   left {r.OverLeft}  right {r.OverRight}  bottom {r.OverBottom}  (top spire {r.OverTop}, allowed)");
			Console.WriteLine($"Fit:        {(r.Fits ? "PASS" : "FAIL")}");
			if (!r.Fits)
				Console.WriteLine($"Proposed:   Scale: {r.SolveScale:0.###}  LocalCenterOffset delta: {r.SolveLocalDX},{r.SolveLocalDY},0");
			Console.WriteLine($"Saved:      {outFile}");
		}
	}

	sealed class AuditFootprintsCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--audit-footprints";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return true;
		}

		[Desc("[--png DIR] [--all]",
			"Audit every building actor for sprites that overflow their footprint. ",
			"By default lists only overflowing buildings; --all lists every building. ",
			"--png DIR writes a render PNG for each overflowing building into DIR.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			string pngDir = null;
			var showAll = false;
			for (var i = 1; i < args.Length; i++)
			{
				if (args[i] == "--png" && i + 1 < args.Length)
					pngDir = args[++i];
				else if (args[i] == "--all")
					showAll = true;
			}

			if (pngDir != null)
				Directory.CreateDirectory(pngDir);

			var analyzer = new FootprintAnalyzer(modData);

			var buildings = modData.DefaultRules.Actors.Values
				.Where(a => !a.Name.StartsWith(ActorInfo.AbstractActorPrefix) &&
					a.HasTraitInfo<BuildingInfo>())
				.OrderBy(a => a.Name, StringComparer.Ordinal)
				.ToList();

			Console.WriteLine($"actor,image,sequence,dims,scale,sprite,left,right,bottom,topSpire,fit,solveScale,solveDX,solveDY,absLocalX,absLocalY,absLocalZ");

			var failures = 0;
			var errors = 0;
			foreach (var actorInfo in buildings)
			{
				var r = analyzer.Analyze(actorInfo, null, null, out var bgra, out var w, out var h);
				if (r.Error != null)
				{
					errors++;
					if (showAll)
						Console.WriteLine($"{r.Actor},{r.Image},,,,,,,,,ERROR:{r.Error}");
					continue;
				}

				if (!r.Fits)
					failures++;

				if (r.Fits && !showAll)
					continue;

				Console.WriteLine(string.Join(",",
					r.Actor, r.Image, r.Sequence, $"{r.DimX}x{r.DimY}", r.Scale.ToString("0.###", CultureInfo.InvariantCulture),
					$"{r.SpriteW}x{r.SpriteH}", r.OverLeft, r.OverRight, r.OverBottom, r.OverTop,
					r.Fits ? "PASS" : "FAIL", r.SolveScale.ToString("0.###", CultureInfo.InvariantCulture), r.SolveLocalDX, r.SolveLocalDY,
					r.CurLocalX + r.SolveLocalDX, r.CurLocalY + r.SolveLocalDY, r.CurLocalZ));

				if (pngDir != null && !r.Fits && bgra != null)
					new Png(bgra, SpriteFrameType.Bgra32, w, h).Save(Path.Combine(pngDir, $"{r.Actor}.png"));
			}

			Console.WriteLine($"# {buildings.Count} buildings, {failures} overflow, {errors} errors");
		}
	}

	sealed class AuditUndersizeCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--audit-undersize";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return true;
		}

		[Desc("[--png DIR]",
			"List building actors whose sprite under-fills its footprint, leaving occupied cells with no art over them. ",
			"Reports a suggested smaller footprint for review only - footprint changes affect gameplay and are never applied here.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			string pngDir = null;
			for (var i = 1; i < args.Length; i++)
				if (args[i] == "--png" && i + 1 < args.Length)
					pngDir = args[++i];

			if (pngDir != null)
				Directory.CreateDirectory(pngDir);

			var analyzer = new FootprintAnalyzer(modData);

			var buildings = modData.DefaultRules.Actors.Values
				.Where(a => !a.Name.StartsWith(ActorInfo.AbstractActorPrefix) && a.HasTraitInfo<BuildingInfo>())
				.OrderBy(a => a.Name, StringComparer.Ordinal)
				.ToList();

			Console.WriteLine("actor,image,dims,emptyCells,occupiedCells,suggestedDims,fit");

			var undersize = 0;
			foreach (var actorInfo in buildings)
			{
				var r = analyzer.Analyze(actorInfo, null, null, out var bgra, out var w, out var h);
				if (r.Error != null || !r.HasOpaque)
					continue;

				// Only flag genuine under-fill: an occupied cell with no art, and not currently overflowing.
				if (r.EmptyOccupiedCells == 0 || !r.Fits)
					continue;

				undersize++;
				Console.WriteLine(string.Join(",",
					r.Actor, r.Image, $"{r.DimX}x{r.DimY}", r.EmptyOccupiedCells, r.OccupiedCells,
					$"{r.SuggestedDimX}x{r.SuggestedDimY}", r.Fits ? "PASS" : "FAIL"));

				if (pngDir != null && bgra != null)
					new Png(bgra, SpriteFrameType.Bgra32, w, h).Save(Path.Combine(pngDir, $"{r.Actor}.png"));
			}

			Console.WriteLine($"# {buildings.Count} buildings, {undersize} under-fill their footprint");
		}
	}
}
