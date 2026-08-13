# AvaWorld

The world Ava lives in. A separate application from the companion — the companion is the brain, and
this is the place. Neither contains the other.

## Status

**Step five of six.** The world is a headless server that keeps running when nothing is watching it,
with five connected places, a client you can walk around in, and Ava living in it. **A brain can
now steer her**: the wire is open, it takes instructions, and it reports what happens.

The companion is connected: she moves for her own reasons, and can say what they were. What remains
is step six — world events reaching her memory and reflection, so the world improves her continuity
rather than only her whereabouts.

## Layout

```
src/AvaWorld.Simulation   the world itself — plain C#, no Godot, no rendering
src/AvaWorld.Wire         the companion's channel — protocol and WebSocket, still no Godot
src/AvaWorld.Poke         a stand-in for the companion, for driving the world by hand
src/AvaWorld.Server       the Godot host — scheduling and I/O only
tests/…Simulation.Tests   58 tests, none of which need a display
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
.\start-all.ps1
```

That brings up all three pieces, in order, and skips any that are already running:

| | |
|---|---|
| **world server** | the place. Headless, keeps going when nothing is watching. |
| **companion** | her mind. Decides where she goes and why. |
| **client** | a window to watch through. Optional — the world does not care whether anyone is looking. |

`.\stop-all.ps1` stops everything. Nothing is lost: the world saves on every tick, and a restart
inside two minutes is not even recorded as time away.

**She will mostly stand still, and that is correct.** Her policy only moves her when there is a
reason — something on her mind, or something in a room that needs looking after. A world with no
companion attached and a companion with nothing to do look identical from inside the game, so the
companion's window is where you find out which: it logs every decision and the reason for it.

Useful switches: `-NoClient` to run headless, `-NoCompanion` to watch the world without her brain
(she falls back to drifting at random, which is the placeholder rather than her deciding anything),
and `-Companion <path>` if the companion repo is not a sibling of this one.

### Running the pieces separately

```powershell
.\run-server.ps1
```

Starts just the world in the foreground, which is the way to see why it will not start. It prints
where the world file is, whether it created or resumed a world, and how long it was away. `Ctrl+C`
to stop; start it again and it picks up where it left off.

Point it at a specific Godot with `-Godot <path>` or by setting `$env:GODOT`.

To start a new world, delete `world.json`. To keep it elsewhere, set `AVAWORLD_STATE`.

### Walking around in it

With a server running, open a window onto it from a second terminal:

```powershell
.\run-client.ps1
```

WASD to walk, mouse to look, `Escape` to release the mouse, click to take it back.

For a world on another machine, give it the address and that world's token:

```powershell
.\run-client.ps1 -Host 192.168.1.20 -Token <the world's token>
```

The client is only a viewer. Closing it changes nothing about the world except who is in it.

Ava is the capsule wandering between rooms. She is a shape rather than a character on purpose:
giving her a face before she has a mind invites reading personality into what is currently a random
walk. The `.glb` from the companion's avatar work drops in without changing how she moves.

### The token

The world is a long-running service, so joining requires a shared secret — on **both** channels,
the rendering clients' and the brain's. It is generated on first run and written to
`.avaworld-token` beside the world file, so a client on the same machine reads it and needs no
configuration.

For a world on another machine, set `AVAWORLD_TOKEN` to the same value on both ends. The token file
is gitignored; it is a key to a running world, not source.

Server and client are the same binary in two roles, so the layout and the wire contract cannot
drift apart. They are still separate processes: the server is authoritative and does not care
whether anyone is connected.

### The wire

The companion's channel, on the port above the client's (8738 by default). Plain WebSocket and JSON
rather than Godot's multiplayer, because the brain renders nothing and needs events in and
intentions out — which also means it never links a Godot assembly, and anything that speaks
WebSocket can drive the world.

`ava-poke` is a stand-in for the companion, for driving her by hand:

```powershell
dotnet run --project src\AvaWorld.Poke
```

Type a place name to send her there, or `places`, `where`, `stop`, `quit`. One-shot, for scripts:

```powershell
dotnet run --project src\AvaWorld.Poke -- --say=garden --listen=30000
```

**What the world says.** On authenticating it sends `hello`, carrying the whole menu of places and
what they adjoin. The companion is told this every time it connects, precisely so it never needs to
store a layout that may have changed. Then `arrived`, `presence`, and `refusal` as things happen.

**What the world listens to.** `goto` a place, `where`, `places`, `stop`. Note what is missing:
there is no way to say where she should *stand*, only which place she should be *in*. Intentions
are goals, never motion — the world keeps "how", and no message shape exists that would let a brain
take that back.

```
< {"type":"hello","you":"ava","place":"study","places":[…],"actions":["goto",…]}
> {"type":"goto","place":"garden"}
< {"type":"arrived","body":"ava","place":"hall","at":"…"}
< {"type":"arrived","body":"ava","place":"kitchen","at":"…"}
< {"type":"arrived","body":"ava","place":"garden","at":"…"}
```

A brain taking control retires the wandering placeholder — once something is deciding, nothing else
should be.

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

- **`Wandering` is still here.** It picks a random room every twenty seconds, and exists only so the
  world is not inert with nobody deciding. A connected brain retires it immediately and it returns
  when she is left alone, so it no longer competes — but the class should eventually go, not grow.
- **The world is floor slabs.** Five coloured rectangles and four corridor strips, no walls, no
  ceiling, nothing above knee height. More to the point there are no *objects*: nothing in any room
  changes over time, so nothing can be tended, missed, or noticed. The design's claim is that
  consequence and persistence are what make a world feel inhabited rather than extent — which means
  a tomato plant that dries out over days would do more here than walls.
- **Movement is client-reported.** The server owns occupancy and decides which room a position is
  in, but does not validate the position itself. Fine for a trusted user on a private world; it is
  not a defence against a modified client.
- **There is no navmesh.** Routing is room-to-room through doorways, which is the right
  granularity for empty rooms. Once there is furniture to walk around, a navmesh belongs
  *underneath* this — steering between the same waypoints, not replacing them.
- **Nobody can see the guests.** Ava is drawn on every client, but other people's players are not.
  Harmless while there is one of you.

## Design

The full design — what the world is for, how the companion connects, presence rules, and the
remaining steps — lives in [`docs/WORLD.md`](../Persisten_AI/docs/WORLD.md) in the companion repo.
It moves here once there is more world than companion in it.
