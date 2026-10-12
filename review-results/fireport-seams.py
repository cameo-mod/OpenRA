from pathlib import Path
p=Path('OpenRA.Mods.Common/Traits/AutoTarget.cs');s=p.read_text(encoding='utf-8-sig')
s=s.replace('.ToArray().Where(t => !t.IsTraitDisabled);', '.ToArray().Where(t => !t.IsTraitDisabled && t is not IIndependentAutoTarget);')
pos=s.index('\t\tpublic void ScanAndAttack(')
s=s[:pos]+'''		/// <summary>Read-only station scan using this occupant's priorities and selected arms.
		/// The station owns its synchronized cadence; this does not touch nextScanTime or RNG.</summary>
		public Target ScanForTarget(Actor origin, IReadOnlyList<Armament> stationArmaments,
			Func<Target, bool> canFire, bool targetFrozenActors = false)
		{
			if (IsTraitDisabled || Stance < UnitStance.Defend || stationArmaments.Count == 0)
				return Target.Invalid;
			var stances = PlayerRelationship.None;
			var range = WDist.Zero;
			foreach (var armament in stationArmaments)
			{
				if (armament.IsTraitDisabled || armament.IsTraitPaused)
					continue;
				stances |= armament.Info.TargetRelationships;
				if (range < armament.MaxRange())
					range = armament.MaxRange();
			}
			return stances == PlayerRelationship.None ? Target.Invalid
				: ChooseTarget(origin, null, stances, range, false, false, stationArmaments, canFire, targetFrozenActors);
		}

'''+s[pos:]
s=s.replace('Target ChooseTarget(Actor self, AttackBase ab, PlayerRelationship attackStances, WDist scanRange, bool allowMove, bool allowTurn)', 'Target ChooseTarget(Actor self, AttackBase ab, PlayerRelationship attackStances, WDist scanRange, bool allowMove, bool allowTurn,\n\t\t\tIReadOnlyList<Armament> stationArmaments = null, Func<Target, bool> canFire = null, bool targetFrozenActors = false)')
s=s.replace('if (allowMove || ab.Info.TargetFrozenActors)', 'if (allowMove || targetFrozenActors || ab?.Info.TargetFrozenActors == true)')
s=s.replace('var armaments = ab.ChooseArmamentsForTarget(target, false);', 'var armaments = stationArmaments ?? ab.ChooseArmamentsForTarget(target, false);')
s=s.replace('if (!allowTurn && !ab.TargetInFiringArc(self, target, ab.Info.FacingTolerance))', 'if (canFire != null ? !canFire(target) : !allowTurn && !ab.TargetInFiringArc(self, target, ab.Info.FacingTolerance))')
p.write_text(s,encoding='utf-8')
p=Path('OpenRA.Mods.Common/Traits/Attack/AttackBase.cs');s=p.read_text(encoding='utf-8-sig')
for old in ['public bool HasAnyValidWeapons(', 'public IEnumerable<Armament> ChooseArmamentsForTarget(', 'public WDist GetMinimumRangeVersusTarget(', 'public WDist GetMaximumRangeVersusTarget(']:
 s=s.replace(old,old.replace('public ', 'public virtual ',1))
p.write_text(s,encoding='utf-8')
