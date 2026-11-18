using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using static System.Math;

// Port of the procedural generation in Abyssal Garden by Maksymilian Dendura (MIT).
internal sealed partial class GardenGenerator
{
    private const int Nx = 512, Ny = 80, Nz = 512, Slice = Nx * Ny;
    private const double VoxelSize = 0.75, Bottom = -56;
    private const int CellCount = GardenWorld.Cells * GardenWorld.Cells;
    private static readonly DVec GridOrigin = new(GardenWorld.Origin, Bottom, GardenWorld.Origin);
    private readonly int _quality;
    private readonly CancellationToken _cancellation;
    private uint _seed;
    private float[] _density;

    private GardenGenerator(int quality, CancellationToken cancellation) =>
        (_quality, _cancellation) = (quality, cancellation);

    public static GardenWorld Generate(int quality, CancellationToken cancellation) =>
        new GardenGenerator(quality, cancellation).Build();

    private GardenWorld Build()
    {
        var timer = Stopwatch.StartNew();
        _density = ComputeDensity(GardenShaders.Density, _cancellation);
        RemoveIsolatedRock();
        _cancellation.ThrowIfCancellationRequested();
        var collision = BuildCollision();
        var terrain = BuildTerrain();
        _cancellation.ThrowIfCancellationRequested();
        GD.Print($"Abyssal Garden terrain generated in {timer.Elapsed.TotalSeconds:F2}s.");
        var views = StartingViews();
        var flight = FlightPath();
        var (lengths, points) = SampleFlight(flight, views);
        var population = Populate(terrain, points, views);
        _density = null;
        var meshes = BuildMeshes();
        GD.Print($"Abyssal Garden world generated in {timer.Elapsed.TotalSeconds:F2}s.");
        return new GardenWorld(views, flight, lengths, terrain.Vertices, terrain.Indices, terrain.Cells,
            meshes.Vertices, meshes.Indices, meshes.Ranges, population.Solids, population.Ranges,
            population.Batches, population.Animated, population.Volume, population.Map,
            collision.Index, collision.Grid);
    }

    private static float[] ComputeDensity(byte[] code, CancellationToken cancellation)
    {
        if (code.Length == 0)
            throw new InvalidOperationException("The compiled seabed shader is missing.");
        RenderingDevice device = RenderingServer.CreateLocalRenderingDevice()
            ?? throw new InvalidOperationException("Could not create a local rendering device for the seabed.");
        Rid shader = default, pipeline = default, settings = default, output = default, rocks = default, set = default;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var bytecode = new RDShaderSpirV { BytecodeCompute = code };
            shader = device.ShaderCreateFromSpirV(bytecode, "Abyssal Garden seabed");
            if (!shader.IsValid)
                throw new InvalidOperationException("Could not load the seabed compute shader.");
            pipeline = device.ComputePipelineCreate(shader);
            if (!pipeline.IsValid)
                throw new InvalidOperationException("Could not create the seabed compute pipeline.");

            int[] dimensions =
            [
                Nx, Ny, Nz, 0, BitConverter.SingleToInt32Bits((float)GridOrigin.X),
                BitConverter.SingleToInt32Bits((float)Bottom), BitConverter.SingleToInt32Bits((float)GridOrigin.Z),
                BitConverter.SingleToInt32Bits((float)VoxelSize), 19, 3, 2, 8
            ];
            float[] primitives =
            [
                -37, -42, -33, -22, 9, -7, 3.5f, 2, -29, -15, -33, 5, 12, -14, 3, 2,
                -36, 11, -30, 27, 9, -5, 4, 2, 30, -45, 35, -27, 9, -9, 4, 2,
                37, -20, 31, -3, 11, -16, 3, 2, 32, 5, 35, 9, 7, -8, 4, 2,
                19, -15, 19, -15, 4.4f, -6, 2, 1.5f, 6, -88, 6, -88, 10, -34, 0, 0,
                0, -106, 0, -106, 8, 3, 2, 2, 44, 6, 47, -8, 7, -17, 3, 1,
                33, 29, 37, 12, 9, -19, -6.5f, 0, 74, 14, 74, 14, 8, -15, 3, 1,
                -84, -34, -80, -12, 10, -7, 6, 2, -78, -6, -83, 12, 12, -13, 5, 2,
                -82, 18, -79, 32, 9, -8, 6, 2, -17, 62, -19, 78, 10, -6, 9, 3,
                -21, 84, -18, 100, 11, -9, 8, 3, 22, 67, 24, 80, 7, -11, 6, 2,
                -16, 122, 34, 122, 9, -27, 3, 2,
                // Arches
                -23, -9.5f, -16, 0.3f, 4.2f, 1.4f, 1.2f, 0,
                0, -45, -34, 1.57f, 14, 2.7f, 1.2f, 0,
                -40, -45, 44, 1.57f, 10, 2.2f, 1.3f, 0,
                // Tunnels
                19, -21, -23, 2.5f, 19, -21, -7, 0, 9, -37, 108, 4.2f, 9, -34, 136, 0,
                // Clearings
                0, -45, 0, 35, 22, 10, 0, 0, 0, -86, 0, -86, 46, 12, 0, 0,
                42, 24, 74, 6, 22, 10, 0, 0, 72, -26, 72, -26, 26, 10, 0, 0,
                -60, -36, -60, 36, 13, 8, 0, 0, -60, 42, -18, 46, 12, 8, 0, 0,
                4, 58, 8, 100, 24, 10, 0, 0, -34, -74, -56, -46, 11, 8, 0, 0
            ];
            settings = device.UniformBufferCreate(48, Pack<int>(dimensions));
            output = device.StorageBufferCreate(Nx * Ny * Nz * sizeof(float));
            rocks = device.StorageBufferCreate((uint)(primitives.Length * sizeof(float)), Pack<float>(primitives));
            if (!settings.IsValid || !output.IsValid || !rocks.IsValid)
                throw new InvalidOperationException("Could not allocate the seabed compute buffers.");
            using var settingsBinding = Binding(RenderingDevice.UniformType.UniformBuffer, 0, settings);
            using var outputBinding = Binding(RenderingDevice.UniformType.StorageBuffer, 1, output);
            using var rocksBinding = Binding(RenderingDevice.UniformType.StorageBuffer, 2, rocks);
            set = device.UniformSetCreate([settingsBinding, outputBinding, rocksBinding], shader, 0);
            if (!set.IsValid)
                throw new InvalidOperationException("Could not bind the seabed compute buffers.");
            // Bound the length of each GPU submission on slower video cards.
            const int layersPerDispatch = 32;
            for (int z = 0; z < Nz; z += layersPerDispatch)
            {
                cancellation.ThrowIfCancellationRequested();
                Error updated = device.BufferUpdate(settings, 3 * sizeof(int), sizeof(int), BitConverter.GetBytes(z));
                if (updated != Error.Ok)
                    throw new InvalidOperationException($"Could not update the seabed compute slice ({updated}).");
                long list = device.ComputeListBegin();
                device.ComputeListBindComputePipeline(list, pipeline);
                device.ComputeListBindUniformSet(list, set, 0);
                device.ComputeListDispatch(list, Nx / 4, Ny / 4, (uint)Min(layersPerDispatch, Nz - z) / 4);
                device.ComputeListEnd();
                device.Submit();
                device.Sync();
            }
            cancellation.ThrowIfCancellationRequested();
            byte[] data = device.BufferGetData(output);
            if (data.Length != Nx * Ny * Nz * sizeof(float))
                throw new InvalidOperationException("The seabed compute readback was incomplete.");
            return MemoryMarshal.Cast<byte, float>(data).ToArray();
        }
        finally
        {
            foreach (Rid resource in new[] { set, rocks, output, settings, pipeline, shader })
                if (resource.IsValid)
                    device.FreeRid(resource);
            device.Free();
        }
    }

    private static RDUniform Binding(RenderingDevice.UniformType type, int index, Rid id)
    {
        var binding = new RDUniform { UniformType = type, Binding = index };
        binding.AddId(id);
        return binding;
    }

    private void RemoveIsolatedRock()
    {
        var connected = new bool[_density.Length];
        var pending = new List<int>();
        Span<bool> contiguous = stackalloc bool[Nx];
        int offset = 0;
        for (int z = 0; z < Nz; z++)
        {
            contiguous.Fill(true);
            for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++, offset++)
            {
                if (_density[offset] <= 0)
                    contiguous[x] = false;
                else if (contiguous[x])
                    connected[offset] = true;
                else
                    pending.Add(offset);
            }
        }

        // Alternate sweep directions until every overhang connected to the bed is marked.
        bool changed;
        bool reverse = false;
        do
        {
            changed = false;
            for (int k = 0; k < pending.Count; k++)
            {
                int i = pending[reverse ? pending.Count - 1 - k : k];
                if (connected[i])
                    continue;
                if (IsConnected(i - 1) || IsConnected(i + 1) || IsConnected(i - Nx) ||
                    IsConnected(i + Nx) || IsConnected(i - Slice) || IsConnected(i + Slice))
                    changed = connected[i] = true;
            }
            reverse = !reverse;
        } while (changed);
        foreach (int i in pending)
            if (!connected[i])
                _density[i] = -_density[i];

        bool IsConnected(int i) => (uint)i < connected.Length && connected[i];
    }

    private (ushort[] Index, sbyte[] Grid) BuildCollision()
    {
        const int width = GardenWorld.CollisionWidth, height = Ny / 4, offset = 52;
        var index = new ushort[width * width * height];
        var bricks = new sbyte[64 * (ushort.MaxValue - 1)];
        int count = 0;
        for (int bz = 0; bz < width; bz++)
        for (int by = 1; by < height - 1; by++)
        for (int bx = 0; bx < width; bx++)
        {
            if (Square(bx - width / 2 + 0.5) + Square(bz - width / 2 + 0.5) > 52 * 52)
                continue;
            int low = 127, high = -127;
            for (int z = 0; z < 4; z++)
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = GridIndex(offset + bx * 4 + x, by * 4 + y, offset + bz * 4 + z);
                double gx = (double)_density[i + 1] - _density[i - 1];
                double gy = (double)_density[i + Nx] - _density[i - Nx];
                double gz = (double)_density[i + Slice] - _density[i - Slice];
                double gradient = Sqrt(gx * gx + gy * gy + gz * gz) / 1.5;
                int q = RoundInt(Clamp(_density[i] / Max(gradient, 0.05), -1, 1) * 127);
                bricks[count * 64 + x + 4 * y + 16 * z] = (sbyte)q;
                low = Min(low, q);
                high = Max(high, q);
            }
            int key = bx + width * (by + height * bz);
            index[key] = low == high && Abs(low) == 127 ? (ushort)(low > 0 ? 1 : 0) : (ushort)(++count + 1);
        }
        return (index, bricks.AsSpan(0, count * 64).ToArray());
    }

    private static GardenWorld.View[] StartingViews() =>
    [
        new(new(10, 1.3f, 7), new(-9, -31, -10)),
        new(new(50, 1.3f, 34), new(58, -23, 8)),
        new(new(-26, 2.2f, -100), new(0, -9, -108)),
        new(new(-55, 1.5f, -18), new(-60, -30, 8)),
        new(new(10, 1.2f, -62), new(6, -22, -90)),
        new(new(3, 1.6f, 62), new(1, -28, 100))
    ];

    private static GardenWorld.View[] FlightPath() =>
    [
        new(new(10, -42.2f, 7), new(-9, -31, -10)),
        new(new(7.5f, -41.8f, 5.5f), new(-10, -31, -12)),
        new(new(9, -40.5f, 18), new(17, -37, 42)),
        new(new(22, -39.2f, 40), new(48, -36, 45)),
        new(new(38, -39.2f, 46), new(57, -27, 18)),
        new(new(50, -40.4f, 35), new(58, -24, 8)),
        new(new(55, -39, 20), new(60, -18, -10)),
        new(new(58, -34, -12), new(40, -26, -60)),
        new(new(50, -30, -44), new(10, -26, -80)),
        new(new(30, -30, -70), new(4, -28, -90)),
        new(new(12, -38.5f, -62), new(6, -27, -90)),
        new(new(-4, -36.5f, -70), new(0, -14, -100)),
        new(new(-16, -37, -84), new(0, -22, -108)),
        new(new(-29, -38, -90), new(-44, -34, -60)),
        new(new(-41, -38, -70), new(-56, -34, -40)),
        new(new(-53, -38, -50), new(-60, -34, -20)),
        new(new(-58, -38, -28), new(-60, -32, 0)),
        new(new(-57, -38, -10), new(-60, -30, 16)),
        new(new(-60, -38, 10), new(-50, -36, 40)),
        new(new(-52, -37, 36), new(-20, -35, 44)),
        new(new(-22, -38, 43), new(4, -38, 52)),
        new(new(-6, -39.2f, 44.5f), new(4, -37, 70)),
        new(new(3, -40.6f, 53), new(3, -35, 84)),
        new(new(3, -41.5f, 62), new(1, -30, 100)),
        new(new(4, -39, 76), new(3, -22, 100))
    ];

    private static (float[] Lengths, DVec[] Points) SampleFlight(GardenWorld.View[] flight, GardenWorld.View[] views)
    {
        const int steps = 4096, tableSize = 1024;
        int segments = flight.Length - 1;
        var distance = new float[steps + 1];
        DVec previous = Position(0);
        for (int k = 1; k <= steps; k++)
        {
            DVec next = Position((double)k / steps * segments);
            distance[k] = (float)(distance[k - 1] + (next - previous).Length());
            previous = next;
        }
        var lengths = new float[tableSize + 1];
        for (int k = 0, j = 0; k <= tableSize; k++)
        {
            double length = (double)k / tableSize * distance[steps];
            while (j < steps - 1 && distance[j + 1] < length)
                j++;
            lengths[k] = (float)((j + (length - distance[j]) / Max((double)distance[j + 1] - distance[j], 1e-6)) / steps * segments);
        }
        var points = new List<DVec>();
        for (double t = 0; t <= 60; t += 0.1)
        {
            double s = t / 60;
            s = (s * s * (3 - 2 * s) * 0.25 + s * 0.75) * tableSize;
            int k = Min((int)s, tableSize - 1);
            points.Add(Position(Mix(lengths[k], lengths[k + 1], s - k)));
        }
        foreach (var view in views)
            points.Add(new(view.Position.X, -40, view.Position.Z));
        return (lengths, points.ToArray());

        DVec Position(double s)
        {
            int i = Min((int)s, segments - 1);
            double u = s - i;
            DVec a = flight[Max(i - 1, 0)].Position, b = flight[i].Position;
            DVec c = flight[i + 1].Position, d = flight[Min(i + 2, segments)].Position;
            return (b * 2 + (c - a) * u + (a * 2 - b * 5 + c * 4 - d) * (u * u)
                + (b * 3 - a - c * 3 + d) * (u * u * u)) * 0.5;
        }
    }
}
