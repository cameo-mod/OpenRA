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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenRA.FileSystem;

namespace OpenRA.Graphics
{
	public delegate ISpriteFrame AdjustFrame(ISpriteFrame input, int index, int total);

	public sealed class SpriteCache : IDisposable
	{
		public readonly Dictionary<SheetType, SheetBuilder> SheetBuilders;
		readonly ISpriteLoader[] loaders;
		readonly IReadOnlyFileSystem fileSystem;
		readonly SpriteCachePool pool;

		readonly Dictionary<
			int,
			(ImmutableArray<int> Frames, MiniYamlNode.SourceLocation Location, AdjustFrame AdjustFrame, bool Premultiplied, object CacheKey)> spriteReservations = [];
		readonly Dictionary<string, List<int>> reservationsByFilename = [];

		readonly Dictionary<int, Sprite[]> resolvedSprites = [];

		readonly Dictionary<int, (string Filename, MiniYamlNode.SourceLocation Location)> missingFiles = [];

		int nextReservationToken = 1;

		public SpriteCache(
			IReadOnlyFileSystem fileSystem, ISpriteLoader[] loaders, int bgraSheetSize, int indexedSheetSize,
			SpriteCachePool pool = null, int bgraSheetMargin = 1, int indexedSheetMargin = 1)
		{
			if (pool != null)
			{
				// Borrow shared builders so atlases persist across map loads.
				SheetBuilders = new Dictionary<SheetType, SheetBuilder>
				{
					{ SheetType.Indexed, pool.GetOrCreateSheetBuilder(SheetType.Indexed) },
					{ SheetType.BGRA, pool.GetOrCreateSheetBuilder(SheetType.BGRA) }
				};
			}
			else
			{
				SheetBuilders = new Dictionary<SheetType, SheetBuilder>
				{
					{ SheetType.Indexed, new SheetBuilder(SheetType.Indexed, indexedSheetSize, indexedSheetMargin) },
					{ SheetType.BGRA, new SheetBuilder(SheetType.BGRA, bgraSheetSize, bgraSheetMargin) }
				};
			}

			this.fileSystem = fileSystem;
			this.loaders = loaders;
			this.pool = pool;
		}

		public int ReserveSprites(string filename, ImmutableArray<int> frames, MiniYamlNode.SourceLocation location,
			AdjustFrame adjustFrame = null, bool premultiplied = false, object cacheKey = null)
		{
			var token = nextReservationToken++;

			// Default the cache key to the delegate itself for backwards compatibility. Callers using delegates
			// that capture state should pass a stable value (e.g. a ValueTuple of the captured fields) so
			// SpriteCachePool can recognise equivalent reservations across map loads as cache hits.
			spriteReservations[token] = (frames, location, adjustFrame, premultiplied, cacheKey ?? (object)adjustFrame);
			reservationsByFilename.GetOrAdd(filename, _ => []).Add(token);
			return token;
		}

		static ISpriteFrame[] GetFrames(IReadOnlyFileSystem fileSystem, string filename, ISpriteLoader[] loaders)
		{
			if (!fileSystem.TryOpen(filename, out var stream))
				return null;

			using (stream)
			{
				foreach (var loader in loaders)
					if (loader.TryParseSprite(stream, filename, out var frames, out _))
						return frames;

				return null;
			}
		}

		public ISpriteFrame[] LoadFramesUncached(string filename)
		{
			return GetFrames(fileSystem, filename, loaders);
		}

		// Maximum number of sprite files decoded and packed per load wave.
		// Bounding the wave keeps the decoded frame corpus (the dominant transient
		// allocation during loading) at a fraction of its all-at-once size.
		const int MaxFilesPerWave = 256;

		public void LoadReservations(ModData modData)
		{
			var cacheHits = 0;
			var cacheMisses = 0;

			// Pre-pass: if a shared pool is in use, identify files whose every needed sprite is already
			// resolved in the pool, serve them directly, and remove them from the work list so Phase A
			// skips re-opening them.
			if (pool != null)
			{
				var fullyCachedFilenames = new List<string>();
				foreach (var (filename, tokens) in reservationsByFilename)
				{
					if (!pool.FrameCounts.TryGetValue(filename, out var frameCount))
						continue;

					var allCached = true;
					foreach (var token in tokens)
					{
						if (!spriteReservations.TryGetValue(token, out var rs))
							continue;

						var frames = rs.Frames.IsDefaultOrEmpty ? Enumerable.Range(0, frameCount) : (IEnumerable<int>)rs.Frames;
						foreach (var i in frames)
						{
							if (!pool.ResolvedSprites.ContainsKey((filename, i, rs.Premultiplied, rs.CacheKey)))
							{
								allCached = false;
								break;
							}
						}

						if (!allCached)
							break;
					}

					if (allCached)
						fullyCachedFilenames.Add(filename);
				}

				foreach (var filename in fullyCachedFilenames)
				{
					var tokens = reservationsByFilename[filename];
					var frameCount = pool.FrameCounts[filename];
					foreach (var token in tokens)
					{
						if (!spriteReservations.TryGetValue(token, out var rs))
							continue;

						var resolved = new Sprite[frameCount];
						resolvedSprites[token] = resolved;

						var frames = rs.Frames.IsDefaultOrEmpty ? Enumerable.Range(0, frameCount) : (IEnumerable<int>)rs.Frames;
						foreach (var i in frames)
							resolved[i] = pool.ResolvedSprites[(filename, i, rs.Premultiplied, rs.CacheKey)];

						cacheHits++;
					}

					reservationsByFilename.Remove(filename);
				}
			}

			var fileCount = reservationsByFilename.Count;

			// Resolve each filename to its package on the main thread before going parallel.
			// fileIndex is a Cache<> backed by a plain Dictionary — not safe for concurrent writes.
			var filePackages = new Dictionary<string, (IReadOnlyPackage Package, string ResolvedName)>(fileCount);
			foreach (var filename in reservationsByFilename.Keys)
				if (fileSystem.TryGetPackageContaining(filename, out var pkg, out var pkgResolvedName))
					filePackages[filename] = (pkg, pkgResolvedName);

			// Deterministic wave order: insertion order of reservationsByFilename.
			var orderedFilenames = reservationsByFilename.Keys.ToList();

			var packageLocks = new ConcurrentDictionary<IReadOnlyPackage, object>();

			// When borrowing shared builders, use the pool's resolved-sprite cache as our dedup map so
			// new sprites we pack become available for future loads. With no pool, fall back to a local map.
			// Spans all waves so sprites shared between files in different waves still deduplicate.
			var spriteCache = pool != null
				? pool.ResolvedSprites
				: new Dictionary<(string, int, bool, object), Sprite>(fileCount);

			var sessionStarted = false;
			var spritesPacked = 0;

			// Load in bounded waves: decode, resolve and pack MaxFilesPerWave files at a time so the
			// decoded frame corpus (the dominant transient allocation during loading) stays a small
			// fraction of the whole sprite set instead of being held until every file is decoded.
			for (var waveStart = 0; waveStart < orderedFilenames.Count; waveStart += MaxFilesPerWave)
			{
				var waveEnd = Math.Min(waveStart + MaxFilesPerWave, orderedFilenames.Count);

				// Phase A1: parallel I/O + decode for this wave only.
				// Per-package locks serialise access to non-thread-safe packages (ZipFile, MixFile) while
				// allowing files from different packages to be read and decoded in parallel.
				var fileFrames = new ConcurrentDictionary<string, ISpriteFrame[]>(StringComparer.Ordinal);

				using (new Support.PerfTimer($"LoadReservations.LoadFrames (wave files {waveStart + 1}-{waveEnd} of {orderedFilenames.Count}, parallel)"))
				{
					// Run Phase A1 on background threads so the main thread can keep the loading screen animated.
					var phase1 = Task.Run(() =>
						Parallel.For(waveStart, waveEnd, i =>
						{
							var filename = orderedFilenames[i];
							if (!filePackages.TryGetValue(filename, out var packageInfo))
								return;

							var (package, resolvedName) = packageInfo;

							var lockObj = packageLocks.GetOrAdd(package, _ => new object());
							Stream stream;
							lock (lockObj)
								stream = package.GetStream(resolvedName);

							if (stream == null)
								return;

							using (stream)
							{
								foreach (var loader in loaders)
								{
									if (loader.TryParseSprite(stream, filename, out var frames, out _))
									{
										fileFrames[filename] = frames;
										break;
									}
								}
							}
						}));

					// Redraw periodically so the window doesn't go black while Phase A1 runs on background threads.
					while (!phase1.Wait(2000))
						modData.LoadScreen?.Display();
				}

				var pendingResolve = new List<(
					string Filename,
					int FrameIndex,
					bool Premultiplied,
					object CacheKey,
					ISpriteFrame Frame,
					Sprite[] SpritesForToken)>();

				// Phase A2: serial post-processing — builds pendingResolve from the parallel-decoded frames.
				for (var i = waveStart; i < waveEnd; i++)
				{
					var filename = orderedFilenames[i];
					var tokens = reservationsByFilename[filename];

					// Fall back to the full TryOpen search for any file the parallel pass didn't decode.
					// TryGetPackageContaining only searches the fileIndex; TryOpen has an additional
					// fallback that scans all mounted packages, which can find files that weren't indexed.
					if (!fileFrames.TryGetValue(filename, out var loadedFrames))
						loadedFrames = GetFrames(fileSystem, filename, loaders);

					if (pool != null && loadedFrames != null)
						pool.FrameCounts[filename] = loadedFrames.Length;

					foreach (var token in tokens)
					{
						if (spriteReservations.TryGetValue(token, out var rs))
						{
							cacheMisses++;
							if (loadedFrames != null)
							{
								var resolved = new Sprite[loadedFrames.Length];
								resolvedSprites[token] = resolved;
								if (rs.Frames != null && rs.Frames.Any(i2 => i2 >= loadedFrames.Length))
									throw new InvalidOperationException($"{rs.Location}: {filename} does not contain frames: " +
										string.Join(',', rs.Frames.Where(f => f >= loadedFrames.Length)));

								var frames = rs.Frames != null ? rs.Frames : Enumerable.Range(0, loadedFrames.Length);
								var total = rs.Frames != null ? rs.Frames.Length : loadedFrames.Length;

								var j = 0;
								foreach (var i2 in frames)
								{
									var frame = loadedFrames[i2];
									if (rs.AdjustFrame != null)
										frame = rs.AdjustFrame(frame, j++, total);
									pendingResolve.Add((filename, i2, rs.Premultiplied, rs.CacheKey, frame, resolved));
								}
							}
							else
							{
								resolvedSprites[token] = null;
								missingFiles[token] = (filename, rs.Location);
							}
						}
					}
				}

				// Release this wave's decoded frames for collection before the next wave decodes.
				fileFrames.Clear();

				// When the sheet builder is adding sprites, it reserves height for the tallest sprite seen along the row.
				// We can achieve better sheet packing by keeping sprites with similar heights together.
				var orderedPendingResolve = pendingResolve.OrderBy(x => x.Frame.Size.Height);

				using (new Support.PerfTimer($"LoadReservations.BuildSheets (wave {pendingResolve.Count} sprites)"))
				{
					// When borrowing builders, start a fresh sheet for this session so we don't try to
					// mutate a sheet whose CPU buffer was released at the end of a previous LoadReservations.
					if (pool != null && !sessionStarted && pendingResolve.Count > 0)
					{
						foreach (var sb in SheetBuilders.Values)
							sb.BeginNewSession();

						sessionStarted = true;
					}

					foreach (var (filename, frameIndex, premultiplied, cacheKey, frame, spritesForToken) in orderedPendingResolve)
					{
						// Premultiplied and non-premultiplied sprites must be cached separately
						// to cover the case where the same image is requested in both versions.
						spritesForToken[frameIndex] = spriteCache.GetOrAdd(
							(filename, frameIndex, premultiplied, cacheKey),
							_ =>
							{
								var sheetBuilder = SheetBuilders[SheetBuilder.FrameTypeToSheetType(frame.Type)];
								return sheetBuilder.Add(frame, premultiplied);
							});

						modData.LoadScreen?.Display();
					}
				}

				spritesPacked += pendingResolve.Count;
			}

			foreach (var sb in SheetBuilders.Values)
				sb.Current?.ReleaseBuffer();

			spriteReservations.Clear();
			spriteReservations.TrimExcess();
			reservationsByFilename.Clear();
			reservationsByFilename.TrimExcess();

			long totalBytes = 0;
			var sheetCount = 0;
			foreach (var sb in SheetBuilders.Values)
			{
				foreach (var s in sb.AllSheets)
				{
					sheetCount++;
					var bytesPerPixel = sb.Type == SheetType.BGRA ? 4 : 1;
					totalBytes += (long)s.Size.Width * s.Size.Height * bytesPerPixel;
				}
			}

			Log.Write("perf", $"LoadReservations: hits={cacheHits} misses={cacheMisses} packed={spritesPacked}, {sheetCount} resident sheets, {totalBytes / (1024 * 1024)} MiB total");
		}

		public Sprite[] ResolveSprites(int token)
		{
			if (!resolvedSprites.Remove(token, out var resolved))
				throw new InvalidOperationException($"{nameof(token)} {token} has either already been resolved, or was never reserved via {nameof(ReserveSprites)}");

			resolvedSprites.TrimExcess();

			if (missingFiles.TryGetValue(token, out var r))
				throw new FileNotFoundException($"{r.Location}: {r.Filename} not found", r.Filename);

			return resolved;
		}

		public IEnumerable<(string Filename, MiniYamlNode.SourceLocation Location)> MissingFiles => missingFiles.Values.ToHashSet();

		public void Dispose()
		{
			// Shared builders are owned and disposed by the pool, not by us.
			if (pool != null)
				return;

			foreach (var sb in SheetBuilders.Values)
				sb.Dispose();
		}
	}
}
