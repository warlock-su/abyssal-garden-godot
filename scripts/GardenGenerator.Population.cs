using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private readonly record struct Leaf(float X, float Y, float Z, float Length,
        float Yaw, float Tilt, float Width, float Height, uint Traits);
    private readonly record struct Solid(float X, float Y, float Z, float Scale,
        float Qx, float Qy, float Qz, float Qw, float Material, float Seed, float TextureScale, float Bob);
    private readonly record struct Particle(float X, float Y, float Z, float A, float B, float C, float D, float E);
    private sealed record Population(byte[] Solids, GardenWorld.InstanceRange[] Ranges,
        GardenWorld.Batch[] Batches, byte[][] Animated, byte[] Volume, byte[] Map);

    private Terrain _terrain;
    private DVec[] _flightPoints;
    private GardenWorld.View[] _views;
    private List<Leaf>[] _bigLeaves, _smallLeaves;
    private List<Solid>[] _solids;
    private List<Solid>[][] _props, _chunks;
    private List<Particle> _glints, _bubbles;
    private List<DVec> _spongeVents;
    private float[] _kelpAbsorption;
    private int _instanceOrdinal;

    private static List<T>[] Buckets<T>(int count = CellCount)
    {
        var result = new List<T>[count];
        for (int i = 0; i < count; i++)
            result[i] = new();
        return result;
    }

    private static List<Solid>[][] SolidBatches(int count)
    {
        var result = new List<Solid>[count][];
        for (int i = 0; i < count; i++)
            result[i] = Buckets<Solid>();
        return result;
    }

    private Population Populate(Terrain terrain, DVec[] flight, GardenWorld.View[] views)
    {
        _terrain = terrain;
        _flightPoints = flight;
        _views = views;
        _bigLeaves = Buckets<Leaf>();
        _smallLeaves = Buckets<Leaf>();
        _solids = Buckets<Solid>(12);
        _props = SolidBatches(6);
        _chunks = SolidBatches(7);
        _glints = new();
        _bubbles = new();
        _spongeVents = new();
        _kelpAbsorption = new float[Nx * Ny * Nz / 8];

        GrowKelp();
        _cancellation.ThrowIfCancellationRequested();
        GrowRibbons();
        GrowGroundCover();
        _cancellation.ThrowIfCancellationRequested();
        GrowSponges();
        ScatterRocks();
        _cancellation.ThrowIfCancellationRequested();
        PlaceLandmarks();
        AddBubbles();

        for (int i = 0; i < views.Length; i++)
        {
            Vector3 p = views[i].Position;
            p.Y = (float)(Ground(p.X, p.Z) + ViewHeights[i]);
            views[i] = views[i] with { Position = p };
        }

        var volumes = BuildLightVolume();
        _cancellation.ThrowIfCancellationRequested();
        var batches = new GardenWorld.Batch[16];
        for (int i = 0; i < _chunks.Length; i++)
            batches[i] = PackBatch(_chunks[i], 1, 1, p => p.Y, _ => 3f);
        for (int i = 0; i < _props.Length; i++)
            batches[7 + i] = PackBatch(_props[i], 1, 1, p => p.Y, _ => 3f);
        batches[13] = PackBatch(_bigLeaves, 1, 1, p => p.Y, p => p.Length);
        batches[14] = PackBatch(_bigLeaves, 2, 1.5f, p => p.Y, p => p.Length);
        batches[15] = PackBatch(_smallLeaves, 1, 1, p => p.Y, p => p.Length);
        var ranges = new GardenWorld.InstanceRange[12];
        var solids = new List<Solid>();
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = new(solids.Count, _solids[i].Count);
            solids.AddRange(_solids[i]);
        }
        solids.Add(new(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0));
        return new(Pack<Solid>(CollectionsMarshal.AsSpan(solids)), ranges, batches, Animate(), volumes, BuildGroundMap());
    }

    private static readonly double[] ViewHeights = [1.3, 1.3, 2.2, 1.5, 1.2, 1.6];

    private bool KeepInstance() => (_instanceOrdinal++ & ((1 << _quality) - 1)) == 0;

    private void AddLeaf(List<Leaf>[] leaves, double x, double y, double z, double length,
        double yaw, double tilt, double width, double height, int type, double seed1, double seed2, double bend)
    {
        if (type != 1 && !KeepInstance())
            return;
        double scaledWidth = type == 1 ? width : width * (1 + _quality / 2.0);
        uint traits = (uint)type | (uint)(seed1 * 255) << 8 | (uint)(seed2 * 255) << 16 | (uint)(bend * 255) << 24;
        leaves[Cell(x, z)].Add(new((float)x, (float)y, (float)z, (float)length, (float)yaw,
            (float)tilt, (float)scaledWidth, (float)height, traits));
    }

    private void AddGlint(double x, double y, double z, double height, double seed, double hue, double size)
    {
        if (KeepInstance())
            _glints.Add(new((float)x, (float)y, (float)z, (float)height, (float)seed, (float)hue, (float)size, 0));
    }

    private static Solid SolidInstance(DVec p, double size, DQuat q, int material, double seed, int textureScale, double bob) =>
        new((float)p.X, (float)p.Y, (float)p.Z, (float)size, (float)q.X, (float)q.Y, (float)q.Z, (float)q.W,
            material, (float)seed, textureScale, (float)bob);

    private void AddSolid(int mesh, DVec p, double size, DQuat q, int material, double seed, double bob = 0)
    {
        var bucket = mesh is > 3 and < 10 ? _props[mesh - 4][Cell(p.X, p.Z)] : _solids[mesh];
        bucket.Add(SolidInstance(p, size, q, material, seed, 1, bob));
    }

    private (double X, double Z) RelativeToView(int view, double forward, double right)
    {
        var pose = _views[view];
        double x = pose.Target.X - pose.Position.X, z = pose.Target.Z - pose.Position.Z;
        double l = Hypot(x, z);
        x /= l;
        z /= l;
        return (pose.Position.X + x * forward - z * right, pose.Position.Z + z * forward + x * right);
    }

    private readonly double[] _floors = new double[Ny];

    private int FindFloors(double x, double z)
    {
        int ix = Clamp(RoundInt((x - GridOrigin.X) / VoxelSize), 1, Nx - 2);
        int iz = Clamp(RoundInt((z - GridOrigin.Z) / VoxelSize), 1, Nz - 2);
        int count = 0;
        double ceiling = 3;
        for (int y = Ny - 2; y >= 0; y--)
        {
            int i = GridIndex(ix, y, iz);
            double d = _density[i], above = _density[i + Nx];
            if (above <= 0 && d > 0)
            {
                double floor = Bottom + (y + d / (d - above)) * VoxelSize;
                _floors[count++] = floor;
                _floors[count++] = Min(ceiling, 0) - floor;
            }
            else if (above > 0 && d <= 0)
                ceiling = Bottom + (y + 0.5) * VoxelSize;
        }
        return count;
    }

    private double Ground(double x, double z)
    {
        int count = FindFloors(x, z);
        return count == 0 ? -45 : _floors[count - 2];
    }

    private DVec Gradient(DVec p) => new DVec(
        Field(p - new DVec(0.6, 0, 0)) - Field(p + new DVec(0.6, 0, 0)),
        Field(p - new DVec(0, 0.6, 0)) - Field(p + new DVec(0, 0.6, 0)),
        Field(p - new DVec(0, 0, 0.6)) - Field(p + new DVec(0, 0, 0.6))).Unit();

    private bool SnapToRock(DVec origin, DVec normal, out DVec position, out DVec gradient)
    {
        position = gradient = default;
        DVec p = origin + normal * 2.5;
        double previous = Field(p);
        if (previous > 0)
            return false;
        for (int i = 0; i < 24; i++)
        {
            DVec q = p - normal * 0.25;
            double density = Field(q);
            if (density > 0)
            {
                position = p + (q - p) * (previous / (previous - density));
                gradient = Gradient(position);
                return true;
            }
            p = q;
            previous = density;
        }
        return false;
    }

    private (DVec Point, DVec Normal) PickTerrain()
    {
        int k = (int)(Random() * _terrain.Points.Length);
        var p = _terrain.Points[k];
        return (new(p.X, p.Y, p.Z), _terrain.Normals[k]);
    }

    private bool NearFlight(DVec p, double radius, bool ignoreHeight = false)
    {
        double r2 = radius * radius;
        foreach (var sample in _flightPoints)
            if ((ignoreHeight || p.Y < sample.Y + 3 && p.Y > sample.Y - 13) &&
                Square(p.X - sample.X) + Square(p.Z - sample.Z) < r2)
                return true;
        return false;
    }

    private bool NearView(double x, double z, double radius = 3)
    {
        foreach (var view in _views)
            if (Square(x - view.Position.X) + Square(z - view.Position.Z) < radius * radius)
                return true;
        return false;
    }

    private static readonly (double X, double Z, double Radius)[] MagentaPatches =
    [
        (14, 6, 13), (-4, 20, 9), (6, 84, 30), (-58, 8, 10), (50, 30, 12), (8, -70, 10), (-12, -98, 9),
        (-36, 44, 9), (-46, -60, 9), (24, 42, 8), (-56, 26, 9), (-44, 38, 8), (-10, 44, 8), (12, 24, 8), (-50, -45, 8)
    ];

    private static readonly (double X, double Z, double Radius)[] Hotspots =
    [
        (0, 5, 30), (42, 30, 24), (6, 82, 34), (-58, 10, 24), (4, -88, 30), (-36, 44, 18), (-46, -60, 20)
    ];

    private static double Purple(double x, double z) => Smooth(44, 26, Hypot(x - 6, z - 84));

    private static double Magenta(double x, double z)
    {
        double m = Smooth(0.54, 0.66, FractalNoise(x * 0.03, 7, z * 0.03, 3)) * 0.9;
        foreach (var p in MagentaPatches)
            m = Max(m, Smooth(p.Radius, p.Radius * 0.45, Hypot(x - p.X, z - p.Z)));
        return m * Smooth(0.3, 0.55, FractalNoise(x * 0.17, 1, z * 0.17, 2) + m * 0.25);
    }

    private static double Hot(double x, double z)
    {
        double h = 0.4;
        foreach (var p in Hotspots)
            h = Max(h, Smooth(p.Radius, p.Radius * 0.4, Hypot(x - p.X, z - p.Z)));
        return h;
    }

    private static GardenWorld.Batch PackBatch<T>(List<T>[] cells, int step, float width,
        Func<T, float> height, Func<T, float> extent) where T : unmanaged
    {
        var starts = new int[CellCount + 1];
        var low = CellBounds(1e9f);
        var high = CellBounds(-1e9f);
        for (int c = 0; c < CellCount; c++)
            starts[c + 1] = starts[c] + (cells[c].Count + step - 1) / step;
        var data = GC.AllocateUninitializedArray<byte>(starts[^1] * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        Span<T> destination = MemoryMarshal.Cast<byte, T>(data.AsSpan());
        for (int c = 0; c < CellCount; c++)
        {
            var source = CollectionsMarshal.AsSpan(cells[c]);
            int k = starts[c];
            for (int i = 0; i < source.Length; i += step)
            {
                T value = source[i];
                destination[k++] = value;
                float y = height(value), l = extent(value);
                low[c] = Min(low[c], y - l);
                high[c] = Max(high[c], y + l);
            }
        }
        if (width != 1)
        {
            Span<Leaf> leaves = MemoryMarshal.Cast<byte, Leaf>(data.AsSpan());
            for (int i = 0; i < leaves.Length; i++)
                leaves[i] = leaves[i] with { Width = leaves[i].Width * width };
        }
        return new(data, new(starts, low, high));
    }

    private byte[] BuildLightVolume()
    {
        const int width = Nx / 2, height = Ny / 2, depth = Nz / 2;
        var volume = new byte[width * height * depth * 2];
        var previous = new float[width * depth];
        var next = new float[previous.Length];
        var horizontal = new float[previous.Length];
        Array.Fill(previous, 1);
        float[] field = _density;
        for (int y = height - 1; y >= 0; y--)
        {
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                int i = x + width * z;
                horizontal[i] = (float)((previous[x > 0 ? i - 1 : i] + 2.0 * previous[i] + previous[x < width - 1 ? i + 1 : i]) * 0.25);
            }
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                int i = x + width * z, g = GridIndex(x * 2, y * 2, z * 2);
                double light = (horizontal[z > 0 ? i - width : i] + 2.0 * horizontal[i] + horizontal[z < depth - 1 ? i + width : i]) * 0.25;
                double density = ((double)field[g] + field[g + 1] + field[g + Nx] + field[g + Nx + 1] +
                    field[g + Slice] + field[g + Slice + 1] + field[g + Slice + Nx] + field[g + Slice + Nx + 1]) * 0.125;
                int offset = x + width * (y + height * z);
                next[i] = (float)(light * Clamp(0.5 - density / 1.5, 0, 1) * (1 - Min((double)_kelpAbsorption[offset], 0.5)));
                volume[offset * 2] = (byte)(Clamp(0.5 - density / 12, 0, 1) * 255);
                volume[offset * 2 + 1] = (byte)(Min(1, light) * 255);
            }
            (previous, next) = (next, previous);
        }
        return volume;
    }

    private static byte[] BuildGroundMap()
    {
        var map = new byte[Nx * Nz * 4];
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++)
        {
            double x = GridOrigin.X + (i + 0.5) * VoxelSize, z = GridOrigin.Z + (j + 0.5) * VoxelSize;
            int offset = (i + Nx * j) * 4;
            map[offset] = (byte)(Magenta(x, z) * 255);
            map[offset + 2] = (byte)(Purple(x, z) * 255);
            map[offset + 3] = (byte)(Smooth(0.42, 0.6, FractalNoise(x * 0.04, 3, z * 0.04, 3)) * 255);
        }
        return map;
    }
}
