# AvaWorld

The world Ava lives in. A separate application from the companion — the companion is the brain, and
this is the place. Neither contains the other.

## Status

**Step two of six.** The world is a headless server that keeps running when nothing is watching it,
with five connected places you can walk around as a client. It knows who is in which room, records
what happened, and remembers all of it across a restart.

No companion connection yet — that is steps four and five, and it is when the world starts earning
its place rather than just existing.

## Layout

```
src/AvaWorld.Simulation   the world itself — plain C#, no Godot, no rendering
src/AvaWorld.Server       the Godot host — scheduling and I/O only
tests/…Simulation.Tests   13 tests, none of which need a display
```

### The rule this layout enforces

> The server must produce identical world history with no client ever connected.

`AvaWorld.Simulation` must never reference Godot. That is not a convention, it is the reason the
projects are split: Godot makes the wrong thing easy — simulation logic drifts into `_process` on
visual nodes, and one day the world only advances while someone is looking at it. Making the
reference impossible is more durable than remembering not to add it.

If you find yourself wanting a `Node` in the simulation project, the logic belongs in the server —
or, more likely, the design has drifted.

## Running it

```powershell
dotnet build
.\run-server.ps1
```

The script finds Godot, refuses to launch under a build that can't run C#, and starts the server. It
prints where the world file is, whether it created or resumed a world, and how long it was away.
`Ctrl+C` to stop; start it again and it picks up where it left off.

Point it at a specific Godot with `-Godot <path>` or by setting `$env:GODOT`.

To start a new world, delete `world.json`. To keep it elsewhere, set `AVAWORLD_STATE`.

### Walking around in it

With a server running, connect a client from a second terminal:

```powershell
& $env:GODOT --path src\AvaWorld.Server --client
```

WASD to walk, mouse to look, `Escape` to release the mouse, click to take it back. Add
`--host=<addr>` to reach a world on another machine, `--port=<n>` if you moved it.

Server and client are the same binary in two roles, so the layout and the wire contract cannot
drift apart. They are still separate processes: the server is authoritative and does not care
whether anyone is connected.

### Checking it works without a screen

```powershell
& $env:GODOT --headless --path src\AvaWorld.Server --walk
```

Connects, tours every room in the layout, and exits. The server should log a room change for each
one. This exercises the whole loop — connect, move, resolve a room, record it — which is otherwise
the only part that needs a human at a display.

### You need the .NET build of Godot

Godot ships two Windows builds and only one runs C#. With the wrong one you get:

```
ERROR: No loader found for resource: res://Main.cs (expected type: Script)
ERROR: res://Main.tscn:6 - Parse Error: [ext_resource] referenced non-existent resource
```

**That is not a broken project.** It is Godot not knowing what a `.cs` file is, because the build was
compiled without the .NET module. The give-away is the version banner:

```
4.7.1.stable.mono.official   ← correct
4.7.1.stable.official        ← no C# support
```

Get the **.NET** download from [godotengine.org/download](https://godotengine.org/download).
`run-server.ps1` checks this before launching so the failure says what it actually is — the error
Godot gives points nowhere near the cause.

If you run Godot by hand instead, use the `_console` executable on Windows; the plain one detaches
from the terminal and you will not see the world's log at all:

```powershell
Godot_v4.7.1-stable_mono_win64_console.exe --headless --path src/AvaWorld.Server
```

## Tests

```bash
dotnet test
```

None of them launch Godot, which is the point — the world's behaviour is testable without a display,
including the eight-hours-passed case, which runs in milliseconds against a fake clock.

## The layout

Five places — hall, kitchen, study, greenhouse, garden — defined once in `Cottage.cs` and used
twice: the engine builds floors from it, and the server resolves "which room is this body in" from
it. One source, so the geometry and the world's beliefs cannot disagree.

Small on purpose. What makes a world feel inhabited is consequence and persistence, not extent —
the basil you saw wilting yesterday being dead today. Five rooms is enough to be somewhere and to
have a reason to move, and few enough that each can earn its place before a sixth is added.

Place resolution lives in the simulation rather than the engine, because deciding what the world
believes about where everyone is deserves tests, and point-in-rectangle is not a rendering concern.
Standing between rooms resolves to *no place*, which is a real answer: a body in a doorway keeps
the room it came from rather than having one invented for it.

## What it does today

- Creates a world, or resumes the saved one.
- Advances on a timer, measuring elapsed time rather than assuming it, so tick rate doesn't change
  how much time passed.
- Saves atomically on every tick — temp file, flush to disk, then rename — because being killed
  mid-save is the *normal* way this process ends, not a rare accident. A recovered temp file is
  preferred over starting from nothing.
- Records downtime as downtime. **Nothing is ever generated for a gap.** A plausible eight hours is
  easy to synthesise, makes the world seem more alive, and would be indistinguishable from real
  history once written — which is exactly the confabulation the companion's design exists to
  prevent. If the world wasn't running on Tuesday afternoon, there is nothing to say about Tuesday
  afternoon.
- Distinguishes "nothing happened then" from "there is no record of then" (`World.WasRunningAt`).
- Tracks who is in which room, and records arrivals, joins and departures as world events. Ava is
  where she was when the world stopped, not at a spawn point — she lives here.
- Refuses to start on an inconsistent layout: a room with no floor, a footprint naming a place that
  does not exist, or two rooms claiming the same ground. That last one would make place resolution
  depend on declaration order, which presents as a haunting.

## Not yet true

Worth stating plainly rather than discovering later:

- **Movement is client-reported.** The server owns occupancy and decides which room a position is
  in, but it does not validate the position itself. Fine for one trusted user on a private world;
  it is not a defence against a modified client, and step three's authentication is the point at
  which that starts to matter.
- **There is no authentication on the wire.** The world was local when this was designed and is
  now not. Anything that can reach the port can join.
- **Ava has no body.** She exists in the world's beliefs and is in a room, but nothing represents
  her in the scene and nothing moves her. That is step three.

## Design

The full design — what the world is for, how the companion connects, presence rules, and the
remaining steps — lives in [`docs/WORLD.md`](../Persisten_AI/docs/WORLD.md) in the companion repo.
It moves here once there is more world than companion in it.
