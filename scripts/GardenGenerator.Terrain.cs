using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private const double Tau = 2 * PI;

    private readonly record struct DVec(double X, double Y, double Z)
    {
        public static implicit operator DVec(Vector3 v) => new(v.X, v.Y, v.Z);
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
        public static DVec operator +(DVec a, DVec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVec operator -(DVec a, DVec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVec operator *(DVec v, double s) => new(v.X * s, v.Y * s, v.Z * s);
        public static DVec operator /(DVec v, double s) => new(v.X / s, v.Y / s, v.Z / s);
        public double Length() => Sqrt(X * X + Y * Y + Z * Z);
        public DVec Unit()
        {
            double length = Length();
            return length == 0 ? this : this / length;
        }
        public DVec Cross(DVec b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    }

    private readonly record struct DQuat(double X, double Y, double Z, double W)
    {
        public DQuat Unit()
        {
            double length = Sqrt(X * X + Y * Y + Z * Z + W * W);
            return length == 0 ? this : new(X / length, Y / length, Z / length, W / length);
        }

        public static DQuat operator *(DQuat a, DQuat b) => new(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }

    private static DQuat AxisAngle(double x, double y, double z, double angle)
    {
        DVec axis = new DVec(x, y, z).Unit() * Sin(angle / 2);
        return new(axis.X, axis.Y, axis.Z, Cos(angle / 2));
    }

    private static DQuat AlignUp(DVec n) => n.Y < -0.9999 ? new(1, 0, 0, 0) : new DQuat(n.Z, 0, -n.X, 1 + n.Y).Unit();
    private DQuat RandomRotation() => new DQuat(Range(-1, 1), Range(-1, 1), Range(-1, 1), Range(-1, 1)).Unit();
    private static double Square(double x) => x * x;
    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static int RoundInt(double x) => (int)Floor(x + 0.5);
    private static double Smooth(double a, double b, double x)
    {
        double t = Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static double Hypot(double x, double z) => Sqrt(x * x + z * z);
    private static int GridIndex(int x, int y, int z) => x + Nx * (y + Ny * z);
    private static byte[] Pack<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();

    private void Seed(int seed) => _seed = unchecked((uint)(20261110 + seed * 7919));

    private double Random()
    {
        unchecked
        {
            _seed += 0x6D2B79F5;
            uint t = (_seed ^ (_seed >> 15)) * (_seed | 1);
            t = (t + (t ^ (t >> 7)) * (t | 61)) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    private double Range(double a, double b) => Mix(a, b, Random());

    private static double Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h ^ (h >> 16)) / 4294967296.0;
        }
    }

    private static double FractalNoise(double x, double y, double z, int octaves = 4)
    {
        double amplitude = 0.5, sum = 0;
        for (int i = 0; i < octaves; i++)
        {
            int ix = (int)Floor(x), iy = (int)Floor(y), iz = (int)Floor(z);
            double u = Smooth(0, 1, x - ix), v = Smooth(0, 1, y - iy), w = Smooth(0, 1, z - iz);
            double a = Mix(Mix(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), u),
                Mix(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), u), v);
            double b = Mix(Mix(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), u),
                Mix(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), u), v);
            sum += amplitude * Mix(a, b, w);
            x = x * 2.03 + 17.1;
            y = y * 2.03 + 31.7;
            z = z * 2.03 + 5.3;
            amplitude *= 0.5;
        }
        return sum / (1 - 2 * amplitude);
    }

    private double Sample(double x, double y, double z) => Sample(_density, Nx, Ny, Nz, x, y, z);

    private static double Sample(float[] field, int nx, int ny, int nz, double x, double y, double z)
    {
        x = Clamp(x, 0, nx - 1.001);
        y = Clamp(y, 0, ny - 1.001);
        z = Clamp(z, 0, nz - 1.001);
        int ix = (int)x, iy = (int)y, iz = (int)z;
        double u = x - ix, v = y - iy, w = z - iz;
        int i = ix + nx * (iy + ny * iz);
        double a = Mix(Mix(field[i], field[i + 1], u), Mix(field[i + nx], field[i + nx + 1], u), v);
        i += nx * ny;
        double b = Mix(Mix(field[i], field[i + 1], u), Mix(field[i + nx], field[i + nx + 1], u), v);
        return Mix(a, b, w);
    }

    private double Field(DVec p)
    {
        DVec grid = (p - GridOrigin) / VoxelSize;
        return Sample(grid.X, grid.Y, grid.Z);
    }

    private DVec Normal(DVec p, double e) => new DVec(
        Sample(p.X - e, p.Y, p.Z) - Sample(p.X + e, p.Y, p.Z),
        Sample(p.X, p.Y - e, p.Z) - Sample(p.X, p.Y + e, p.Z),
        Sample(p.X, p.Y, p.Z - e) - Sample(p.X, p.Y, p.Z + e)).Unit();

    private static int Cell(double x, double z) =>
        Clamp((int)Floor((x - GridOrigin.X) / GardenWorld.CellSize), 0, GardenWorld.Cells - 1)
        + GardenWorld.Cells * Clamp((int)Floor((z - GridOrigin.Z) / GardenWorld.CellSize), 0, GardenWorld.Cells - 1);

    private readonly record struct PackedVertex(float X, float Y, float Z, uint Normal);
    private readonly record struct Quad(int A, int B, int C, int D, bool Positive);
    private sealed record Terrain(byte[] Vertices, byte[] Indices, GardenWorld.CellsData Cells,
        PackedVertex[] Points, Vector3[] Normals);

    private static uint PackNormal(DVec normal, double aux = 1) =>
        (uint)(byte)RoundInt(normal.X * 127) | (uint)(byte)RoundInt(normal.Y * 127) << 8 |
        (uint)(byte)RoundInt(normal.Z * 127) << 16 | (uint)(byte)RoundInt(Clamp(aux, -1, 1) * 127) << 24;

    private static float[] CellBounds(float initial)
    {
        var bounds = new float[CellCount];
        Array.Fill(bounds, initial);
        return bounds;
    }

    private static (List<Vector3> Points, List<Quad> Quads) SurfaceNets(float[] field, int nx, int ny, int nz)
    {
        int slice = nx * ny;
        var points = new List<Vector3>(nx * nz * 2);
        var quads = new List<Quad>(points.Capacity);
        int[] current = new int[slice], previous = new int[slice];
        Span<double> corners = stackalloc double[8];
        ReadOnlySpan<(int A, int B)> edges =
        [
            (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7),
            (0, 4), (1, 5), (2, 6), (3, 7)
        ];
        for (int z = 0; z < nz - 1; z++)
        {
            for (int y = 0; y < ny - 1; y++)
            {
                int start = nx * (y + ny * z);
                for (int x = 0; x < nx - 1; x++)
                {
                    int i = start + x;
                    double a = field[i], b = field[i + 1], c = field[i + nx], d = field[i + nx + 1];
                    double e = field[i + slice], f = field[i + slice + 1];
                    double g = field[i + slice + nx], h = field[i + slice + nx + 1];
                    bool solid = a > 0;
                    if ((b > 0) == solid && (c > 0) == solid && (d > 0) == solid &&
                        (e > 0) == solid && (f > 0) == solid && (g > 0) == solid && (h > 0) == solid)
                        continue;
                    corners[0] = a; corners[1] = b; corners[2] = c; corners[3] = d;
                    corners[4] = e; corners[5] = f; corners[6] = g; corners[7] = h;
                    double sx = 0, sy = 0, sz = 0;
                    int count = 0;
                    foreach (var edge in edges)
                    {
                        double left = corners[edge.A], right = corners[edge.B];
                        if ((left > 0) == (right > 0))
                            continue;
                        double t = left / (left - right);
                        int axis = edge.A ^ edge.B;
                        sx += axis == 1 ? t : edge.A & 1;
                        sy += axis == 2 ? t : (edge.A >> 1) & 1;
                        sz += axis == 4 ? t : edge.A >> 2;
                        count++;
                    }
                    int v = points.Count;
                    points.Add(new((float)(x + sx / count), (float)(y + sy / count), (float)(z + sz / count)));
                    int q = x + nx * y;
                    current[q] = v;
                    if ((b > 0) != solid && y > 0 && z > 0)
                        quads.Add(new(v, current[q - nx], previous[q - nx], previous[q], solid));
                    if ((c > 0) != solid && x > 0 && z > 0)
                        quads.Add(new(v, previous[q], previous[q - 1], current[q - 1], solid));
                    if ((e > 0) != solid && x > 0 && y > 0)
                        quads.Add(new(v, current[q - 1], current[q - 1 - nx], current[q - nx], solid));
                }
            }
            (current, previous) = (previous, current);
        }
        return (points, quads);
    }

    private Terrain BuildTerrain()
    {
        var (grid, quads) = SurfaceNets(_density, Nx, Ny, Nz);
        var vertices = new PackedVertex[grid.Count];
        var normals = new Vector3[grid.Count];
        for (int v = 0; v < grid.Count; v++)
        {
            DVec p = GridOrigin + (DVec)grid[v] * VoxelSize;
            DVec n = Normal(grid[v], 0.8);
            vertices[v] = new((float)p.X, (float)p.Y, (float)p.Z, PackNormal(n));
            normals[v] = n.ToVector3();
        }
        var starts = new int[CellCount + 1];
        var low = CellBounds(1e9f);
        var high = CellBounds(-1e9f);
        var keys = new byte[quads.Count];
        for (int i = 0; i < quads.Count; i++)
        {
            var q = quads[i];
            var p = vertices[q.A];
            int cell = Cell(p.X, p.Z);
            keys[i] = (byte)cell;
            starts[cell + 1]++;
            float b = vertices[q.B].Y, c = vertices[q.C].Y, d = vertices[q.D].Y;
            low[cell] = Min(low[cell], Min(Min(p.Y, b), Min(c, d)));
            high[cell] = Max(high[cell], Max(Max(p.Y, b), Max(c, d)));
        }
        for (int c = 0; c < CellCount; c++)
            starts[c + 1] += starts[c];
        int[] next = (int[])starts.Clone();
        byte[] indices = GC.AllocateUninitializedArray<byte>(quads.Count * 6 * sizeof(int));
        Span<int> destination = MemoryMarshal.Cast<byte, int>(indices.AsSpan());
        for (int i = 0; i < quads.Count; i++)
        {
            var q = quads[i];
            int j = next[keys[i]]++ * 6;
            WriteQuad(destination.Slice(j, 6), q);
        }
        return new(Pack<PackedVertex>(vertices), indices, new(starts, low, high), vertices, normals);
    }

    private static void WriteQuad(Span<int> indices, Quad q)
    {
        indices[0] = indices[3] = q.A;
        indices[1] = q.Positive ? q.B : q.C;
        indices[2] = q.Positive ? q.C : q.B;
        indices[4] = q.Positive ? q.C : q.D;
        indices[5] = q.Positive ? q.D : q.C;
    }
}
