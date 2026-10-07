#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING. If not, see <http://www.gnu.org/licenses/>.
 */
#endregion

using OpenRA.Support;

namespace OpenRA
{
	/// <summary>
	/// Deterministic per-bot-player RNG for bot DECISION draws.
	/// <para>
	/// <see cref="World.LocalRandom"/> is unseeded per process and shared with cosmetic
	/// consumers (sound clip picks, weather, visual variants) whose variable draw counts
	/// shift every later pick on that stream — the same lobby seed then produces different
	/// bot decisions run-to-run and diverges across multiplayer clients. Bot modules
	/// therefore draw from a private stream derived from the lobby seed, the owning
	/// player's client index and a per-module salt: identical on every client and across
	/// same-seed runs, different whenever the lobby seed differs.
	/// </para>
	/// <para>
	/// Synchronized world state must still use <see cref="World.SharedRandom"/>.
	/// </para>
	/// </summary>
	public static class BotRandom
	{
		public static MersenneTwister Create(World world, Player player, int salt)
		{
			var lobbySeed = world.LobbyInfo.GlobalSettings.RandomSeed;
			return new MersenneTwister(unchecked(
				lobbySeed + (player.ClientIndex + 1) * (int)0x9E3779B9u + salt * (int)0x85EBCA6Bu));
		}
	}
}
