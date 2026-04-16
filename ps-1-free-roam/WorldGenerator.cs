using Godot;

public partial class WorldGenerator : Node3D
{
    [Export] public NodePath PlayerPath { get; set; }

    private Node3D        _player;
    private int           _nextChunk = 0;
    private System.Random _rng       = new System.Random();

    // Each chunk is a 60-unit deep slice of the world stretching in -Z
    private const float ChunkDepth  = 60f;
    private const float ShapeSpread = 80f;  // shapes placed within ±40 on X axis
    private const float SpawnAhead  = 90f;  // spawn next chunk when player is this close
    private const int   Prewarm     = 5;    // chunks generated before the player moves

    public override void _Ready()
    {
        _player = GetNode<Node3D>(PlayerPath);
        for (int i = 0; i < Prewarm; i++)
            SpawnChunk(_nextChunk++);
    }

    public override void _Process(double delta)
    {
        // Far edge of the last spawned chunk
        float frontierZ = -(_nextChunk * ChunkDepth);
        if (_player.GlobalPosition.Z < frontierZ + SpawnAhead)
            SpawnChunk(_nextChunk++);
    }

    // -------------------------------------------------------------------------

    private void SpawnChunk(int index)
    {
        float nearZ = -(index * ChunkDepth);
        float farZ  = nearZ - ChunkDepth;

        int count = _rng.Next(3, 8);
        for (int i = 0; i < count; i++)
        {
            float x = Rng(-ShapeSpread * 0.5f, ShapeSpread * 0.5f);
            float z = Rng(farZ + 3f, nearZ - 3f);  // stay clear of chunk edges
            PlaceShape(x, z);
        }
    }

    private void PlaceShape(float x, float z)
    {
        float w, h, d;

        switch (_rng.Next(3))
        {
            case 0:  // chunky block
                w = Rng(4f, 12f);
                h = Rng(2f,  6f);
                d = Rng(4f, 12f);
                break;
            case 1:  // tall pillar
                w = Rng(1f,  3f);
                h = Rng(5f, 10f);
                d = Rng(1f,  3f);
                break;
            default: // wide flat slab — walkable surface
                w = Rng(8f, 18f);
                h = Rng(0.5f, 2f);
                d = Rng(6f, 14f);
                break;
        }

        MakeBox(new Vector3(x, h * 0.5f, z), new Vector3(w, h, d));
    }

    private void MakeBox(Vector3 position, Vector3 size)
    {
        var body = new StaticBody3D();
        body.Position = position;

        var meshInst = new MeshInstance3D();
        meshInst.Mesh = new BoxMesh { Size = size };
        body.AddChild(meshInst);

        var col = new CollisionShape3D();
        col.Shape = new BoxShape3D { Size = size };
        body.AddChild(col);

        AddChild(body);
    }

    private float Rng(float min, float max)
        => (float)(_rng.NextDouble() * (max - min) + min);
}
