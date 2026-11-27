# 3. Automatic discovery skips UI objects

Date: 2026-11-27. Status: accepted, confirmed in Torque Drift (IL2CPP), which
skipped UI objects and went on to the tagged car, and in Subnautica Below Zero
(Mono), which skipped them with no plugin error and tracked the player.

## Context

In a Torque Drift session automatic discovery chose `Panel`, whose components
are `RectTransform`, `AspectRatioFitter` and `RectMask2D`. Its position was in
screen coordinates and never changed in the recording. A screen's UI can sit at
a steady distance from a UI camera, which is the pattern discovery looks for.

## Decision

An object with a `RectTransform` is never a candidate, neither as one of the
nearest objects nor as their ancestor. The log's observing line says how many
were skipped.

When nothing is left to observe, discovery waits 1 s before it looks again.
Without the wait it would search every object in the scene on every frame, which
a screen made only of UI would now cause.

## Consequences

- The camera root used while discovery runs, or after it gives up, can still be
  a UI object. [ADR 0002](0002-recheck-tag-after-later-picks.md) keeps looking
  for the tag then.
- A `ComponentType`, a tag or a learned type can still select a UI object.
- This sits in each plugin's Unity code, which the smoke test doesn't run.
  The Mono build compiles against a stub, so only a game shows that a Unity type
  resolves there.
