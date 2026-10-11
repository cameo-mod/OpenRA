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

using NUnit.Framework;
using OpenRA.Traits;

namespace OpenRA.Test
{
	sealed class MockBotInfo : TraitInfo, IBotInfo
	{
		public string Type { get; }
		public string Name { get; }

		public MockBotInfo(string type, string name)
		{
			Type = type;
			Name = name;
		}

		public override object Create(ActorInitializer init)
		{
			return null;
		}
	}

	[TestFixture]
	sealed class PlayerTest
	{
		[TestCase(TestName = "Bot player name falls back to the player name when the bot type is unregistered")]
		public void BotPlayerNameFallsBackForUnregisteredType()
		{
			var botInfos = new IBotInfo[] { new MockBotInfo("hard", "bot_ai.hard") };
			var name = Player.ResolveBotPlayerName(botInfos, "hard_control", "Multi0", 1);
			Assert.That(name, Is.EqualTo("Multi0"));
		}

		[TestCase(TestName = "Bot player name falls back to the player name when no bot types are registered")]
		public void BotPlayerNameFallsBackForEmptyBotInfos()
		{
			var name = Player.ResolveBotPlayerName(new IBotInfo[0], "hard_control", "Multi0", 1);
			Assert.That(name, Is.EqualTo("Multi0"));
		}
	}
}
