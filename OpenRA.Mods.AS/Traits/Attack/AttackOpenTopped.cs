#region Copyright & License Information
/*
 * Copyright 2015- OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

namespace OpenRA.Mods.AS.Traits
{
	/// <summary>One-release migration alias. Canonical independent stations are unconditional.</summary>
	public class AttackOpenToppedInfo : OpenRA.Mods.Common.Traits.AttackGarrisonedInfo
	{
		public override void RulesetLoaded(Ruleset rules, ActorInfo actor)
		{
			Log.Write("debug", $"{actor.Name}: AttackOpenTopped is deprecated; use AttackGarrisoned.");
			base.RulesetLoaded(rules, actor);
		}
	}
}
