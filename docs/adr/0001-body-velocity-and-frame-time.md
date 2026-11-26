# 1. Body dynamics use the physics body velocity and frame time

Date: 2026-11-26. Status: accepted, not yet confirmed in a game.

## Context

The plugin worked out velocity from the pawn's transform, sampled in
`Update`. A `Rigidbody` without interpolation moves its transform only on
physics steps, so a frame sees no step, one step or several. In a simulation
at a constant 68 m/s, with 50 Hz physics and 144 frames a second, surge read
over 700 m/s² instead of zero. The same defect was measured in a recording
from `ue4ss-ogs-ts`. It hasn't been measured in a BepInEx game.

The time step also came from `Time.time`, a single-precision float. It loses
resolution as a session gets longer, which adds noise to smooth motion.

## Decision

**Velocity** comes from the `Rigidbody` on the pawn or its nearest parent.
Neither build can reference the physics module, so the component is found by
type name and read by reflection, through `linearVelocity` or `velocity`.

A body's reading isn't always the pawn's motion: a kinematic body reports
zero. Every 0.5 s the distance the pawn moved is compared with the distance
the velocity explains. A check fails when more than half the path, plus 0.5 m,
is unexplained. Each change of source restarts the derivation.

**Three failures in a row** are needed before position deltas take over, and
one passing check brings the body's velocity back. One failure was enough at first.
In a `ue4ss-ogs-ts` game recording, brief fallbacks brought back the
surge spikes that engine velocity removes. A reading that's really broken keeps failing, so it still loses,
about a second later. Until then its dynamics are wrong.

**A frozen pose isn't evidence** against a velocity. A failed check in which
the pawn didn't move at all is never counted, however long that lasts: a game
can hold its simulation without reporting a pause, as Hot Wheels Unleashed
does while it shows a keyboard hint, and the velocity stays as it was. A
failed check isn't counted either when the pawn moved less than a tenth of the
distance the velocity explains. In a `ue4ss-ogs-ts` game recording, switching during a frozen pose
caused a surge spike when the pose moved again. At most six of these slightly moving checks in a row
are skipped. Without that limit a velocity more than ten times too big would
never be rejected.

**Only movement restores trust.** A passing check counts when the pawn moved
more than 1 m in it, the distance at which a zero velocity fails. A parked
pawn passes any velocity that reads zero, so without this a reading stuck at
zero was trusted again at every stop.

**Time** is a double that adds up `Time.deltaTime`. It works on Unity
versions that don't have `Time.timeAsDouble`.

This logic lives in `Shared/`, so both builds and the smoke test run the same
code. The vendored core is unchanged.

## Consequences

- A pawn that moves in steps and has no readable `Rigidbody` still takes the
  noisy position path. The log names the source in use.
- IL2CPP games can strip the velocity getter. The reader then isn't created.
- Reading by reflection allocates a little on each frame.
