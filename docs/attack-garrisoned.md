# Independent passenger fire ports

The canonical `AttackGarrisoned` lives in Common and derives its runtime from
`AttackBase`. It never uses `AttackFollow`'s combined passenger opportunity target.
AS `AttackOpenTopped` is a one-release rules alias. Common depends only on
`IFirePortOccupantProvider`; AS Garrisonable implements that bridge and forwards its
entry/exit notifications. Cargo uses its existing Common notifications. Assembly
lookup order is unchanged.

Each YAML port has at most one occupant. Entry selects the lowest free index;
existing bindings do not shift after another occupant exits. Overflow occupants
cannot fire and acquire a free station on reconciliation. `NoFireOverflow` records
an intentional capacity/port mismatch for the mod audit; it does not enable sharing.
Removed, dead or disposing hosts cannot fire (including carriers that are themselves
inside another transport). Selected armaments, targets, force/persistence/retaliation flags and fixed scan
cadence are station-local. Assignment and attack state participate in sync.

The occupant's AutoTarget supplies priorities, but the station supplies selected
weapons and an eligibility predicate. Candidate selection checks the actual port
position, minimum/maximum range and optional cone before ranking. No port or station
scan draws SharedRandom. Explicit host orders broadcast independently; a station
cannot substitute an opportunity target for an unavailable explicit target. The
activity rejects hidden live targets, preserves permitted frozen snapshots, handles
capture/cancellation/resupply, and moves using occupied station ranges with a
conservative allowance for port offset. Fixed point and integer values are used.

Armament owns attack notifications; station entry attaches host observers once and
exit removes them. Muzzles use the same assigned port. Forecast and capability
queries do not change station clocks, targets, passenger positions or RNG. Consumers
must enforce their visibility/ownership contract before exposing private occupants.

Run `dotnet build OpenRA.slnx -c Release` and
`dotnet test OpenRA.Test/OpenRA.Test.csproj -c Release`. AttackGarrisonedTest uses real
actors, armaments, AutoTarget priorities, Cargo and AS Garrisonable, with only the
outer World shell/spatial index replaced to avoid rendering/assets. Its public
Fixture is reusable by the mod predictor tests. It covers scripted station/fire
notification replay, not a rendered multiplayer match or projectile damage. Human
mixed-passenger gameplay approval remains separate.
