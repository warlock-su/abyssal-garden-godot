using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Godot;

internal sealed class GardenWorld
{
    public const int Cells = 16;
    public const int CellSize = 24;
    public const int Origin = -192;
    private const int CollisionWidth = 102;
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
    public MeshRange[] Meshes { get; } = new MeshRange[26];
    public byte[] Solids { get; private set; }
    public InstanceRange[] SolidRanges { get; } = new InstanceRange[12];
    public CellsData TerrainCells { get; }
    public Batch[] Batches { get; } = new Batch[16];
    public byte[][] Animated { get; } = new byte[5][];
    public byte[] Density { get; private set; }
    public byte[] SurfaceMap { get; private set; }
    private readonly ushort[] _collisionIndex;
    private readonly sbyte[] _collisionGrid;

    public GardenWorld(int quality)
    {
        using var stream = OpenWorld(quality);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        if (reader.ReadUInt32() != 0x31594241)
            throw new InvalidDataException("Unrecognized Abyssal Garden world format.");

        Views = ReadViews(reader);
        Flight = ReadViews(reader);
        FlightLengths = ReadArray<float>(reader);
        TerrainVertices = ReadBlock(reader);
        TerrainIndices = ReadBlock(reader);
        MeshVertices = ReadBlock(reader);
        MeshIndices = ReadBlock(reader);
        var meshes = ReadArray<int>(reader);
        for (int i = 0; i < Meshes.Length; i++)
            Meshes[i] = new(meshes[3 * i], meshes[3 * i + 1], meshes[3 * i + 2]);
        Solids = ReadBlock(reader);
        var ranges = ReadArray<int>(reader);
        for (int i = 0; i < SolidRanges.Length; i++)
            SolidRanges[i] = new(ranges[2 * i], ranges[2 * i + 1]);
        TerrainCells = ReadCells(reader);
        for (int i = 0; i < Batches.Length; i++)
            Batches[i] = new(ReadBlock(reader), ReadCells(reader));
        for (int i = 0; i < Animated.Length; i++)
            Animated[i] = ReadBlock(reader);
        Density = ReadBlock(reader);
        SurfaceMap = ReadBlock(reader);
        _collisionIndex = ReadArray<ushort>(reader);
        _collisionGrid = ReadArray<sbyte>(reader);
    }

    private static Stream OpenWorld(int quality)
    {
        string resource = $"res://assets/abyssal/world-{quality}.gz";
        string file = ProjectSettings.GlobalizePath(resource);
        if (File.Exists(file))
            return new FileStream(file, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);
        byte[] data = Godot.FileAccess.GetFileAsBytes(resource);
        if (data.Length == 0)
            throw new IOException($"Missing world data for quality {quality}.");
        return new MemoryStream(data, writable: false);
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

    private static View[] ReadViews(BinaryReader reader)
    {
        var values = ReadArray<float>(reader);
        var views = new View[values.Length / 6];
        for (int i = 0; i < views.Length; i++)
        {
            int o = 6 * i;
            views[i] = new(new(values[o], values[o + 1], values[o + 2]),
                new(values[o + 3], values[o + 4], values[o + 5]));
        }
        return views;
    }

    private static CellsData ReadCells(BinaryReader reader) =>
        new(ReadArray<int>(reader), ReadArray<float>(reader), ReadArray<float>(reader));

    private static T[] ReadArray<T>(BinaryReader reader) where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(ReadBlock(reader)).ToArray();

    private static byte[] ReadBlock(BinaryReader reader)
    {
        int size = reader.ReadInt32();
        if (size < 0 || size > 256 * 1024 * 1024)
            throw new InvalidDataException($"Invalid world block size {size}.");
        var data = GC.AllocateUninitializedArray<byte>(size);
        reader.BaseStream.ReadExactly(data);
        return data;
    }
}
