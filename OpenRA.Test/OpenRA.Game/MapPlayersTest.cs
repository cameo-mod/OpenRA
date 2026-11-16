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
using NUnit.Framework;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class MapPlayersTest
	{
		[TestCase(TestName = "Players keep their definition order when removed and re-added")]
		public void PlayersKeepDefinitionOrder()
		{
			var players = new MapPlayers();
			string[] names = ["Neutral", "Creeps", "Multi0", "Multi1", "Multi2"];
			foreach (var name in names)
				players.Players.Add(name, null);

			// The map editor removes and re-adds all spawn players when updating multiple spawns at once.
			for (var i = 2; i < names.Length; i++)
				players.Players.Remove(names[i]);

			for (var i = 2; i < names.Length; i++)
				players.Players.Add(names[i], null);

			Assert.That(players.Players.Keys, Is.EqualTo(names));
		}

		// MapPlayers(IEnumerable<MiniYamlNode>) builds Players with this constructor (MapPlayers.cs).
		// PlayerReference needs Game.ModData, so the pairs carry null values here; only the keys matter.
		static OrderedDictionary<string, PlayerReference> FromPairs(params string[] keys)
		{
			return new OrderedDictionary<string, PlayerReference>(keys.Select(k => KeyValuePair.Create(k, (PlayerReference)null)));
		}

		[TestCase(TestName = "Players built from definitions keep the definition order")]
		public void PlayersFromDefinitionsKeepOrder()
		{
			string[] names = ["Neutral", "Multi1", "Multi0", "Creeps"];
			Assert.That(FromPairs(names).Keys, Is.EqualTo(names));
		}

		[TestCase(TestName = "A duplicate player name is rejected, as the old ToDictionary did")]
		public void DuplicatePlayerNameThrows()
		{
			Assert.Throws<ArgumentException>(() => FromPairs("Neutral", "Multi0", "Multi1", "Multi0"));
		}
	}
}
