# 2. The tag is checked again while a later pick is tracked

Date: 2026-11-27. Status: accepted, confirmed in Torque Drift (IL2CPP), where
the plugin left `Panel`, and later `Fader(Clone)`, for the tagged car. The Mono
plugin has the same change and hasn't run it in a game.

## Context

The pawn is looked for again only when there is none, or when it came from the
tag. In two Torque Drift sessions the `Player` tag lookup gave nothing at startup.
In one, automatic discovery gave up and the plugin stayed on the root of a
loading screen camera, `Fader(Clone)`. In the other, discovery chose `Panel`, a
UI element. Neither moved, and neither log shows a later change.

## Decision

Unless `Pawn.ComponentType` is set, look for the tag every 2 s while the pawn
came from anything else: the camera root, a learned type or discovery. A tagged
object replaces it. The tag already comes before those in the lookup order.

The log says every 30 s when there's still no tagged object, and once if the
lookup throws.

## Consequences

- A game that gets its tagged object after startup is picked up without a
  restart.
- A tagged object replaces a discovered one, even one that moves.
- A game without the tag does one tag search every 2 s.
- This decision sits in each plugin's Unity code, which the smoke test doesn't
  run.
