# HitShape int32 overflow findings

Follow-up to `375d20e9ca` (polygon hitshape int32 sqrt overflow crash) and the
companion Capsule fix on this branch.

## Background

`int2.LengthSquared` (`X * X + Y * Y`) is evaluated in int32 and wraps
silently past ~46340 units. `int2.Length` feeds that result to
`Exts.ISqrt(int)`, which throws `InvalidOperationException: square root of a
negative integer` on a wrapped-negative operand. The crash domain was
observed live in a 4v4 A/B campaign game
(`AreaDamageWarhead.ApplyRing -> PolygonShape.DistanceFromEdge`,
sqrt of -1868759040).

## Fixed

- `PolygonShape` (`375d20e9ca`): squared-distance math migrated to long,
  `WDist` result saturated at `int.MaxValue`. Bit-identical inside the
  previously valid int domain.
- `CapsuleShape` (this branch): `abLenSq`, the projection parameter `t`,
  the perpendicular distance, and both endpoint distances migrated to long,
  `WDist` result saturated at `int.MaxValue`. The capsule shares the
  polygon's crash path (`(projection - p).Length` on a possibly-wrapped
  square).

## Remaining findings — RectangleShape, CircleShape

Both still evaluate `int2.Length`/`LengthSquared` in int32:

- `RectangleShape.DistanceFromEdge` calls `int2.Length` via `VectorLength`.
  A wrapped-negative `LengthSquared` throws the same
  `InvalidOperationException` inside `Exts.ISqrt`. **Same crash class,
  lower probability**: rectangle edges are axis-aligned clamp distances, so
  reaching the >46340-unit domain needs an equally extreme impact vector,
  but the crash path is identical.
- `CircleShape.DistanceFromEdge` computes `(v - center).Length` style math
  in int32. Same wrapped-operand crash class as above.

No observed crash is attributed to Rectangle/Circle to date — every
recorded campaign crash is `PolygonShape.DistanceFromEdge`. The remaining
two shapes are flagged for review rather than patched on this bump, per
maintainer instruction to keep the engine delta tight and reviewable.

## Recommendation

Apply the identical long-math migration to `RectangleShape` and
`CircleShape` on the next engine bump. The change is mechanical
(`LengthSquared`/`Length`/`Dot` call sites -> long equivalents, `WDist`
result saturated at `int.MaxValue`) and produces bit-identical results in
the previously valid int domain.
