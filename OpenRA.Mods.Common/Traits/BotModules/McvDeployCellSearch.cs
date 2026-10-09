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
using System.Linq;

namespace OpenRA.Mods.Common.Traits
{
	public enum McvDeployCellRejectReason { None, Placement, Unreachable }

	/// <summary>Unique candidate tallies for one opt-in deployment search.</summary>
	public readonly record struct McvDeployCellTally(int Scanned, int Placeable, int Unreachable)
	{
		public int PlacementRejected => Scanned - Placeable;
	}

	/// <summary>Opt-in search; the module's legacy search remains unchanged when disabled.</summary>
	public static class McvDeployCellSearch
	{
		/// <summary>Find a reachable placeable candidate without aborting on another cell's rejection.</summary>
		public static CPos? Find(IEnumerable<CPos> weightedCells, CPos source, CPos target,
			int tryMaintainRange, Func<CPos, bool> canPlace, Func<CPos, bool> canReach,
			out McvDeployCellTally tally)
		{
			// Reuse the same ordering and verdict in both passes, including the equal-source shuffle.
			var cells = weightedCells.ToArray();
			var verdicts = new Dictionary<CPos, McvDeployCellRejectReason>();
			var placeable = 0;
			var unreachable = 0;
			bool Accept(CPos cell)
			{
				if (!verdicts.TryGetValue(cell, out var reason))
				{
					if (!canPlace(cell))
						reason = McvDeployCellRejectReason.Placement;
					else
					{
						placeable++;
						if (!canReach(cell))
						{
							unreachable++;
							reason = McvDeployCellRejectReason.Unreachable;
						}
					}

					verdicts.Add(cell, reason);
				}

				return reason == McvDeployCellRejectReason.None;
			}

			CPos? best = null;
			foreach (var cell in cells)
				if (Accept(cell))
				{
					best = cell;
					break;
				}

			if (best.HasValue && source != target &&
				(best.Value - target).LengthSquared >= (tryMaintainRange + 2) * (tryMaintainRange + 2))
			{
				foreach (var cell in cells.OrderBy(c => (c - target).LengthSquared))
					if (Accept(cell))
					{
						if ((cell - target).LengthSquared < (best.Value - target).LengthSquared)
							best = cell;

						break;
					}
			}

			tally = new McvDeployCellTally(verdicts.Count, placeable, unreachable);
			return best;
		}
	}
}
