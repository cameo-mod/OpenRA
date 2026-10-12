from pathlib import Path
root=Path.cwd()
src=root/'OpenRA.Mods.AS/Traits/Attack/AttackGarrisoned.cs'
text=src.read_text(encoding='utf-8-sig')
license=(root/'OpenRA.Mods.Common/Traits/Attack/AttackGarrisoned.cs').read_text(encoding='utf-8-sig').split('using System;')[0]
text=license+text.replace('namespace OpenRA.Mods.AS.Traits','namespace OpenRA.Mods.Common.Traits').replace('INotifyGarrisonerEntered, INotifyGarrisonerExited','INotifyFirePortOccupantEntered, INotifyFirePortOccupantExited').replace('host.TraitOrDefault<Garrisonable>()?.Garrisoners ?? []','host.TraitsImplementing<IFirePortOccupantProvider>().SelectMany(p => p.Occupants)').replace('INotifyGarrisonerEntered.OnGarrisonerEntered','INotifyFirePortOccupantEntered.OnFirePortOccupantEntered').replace('INotifyGarrisonerExited.OnGarrisonerExited','INotifyFirePortOccupantExited.OnFirePortOccupantExited')
(root/'OpenRA.Mods.Common/Traits/Attack/AttackGarrisoned.cs').write_text(text,encoding='utf-8',newline='\n')
src.unlink()
bridge=root/'OpenRA.Mods.Common/Traits/Attack/IndependentFirePorts.cs'
b=bridge.read_text(encoding='utf-8-sig')
b=b.replace('\t/// <summary>An attack owner', '''\t/// <summary>Occupants supplied by a container outside the Common assembly.</summary>
\tpublic interface IFirePortOccupantProvider
\t{
\t\tIEnumerable<Actor> Occupants { get; }
\t}
\tpublic interface INotifyFirePortOccupantEntered
\t{
\t\tvoid OnFirePortOccupantEntered(Actor self, Actor occupant);
\t}
\tpublic interface INotifyFirePortOccupantExited
\t{
\t\tvoid OnFirePortOccupantExited(Actor self, Actor occupant);
\t}

\t/// <summary>An attack owner''')
bridge.write_text(license+b,encoding='utf-8',newline='\n')
g=root/'OpenRA.Mods.AS/Traits/Garrisonable.cs'
s=g.read_text(encoding='utf-8-sig').replace('ITransformActorInitModifier, INotifyPassengersDamage','ITransformActorInitModifier, INotifyPassengersDamage, IFirePortOccupantProvider')
s=s.replace('\t\tpublic int GarrisonerCount', '\t\tIEnumerable<Actor> IFirePortOccupantProvider.Occupants => garrisonable;\n\t\tpublic int GarrisonerCount')
for actor in ('c','a'):
    old=f'\t\t\t\t\t\tnpe.OnGarrisonerEntered(self, {actor});'
    if old not in s:
        old=f'\t\t\t\t\tnpe.OnGarrisonerEntered(self, {actor});'
    depth=old.split('npe.')[0]
    s=s.replace(old,old+'\n'+depth[:-1]+'foreach (var observer in self.TraitsImplementing<INotifyFirePortOccupantEntered>())\n'+depth+f'observer.OnFirePortOccupantEntered(self, {actor});')
old='\t\t\t\tnpe.OnGarrisonerExited(self, passenger);'
s=s.replace(old,old+'\n\t\t\tforeach (var observer in self.TraitsImplementing<INotifyFirePortOccupantExited>())\n\t\t\t\tobserver.OnFirePortOccupantExited(self, passenger);')
g.write_text(s,encoding='utf-8',newline='\n')
a=root/'OpenRA.Mods.AS/Traits/Attack/AttackOpenTopped.cs'
s=a.read_text(encoding='utf-8-sig').replace(' : AttackGarrisonedInfo',' : OpenRA.Mods.Common.Traits.AttackGarrisonedInfo')
a.write_text(s,encoding='utf-8',newline='\n')
