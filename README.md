# AvaWorld

The world Ava lives in. A separate application from the companion — the companion is the brain, and
this is the place. Neither contains the other.

## Status

**Step one of six.** The world is a headless server with a clock that keeps running when nothing is
watching it, saves itself atomically, and is honest about time it was not running. There is no
geometry, no client, and no companion connection yet. Those are steps two through six.

That order is deliberate: "always running" is a property that decays quietly if it isn't designed
for, and retrofitting it once logic lives in the scene tree is the expensive version.

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

## Design

The full design — what the world is for, how the companion connects, presence rules, and the
remaining steps — lives in [`docs/WORLD.md`](../Persisten_AI/docs/WORLD.md) in the companion repo.
It moves here once there is more world than companion in it.
