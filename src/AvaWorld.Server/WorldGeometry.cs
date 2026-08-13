using AvaWorld.Simulation;
using Godot;

namespace AvaWorld.Server;

/// <summary>
/// Builds the physical world from the layout in <see cref="Cottage"/>.
///
/// Generated in code rather than authored as a .tscn, on purpose for now: the geometry and the
/// world's beliefs about where places are come from one source, so they cannot drift. A room moved
/// in the layout moves the floor you walk on and the volume the server tests against, together.
///
/// This is a stepping stone. Real geometry — modelled rooms, furniture, light — comes from Blender
/// as glTF later, at which point the layout keeps defining the footprints and the art hangs off it.
/// </summary>
public static class WorldGeometry
{
    private const float FloorThickness = 0.4f;

    /// <summary>
    /// Builds the floors — one slab per place, one per doorway — plus a catch volume, parented to
    /// <paramref name="root"/>.
    ///
    /// There are no walls yet, and nothing above knee height at all. What you walk on is exactly
    /// the footprint the server tests positions against, so the world you see *is* the layout
    /// rather than a picture of it. Walls would be built from these same bounds with gaps at the
    /// doorways.
    /// </summary>
    public static void Build(Node3D root, bool withVisuals)
    {
        var map = Cottage.Map();

        foreach (var bounds in map.Bounds)
            AddSlab(root, bounds, withVisuals, RoomColour(bounds.PlaceId));

        foreach (var doorway in Cottage.Doorways())
            AddSlab(root, doorway.Bounds, withVisuals, new Color(0.32f, 0.30f, 0.28f));

        // A body that leaves the floor should come back, not fall forever. Cheaper and less
        // fragile than fencing every edge while the layout is still changing shape.
        AddCatchVolume(root);

        if (withVisuals)
            AddLighting(root);
    }

    /// <summary>Where a body should appear when it first enters the world.</summary>
    public static Vector3 SpawnPoint()
    {
        var hall = Cottage.Map().For(Cottage.Spawn)!.Value;
        return new Vector3(hall.CentreX, 1.2f, hall.CentreZ);
    }

    /// <summary>
    /// A visible stand-in for Ava on the client, until there is a model of her.
    ///
    /// Deliberately reads as a <em>marker</em> rather than a person. An earlier version was a
    /// flesh-toned capsule, which managed to be person-coloured without being a person — the
    /// uncanny worst of both. Cool and clearly synthetic is the honest placeholder: it says "she
    /// is here" without inviting anyone to read personality into what is still a walk between
    /// rooms.
    ///
    /// It also has to be findable. The rooms are twelve metres across and unlit apart from the sun,
    /// so the marker carries its own soft light — otherwise "where is she?" is answered by
    /// wandering around until you bump into her.
    ///
    /// Replacing this with a real model is a change to this method and nothing else. Nothing about
    /// how she moves, or what the world believes, touches her appearance.
    /// </summary>
    public static Node3D BuildAvaStandIn()
    {
        var root = new Node3D { Name = "Ava" };

        var presence = new Color(0.42f, 0.72f, 0.78f); // cool, obviously not skin

        root.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Position = new Vector3(0, 0.9f, 0),
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.3f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = presence,
                Metallic = 0.1f,
                Roughness = 0.45f,
                EmissionEnabled = true,
                Emission = presence,
                EmissionEnergyMultiplier = 0.35f,
            },
        });

        // A small mote above her, so she can be picked out across a room or through a doorway.
        root.AddChild(new MeshInstance3D
        {
            Name = "Mote",
            Position = new Vector3(0, 2.15f, 0),
            Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = presence,
                EmissionEnabled = true,
                Emission = new Color(0.75f, 0.95f, 1.0f),
                EmissionEnergyMultiplier = 2.5f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });

        root.AddChild(new OmniLight3D
        {
            Name = "Presence",
            Position = new Vector3(0, 1.5f, 0),
            LightColor = presence,
            LightEnergy = 1.4f,
            OmniRange = 7f,
        });

        return root;
    }

    /// <summary>The centre of a place, as somewhere to stand in it.</summary>
    public static Vector3? CentreOf(string placeId)
    {
        var bounds = Cottage.Map().For(placeId);
        return bounds is null ? null : new Vector3(bounds.Value.CentreX, 1.2f, bounds.Value.CentreZ);
    }

    private static void AddSlab(Node3D root, PlaceBounds bounds, bool withVisuals, Color colour)
    {
        var body = new StaticBody3D
        {
            Name = $"floor_{Sanitise(bounds.PlaceId)}",
            Position = new Vector3(bounds.CentreX, -FloorThickness / 2f, bounds.CentreZ),
        };

        var size = new Vector3(bounds.Width, FloorThickness, bounds.Depth);

        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });

        if (withVisuals)
        {
            body.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = colour },
            });
        }

        root.AddChild(body);
    }

    /// <summary>
    /// A large trigger well below the floor. Anything reaching it is falling, and gets put back in
    /// the hall rather than accelerating into the void.
    /// </summary>
    private static void AddCatchVolume(Node3D root)
    {
        var area = new Area3D { Name = "FellOutOfTheWorld", Position = new Vector3(0, -20, -15) };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 4, 200) } });
        root.AddChild(area);
    }

    private static void AddLighting(Node3D root)
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55, -35, 0),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
        };
        root.AddChild(sun);

        var environment = new WorldEnvironment
        {
            Name = "Sky",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.6f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
            },
        };
        root.AddChild(environment);
    }

    /// <summary>Rooms are told apart by floor colour until there is real art.</summary>
    private static Color RoomColour(string placeId) => placeId switch
    {
        Cottage.Hall => new Color(0.45f, 0.42f, 0.40f),
        Cottage.Kitchen => new Color(0.55f, 0.45f, 0.35f),
        Cottage.Study => new Color(0.35f, 0.33f, 0.42f),
        Cottage.Greenhouse => new Color(0.35f, 0.50f, 0.38f),
        Cottage.Garden => new Color(0.30f, 0.48f, 0.28f),
        _ => new Color(0.4f, 0.4f, 0.4f),
    };

    private static string Sanitise(string id) => id.Replace(':', '_').Replace('-', '_');
}
