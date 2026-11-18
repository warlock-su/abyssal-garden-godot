using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private sealed class MeshBuilder
    {
        private readonly List<DVec> _points = new();
        private readonly List<int> _indices = new();
        private readonly List<double> _aux = new();
        public int Count => _points.Count;

        public int Vertex(DVec point, double aux = 1)
        {
            int index = _points.Count;
            _points.Add(point);
            _aux.Add(aux);
            return index;
        }

        public void Triangle(int a, int b, int c)
        {
            _indices.Add(a);
            _indices.Add(b);
            _indices.Add(c);
        }

        public void Quad(int a, int b, int c, int d)
        {
            Triangle(a, b, c);
            Triangle(a, c, d);
        }

        public void Sweep(IReadOnlyList<DVec> points, double width, double height, DVec up) =>
            Sweep(points, _ => width, _ => height, _ => up);

        public void Sweep(IReadOnlyList<DVec> points, Func<int, double> width, Func<int, double> height, Func<DVec, DVec> up)
        {
            var rings = new int[points.Count, 8];
            Span<DVec> corners = stackalloc DVec[4];
            Span<int> cap = stackalloc int[4];
            for (int k = 0; k < points.Count; k++)
            {
                DVec p = points[k], tangent = (points[Min(k + 1, points.Count - 1)] - points[Max(k - 1, 0)]).Unit();
                DVec sideDirection = up(p).Cross(tangent).Unit();
                DVec side = sideDirection * (width(k) / 2);
                DVec vertical = tangent.Cross(sideDirection) * (height(k) / 2);
                corners[0] = p - side - vertical;
                corners[1] = p + side - vertical;
                corners[2] = p + side + vertical;
                corners[3] = p - side + vertical;
                for (int s = 0; s < 4; s++)
                {
                    rings[k, s * 2] = Vertex(corners[s]);
                    rings[k, s * 2 + 1] = Vertex(corners[(s + 1) % 4]);
                }
                if (k != 0 && k != points.Count - 1)
                    continue;
                for (int s = 0; s < 4; s++)
                    cap[s] = Vertex(corners[s]);
                if (k == 0)
                    Quad(cap[0], cap[1], cap[2], cap[3]);
                else
                    Quad(cap[3], cap[2], cap[1], cap[0]);
            }
            for (int k = 0; k < points.Count - 1; k++)
            for (int s = 0; s < 4; s++)
                Quad(rings[k, s * 2], rings[k + 1, s * 2], rings[k + 1, s * 2 + 1], rings[k, s * 2 + 1]);
        }

        public void Tube(ReadOnlySpan<DVec> points, Func<int, double> radius, int sides,
            bool cap = true, Func<int, double> aux = null)
        {
            var rings = new int[points.Length, sides];
            DVec up = new(0.31, 0.9, 0.3);
            for (int k = 0; k < points.Length; k++)
            {
                DVec tangent = (points[Min(k + 1, points.Length - 1)] - points[Max(k - 1, 0)]).Unit();
                DVec side = up.Cross(tangent).Unit();
                up = tangent.Cross(side);
                double r = radius(k), value = aux?.Invoke(k) ?? Min(1, 0.35 + 0.65 * k / (points.Length - 1));
                for (int s = 0; s < sides; s++)
                {
                    double angle = (double)s / sides * Tau;
                    rings[k, s] = Vertex(points[k] + (side * Cos(angle) + up * Sin(angle)) * r, value);
                }
            }
            for (int k = 0; k < points.Length - 1; k++)
            for (int s = 0; s < sides; s++)
            {
                int next = (s + 1) % sides;
                Quad(rings[k, s], rings[k, next], rings[k + 1, next], rings[k + 1, s]);
            }
            if (cap)
            {
                int end = points.Length - 1;
                int center = Vertex(points[end], aux?.Invoke(end) ?? 1);
                for (int s = 0; s < sides; s++)
                    Triangle(rings[end, s], rings[end, (s + 1) % sides], center);
            }
        }

        public MeshData Build()
        {
            var normals = new Vector3[Count];
            for (int i = 0; i < _indices.Count; i += 3)
            {
                int a = _indices[i], b = _indices[i + 1], c = _indices[i + 2];
                DVec n = (_points[b] - _points[a]).Cross(_points[c] - _points[a]);
                normals[a] = ((DVec)normals[a] + n).ToVector3();
                normals[b] = ((DVec)normals[b] + n).ToVector3();
                normals[c] = ((DVec)normals[c] + n).ToVector3();
            }
            var packed = new PackedVertex[Count];
            for (int i = 0; i < packed.Length; i++)
            {
                DVec p = _points[i];
                packed[i] = new((float)p.X, (float)p.Y, (float)p.Z, PackNormal(((DVec)normals[i]).Unit(), _aux[i]));
            }
            return new(packed, _indices.ToArray());
        }
    }

    private sealed record MeshData(PackedVertex[] Vertices, int[] Indices);

    private (byte[] Vertices, byte[] Indices, GardenWorld.MeshRange[] Ranges) BuildMeshes()
    {
        // The IDs match the material and LOD tables in GardenRenderer.
        MeshData[] meshes =
        [
            RockMesh(1, 3), RockMesh(7, 3), RockMesh(13, 3), RockMesh(22, 3),
            Lathe([(0, 0, 0.5), (0.34, 0, 0.6), (0.5, 0.2, 0.85), (0.6, 0.6, 1), (0.55, 0.95, 1),
                (0.47, 1.08, 1), (0.38, 1.1, 0.9), (0.3, 1.0, 0.45), (0.26, 0.6, 0.15), (0, 0.35, 0.05)], 18, 0.3, 9),
            Lathe([(0, 0, 0.5), (0.2, 0, 0.6), (0.26, 0.5, 0.85), (0.4, 1.25, 1), (0.4, 1.42, 1),
                (0.33, 1.48, 0.9), (0.26, 1.38, 0.4), (0.18, 0.8, 0.12), (0, 0.5, 0.05)], 16, 0.35, 8),
            Lathe([(0, 0, 0.5), (0.5, 0, 0.7), (0.72, 0.18, 1), (0.74, 0.38, 1), (0.62, 0.5, 0.9),
                (0.48, 0.42, 0.45), (0.4, 0.2, 0.15), (0, 0.14, 0.06)], 18, 0.25, 10),
            Coral(false, _quality == 2), Coral(true, _quality == 2), TubeSponge(), Fluke(), Ship(), Manta(), Fish(),
            RockMesh(3), RockMesh(11), RockMesh(29), RockMesh(5, 3), RockMesh(17, 3), RockMesh(41, 3), RockMesh(9, 1),
            Coral(false, true), Coral(true, true), RockMesh(5, 2, true), RockMesh(17, 2, true), RockMesh(41, 2, true)
        ];
        var ranges = new GardenWorld.MeshRange[meshes.Length];
        int vertex = 0, index = 0;
        for (int i = 0; i < meshes.Length; i++)
        {
            ranges[i] = new(vertex, index, meshes[i].Indices.Length);
            vertex += meshes[i].Vertices.Length;
            index += meshes[i].Indices.Length;
        }
        var vertices = new byte[vertex * 16];
        var indices = new byte[index * sizeof(int)];
        Span<PackedVertex> v = MemoryMarshal.Cast<byte, PackedVertex>(vertices.AsSpan());
        Span<int> ix = MemoryMarshal.Cast<byte, int>(indices.AsSpan());
        for (int i = 0; i < meshes.Length; i++)
        {
            meshes[i].Vertices.CopyTo(v[ranges[i].Vertex..]);
            meshes[i].Indices.CopyTo(ix[ranges[i].Index..]);
        }
        return (vertices, indices, ranges);
    }

    private MeshData RockMesh(int seed, int subdivisions = 2, bool detail = false)
    {
        detail |= subdivisions > 2;
        if (_quality == 2 && subdivisions > 1)
            subdivisions--;
        Seed(100 + seed);
        var cuts = new (DVec Normal, double Distance)[4];
        for (int i = 0; i < cuts.Length; i++)
        {
            DQuat q = RandomRotation();
            cuts[i] = (new DVec(q.X, q.Y, q.Z).Unit(), Range(0.8, 0.98));
        }
        double t = (1 + Sqrt(5)) / 2;
        var vertices = new List<DVec>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1)
        };
        for (int i = 0; i < vertices.Count; i++)
            vertices[i] = vertices[i].Unit();
        var faces = new List<(int A, int B, int C)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4),
            (11, 10, 2), (10, 7, 6), (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
        };
        for (int s = 0; s < subdivisions; s++)
        {
            var cache = new Dictionary<(int, int), int>();
            var next = new List<(int, int, int)>(faces.Count * 4);
            foreach (var (a, b, c) in faces)
            {
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }
            faces = next;

            int Midpoint(int a, int b)
            {
                var key = (Min(a, b), Max(a, b));
                if (cache.TryGetValue(key, out int index))
                    return index;
                index = vertices.Count;
                vertices.Add((vertices[a] + vertices[b]).Unit());
                cache.Add(key, index);
                return index;
            }
        }
        var mesh = new MeshBuilder();
        foreach (var (x, y, z) in vertices)
        {
            double r = 1 + (FractalNoise(x * 1.6 + seed, y * 1.6, z * 1.6, 2) - 0.5) * 0.9;
            if (detail)
                r += (FractalNoise(x * 4 + seed, y * 4, z * 4, 2) - 0.5) * 0.25;
            DVec p = new(x * r, y * r * 0.8, z * r);
            foreach (var (n, offset) in cuts)
            {
                double d = p.X * n.X + p.Y * n.Y + p.Z * n.Z - offset;
                if (d > 0)
                    p -= n * d;
            }
            mesh.Vertex(p);
        }
        foreach (var (a, b, c) in faces)
            mesh.Triangle(a, b, c);
        return mesh.Build();
    }

    private static MeshData Lathe(ReadOnlySpan<(double Radius, double Y, double Aux)> profile, int sides, double uneven, int ribs)
    {
        var mesh = new MeshBuilder();
        foreach (var (radius, y, aux) in profile)
        for (int s = 0; s < sides; s++)
        {
            double a = (double)s / sides * Tau, x = Cos(a), z = Sin(a);
            double r = radius * (1 + uneven * (FractalNoise(x * 1.5 + y * 2, y * 3, z * 1.5, 2) - 0.5) + 0.07 * Cos(ribs * a + y * 1.5));
            mesh.Vertex(new(x * r, y, z * r), aux);
        }
        JoinRings(mesh, profile.Length, sides);
        return mesh.Build();
    }

    private static void JoinRings(MeshBuilder mesh, int count, int sides)
    {
        for (int k = 0; k < count - 1; k++)
        for (int s = 0; s < sides; s++)
        {
            int a = k * sides, b = a + sides, next = (s + 1) % sides;
            mesh.Quad(a + s, b + s, b + next, a + next);
        }
    }

    private MeshData Coral(bool fan, bool low)
    {
        Seed(200 + (fan ? 1 : 0));
        var mesh = new MeshBuilder();
        int count = fan ? 1 : 2 + (int)(Random() * 2);
        for (int i = 0; i < count; i++)
            Branch(new(Range(-0.08, 0.08), 0, Range(-0.08, 0.08)),
                new DVec(Range(-0.5, 0.5), 1, fan ? 0 : Range(-0.5, 0.5)).Unit(), fan ? 0.06 : 0.08, fan ? 0.5 : 0.45, 3);
        return mesh.Build();

        void Branch(DVec p, DVec direction, double radius, double length, int depth)
        {
            Span<DVec> path = stackalloc DVec[5];
            path[0] = p;
            DVec end = p;
            for (int k = 1; k <= 3; k++)
            {
                direction = (direction + new DVec(Range(-0.3, 0.3), Range(-0.1, 0.3),
                    fan ? Range(-0.05, 0.05) : Range(-0.3, 0.3))).Unit();
                end += direction * length / 3;
                path[k] = end;
            }
            path[4] = end + direction * radius * 1.6;
            if (low)
            {
                path[1] = end;
                path[2] = path[4];
                mesh.Tube(path[..(depth == 0 ? 3 : 2)], k => (k > 1 ? radius * 0.36 : radius * (1 - 0.22 * k)) * 1.25,
                    3, aux: k => Clamp(1 - (depth + 1 - k) / 5.0, 0, 1));
            }
            else
                mesh.Tube(path[..(depth == 0 ? 5 : 4)], k => k > 3 ? radius * 0.36 : radius * (1 - 0.22 * k / 3),
                    fan ? 5 : 6, aux: k => Clamp(1 - (depth + 1 - k / 3.0) / 5, 0, 1));
            if (depth == 0)
                return;
            int branches = 2 + (Random() < 0.5 ? 1 : 0);
            for (int i = 0; i < branches; i++)
                Branch(end, (direction + new DVec(Range(-0.8, 0.8), Range(-0.1, 0.5),
                    fan ? Range(-0.08, 0.08) : Range(-0.8, 0.8))).Unit(), radius * 0.78, length * 0.8, depth - 1);
        }
    }

    private MeshData TubeSponge()
    {
        Seed(210);
        var mesh = new MeshBuilder();
        Span<DVec> points = stackalloc DVec[5];
        for (int i = 0; i < 9; i++)
        {
            double a = Random() * Tau, r = Range(0, 0.28), h = Range(0.45, 1.1), radius = Range(0.07, 0.12), lean = Range(0.05, 0.3);
            for (int k = 0; k <= 4; k++)
                points[k] = new(Cos(a) * (r + lean * k / 4 * h), h * k / 4, Sin(a) * (r + lean * k / 4 * h));
            mesh.Tube(points, k => radius * (0.8 + 0.25 * k / 4), 12, false, k => 0.35 + 0.65 * k / 4);
            int ring = mesh.Count - 12;
            int center = mesh.Vertex(points[4] - new DVec(0, radius * 1.2, 0), 0.05);
            for (int s = 0; s < 12; s++)
                mesh.Triangle(ring + s, ring + (s + 1) % 12, center);
        }
        return mesh.Build();
    }

    private MeshData Fluke()
    {
        var capsules = new List<(DVec Start, DVec Axis, double InverseLengthSquared, double RadiusA, double RadiusB, double Squash)>();
        Chain([(0, -1.6, 3.7, 1.2), (0, 1.8, 2.35, 0.98), (0, 4.6, 1.0, 0.76), (0, 6.9, 0.1, 0.6)], 0.8);
        Chain([(0, 6.6, 0.1, 0.72), (1.9, 7.9, 0, 0.98), (3.9, 8.8, -0.1, 0.9), (5.7, 9.8, -0.25, 0.64),
            (7, 11.2, -0.4, 0.38), (7.7, 12.8, -0.55, 0.2), (7.9, 14.1, -0.65, 0.1)], 0.18);
        Chain([(0, 6.6, 0.1, 0.72), (-1.7, 8.3, 0.05, 0.96), (-3.3, 9.7, -0.05, 0.88), (-4.7, 11.2, -0.2, 0.64),
            (-5.6, 12.9, -0.4, 0.38), (-6, 14.6, -0.5, 0.2), (-5.9, 15.9, -0.6, 0.1)], 0.18);
        const int nx = 186, ny = 216, nz = 76;
        const double voxel = 0.09, e = 0.7;
        DVec origin = new(-7.6, -2.6, -2.2);
        var field = new float[nx * ny * nz];
        var segments = capsules.ToArray();
        Parallel.For(0, nz, new ParallelOptions { CancellationToken = _cancellation }, z =>
        {
            double pz = origin.Z + z * voxel;
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                double px = origin.X + x * voxel, py = origin.Y + y * voxel, density = 1e9;
                foreach (var (start, axis, inverse, ra, rb, squash) in segments)
                {
                    DVec p = new DVec(px, py, pz / squash) - start;
                    double t = Clamp((p.X * axis.X + p.Y * axis.Y + p.Z * axis.Z) * inverse, 0, 1);
                    double d = ((p - axis * t).Length() - Mix(ra, rb, t)) * squash;
                    double h = Max(0.45 - Abs(density - d), 0) / 0.45;
                    density = Min(density, d) - h * h * 0.45 * 0.25;
                }
                field[x + nx * (y + ny * z)] = (float)-density;
            }
        });
        var (points, quads) = SurfaceNets(field, nx, ny, nz);
        var vertices = new PackedVertex[points.Count];
        var indices = new int[quads.Count * 6];
        for (int i = 0; i < points.Count; i++)
        {
            DVec p = points[i], w = origin + p * voxel;
            DVec normal = new DVec(
                At(p.X - e, p.Y, p.Z) - At(p.X + e, p.Y, p.Z),
                At(p.X, p.Y - e, p.Z) - At(p.X, p.Y + e, p.Z),
                At(p.X, p.Y, p.Z - e) - At(p.X, p.Y, p.Z + e)).Unit();
            vertices[i] = new((float)w.X, (float)w.Y, (float)w.Z, PackNormal(normal));
        }
        for (int i = 0; i < quads.Count; i++)
            WriteQuad(indices.AsSpan(i * 6, 6), quads[i]);
        return new(vertices, indices);

        double At(double x, double y, double z) => Sample(field, nx, ny, nz, x, y, z);

        void Chain(ReadOnlySpan<(double X, double Y, double Z, double Radius)> points, double squash)
        {
            for (int i = 0; i < points.Length - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                DVec start = new(a.X, a.Y, a.Z / squash), axis = new(b.X - a.X, b.Y - a.Y, b.Z / squash - start.Z);
                capsules.Add((start, axis, 1 / (Square(axis.X) + Square(axis.Y) + Square(axis.Z)), a.Radius, b.Radius, squash));
            }
        }
    }
}
