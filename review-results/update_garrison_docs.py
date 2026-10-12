from pathlib import Path
p=Path('docs/attack-garrisoned.md')
s=p.read_text(encoding='utf-8-sig').replace('Selected armaments, targets, force/persistence/retaliation flags and fixed scan','Removed, dead or disposing hosts cannot fire (including carriers that are themselves\ninside another transport). Selected armaments, targets, force/persistence/retaliation flags and fixed scan')
p.write_text(s,encoding='utf-8',newline='\n')
