using System;
using Godot;

internal sealed class GardenWorld
{
    public const int Cells = 16;
    public const int CellSize = 24;
    public const int Origin = -192;
    public const int CollisionWidth = 102;
    private const int CollisionHeight = 20;

    public readonly record struct MeshRange(int Vertex, int Index, int Count);
    public readonly record struct InstanceRange(int First, int Count);
    public readonly record struct View(Vector3 Position, Vector3 Target);
    public sealed record CellsData(int[] Start, float[] Low, float[] High);
    public sealed record Batch(byte[] Instances, CellsData Cells);

    public View[] Views { get; }
    public View[] Flight { get; }
    public float[] FlightLengths { get; }
    public byte[] TerrainVertices { get; private set; }
    public byte[] TerrainIndices { get; private set; }
    public byte[] MeshVertices { get; private set; }
    public byte[] MeshIndices { get; private set; }
    public MeshRange[] Meshes { get; }
    public byte[] Solids { get; private set; }
    public InstanceRange[] SolidRanges { get; }
    public CellsData TerrainCells { get; }
    public Batch[] Batches { get; }
    public byte[][] Animated { get; }
    public byte[] Density { get; private set; }
    public byte[] SurfaceMap { get; private set; }
    private readonly ushort[] _collisionIndex;
    private readonly sbyte[] _collisionGrid;

    public GardenWorld(View[] views, View[] flight, float[] flightLengths,
        byte[] terrainVertices, byte[] terrainIndices, CellsData terrainCells,
        byte[] meshVertices, byte[] meshIndices, MeshRange[] meshes,
        byte[] solids, InstanceRange[] solidRanges, Batch[] batches, byte[][] animated,
        byte[] density, byte[] surfaceMap, ushort[] collisionIndex, sbyte[] collisionGrid)
    {
        Views = views;
        Flight = flight;
        FlightLengths = flightLengths;
        TerrainVertices = terrainVertices;
        TerrainIndices = terrainIndices;
        TerrainCells = terrainCells;
        MeshVertices = meshVertices;
        MeshIndices = meshIndices;
        Meshes = meshes;
        Solids = solids;
        SolidRanges = solidRanges;
        Batches = batches;
        Animated = animated;
        Density = density;
        SurfaceMap = surfaceMap;
        _collisionIndex = collisionIndex;
        _collisionGrid = collisionGrid;
    }

    public void ReleaseUploadData()
    {
        TerrainVertices = TerrainIndices = MeshVertices = MeshIndices = Solids = Density = SurfaceMap = [];
        for (int i = 0; i < Batches.Length; i++)
            Batches[i] = new([], Batches[i].Cells);
        Array.Clear(Animated);
    }

    public float DistanceToRock(Vector3 camera)
    {
        Vector3 grid = (camera - new Vector3(Origin, -56, Origin)) / 0.75f - new Vector3(52, 0, 52);
        int x = Mathf.FloorToInt(grid.X);
        int y = Mathf.FloorToInt(grid.Y);
        int z = Mathf.FloorToInt(grid.Z);
        float u = grid.X - x;
        float v = grid.Y - y;
        float w = grid.Z - z;
        float a = Mathf.Lerp(Voxel(x, y, z), Voxel(x + 1, y, z), u);
        float b = Mathf.Lerp(Voxel(x, y + 1, z), Voxel(x + 1, y + 1, z), u);
        float c = Mathf.Lerp(Voxel(x, y, z + 1), Voxel(x + 1, y, z + 1), u);
        float d = Mathf.Lerp(Voxel(x, y + 1, z + 1), Voxel(x + 1, y + 1, z + 1), u);
        return Mathf.Lerp(Mathf.Lerp(a, b, v), Mathf.Lerp(c, d, v), w) / 127;
    }

    private int Voxel(int x, int y, int z)
    {
        int bx = x >> 2, by = y >> 2, bz = z >> 2;
        if ((uint)bx >= CollisionWidth || (uint)by >= CollisionHeight || (uint)bz >= CollisionWidth)
            return -127;
        int brick = _collisionIndex[bx + CollisionWidth * (by + CollisionHeight * bz)];
        return brick < 2 ? brick * 254 - 127 :
            _collisionGrid[(brick - 2) * 64 + (x & 3) + 4 * (y & 3) + 16 * (z & 3)];
    }

}
