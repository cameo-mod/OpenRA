from pathlib import Path
p=Path('OpenRA.Mods.AS/Traits/Attack/AttackOpenTopped.cs');license=p.read_text(encoding='utf-8-sig').split('using System;')[0];p.write_text(license+'''namespace OpenRA.Mods.AS.Traits
{
	/// <summary>One-release migration alias. Canonical independent stations are unconditional.</summary>
	public class AttackOpenToppedInfo : AttackGarrisonedInfo
	{
		public override void RulesetLoaded(Ruleset rules, ActorInfo actor)
		{
			Log.Write("debug", "{0}: AttackOpenTopped is deprecated; use AttackGarrisoned.", actor.Name);
			base.RulesetLoaded(rules, actor);
		}
	}
}
''',encoding='utf-8')
