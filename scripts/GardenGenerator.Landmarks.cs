using System;
using System.Collections.Generic;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private MeshData Ship()
    {
        Seed(220);
        var mesh = new MeshBuilder();
        const double length = 17, beam = 5.6, depth = 7.6;
        DVec up = new(0, 1, 0), cross = new(1, 0, 0);
        var keel = new DVec[45];
        for (int i = 0; i < keel.Length; i++)
        {
            double x = Mix(-length - 1, length + 2.4, i / 44.0), e = Max(Abs(x) / length - 0.78, 0) * 4.2;
            keel[i] = new(x, -Draught(Clamp(x, -length, length)) - 0.3 + e * e * e * 4.2, 0);
        }
        mesh.Sweep(keel, 0.55, 0.95, up);
        for (double x = -length + 0.7; x < length - 0.3; x += 0.8)
        {
            double a = Random() < 0.1 ? Range(-0.9, -0.2) : -PI / 2;
            double b = Random() < 0.1 ? Range(0.2, 0.9) : PI / 2;
            var ribs = new DVec[17];
            for (int i = 0; i < ribs.Length; i++)
                ribs[i] = Point(x, Mix(a, b, i / 16.0));
            mesh.Sweep(ribs, _ => 0.24, _ => 0.3, p => new DVec(0, p.Y + 0.5, p.Z * 1.3).Unit());
        }
        double holeX = Range(-6, 2);
        int side = Random() < 0.5 ? 1 : -1;
        (double X, double Angle, double Length, double Width)[] holes =
        [
            (holeX, side * 0.8, 3.2, 0.42), (7, -side * 0.95, 2.4, 0.34),
            (12, side * 0.5, 1.7, 0.28), (-11, -side * 0.55, 2.1, 0.3)
        ];
        const int planks = 14;
        for (int j = 0; j < planks; j++)
        {
            double p0 = Mix(-1.5, 1.5, (double)j / planks), p1 = Mix(-1.5, 1.5, (double)(j + 1) / planks);
            bool port = p0 + p1 < 0;
            double low = port ? p1 : p0, high = port ? p0 : p1;
            int prev = -1;
            for (double x = -length + 0.35; x <= length - 0.3; x += 0.85)
            {
                int current = mesh.Vertex(Point(x, low, 0.27));
                mesh.Vertex(Point(x, high, 0.14));
                mesh.Vertex(Point(x, low, 0.1));
                mesh.Vertex(Point(x, high, 0.1));
                if (prev >= 0 && !HasHole(x - 0.42, (p0 + p1) / 2))
                {
                    if (port)
                    {
                        mesh.Quad(prev, current, current + 1, prev + 1);
                        mesh.Quad(prev + 2, prev + 3, current + 3, current + 2);
                    }
                    else
                    {
                        mesh.Quad(prev, prev + 1, current + 1, current);
                        mesh.Quad(prev + 2, current + 2, current + 3, prev + 3);
                    }
                }
                prev = current;
            }
        }
        for (int sign = -1; sign <= 1; sign += 2)
        {
            var points = new List<DVec>();
            for (double x = -length + 0.6; x <= length - 0.6; x++)
                points.Add(Point(x, sign * PI / 2, 0.2));
            mesh.Sweep(points, 0.3, 0.28, up);
            for (int j = 0; j < 3; j++)
            {
                points.Clear();
                for (double x = -length + 0.4; x <= -length + 8.5 - j; x += 0.9)
                    points.Add(new(x, Sheer(x) + 0.55 + j * 0.86, sign * (Max(HalfBeam(x), 2.6) + 0.15)));
                mesh.Sweep(points, 0.82, 0.09, new(0, 0, sign));
            }
        }
        for (double y = -3; y < Sheer(-length) + 3; y += 0.86)
        {
            double w = Max(1.2, 2.9 * Smooth(-4.5, 1.5, y));
            if (Random() < 0.9)
                mesh.Sweep([new(-length + 0.35, y, -w), new(-length + 0.35, y, w)], 0.82, 0.1, cross);
        }
        for (double x = -length + 2; x < length - 2; x += Range(1.8, 3.2))
            mesh.Sweep([new(x, Sheer(x) - 0.25, -HalfBeam(x)), new(x, Sheer(x) - 0.2, 0),
                new(x, Sheer(x) - 0.25, HalfBeam(x))], 0.26, 0.26, up);
        for (double z = -3.9; z <= 3.9; z += 0.62)
        {
            double x = -length + Range(1, 5);
            while (x < length - 4)
            {
                double end = Min(x + Range(3, 9), length - 3.5);
                if (Random() < 0.6)
                {
                    var deck = new List<DVec>();
                    for (double xx = x; xx <= end; xx++)
                        if (HalfBeam(xx) > Abs(z) + 0.3)
                            deck.Add(new(xx, Sheer(xx) - 0.05, z));
                    if (deck.Count > 1)
                        mesh.Sweep(deck, 0.56, 0.07, up);
                }
                x = end + Range(0.5, 3);
            }
        }
        mesh.Sweep([new(7, -depth + 0.6, 0), new(7.1, 1, 0), new(7.4, 8, 0.3), new(7.8, 15, 0.8)],
            k => 0.62 - k * 0.1, k => 0.62 - k * 0.1, _ => cross);
        mesh.Sweep([new(7.5, 9.5, -5.2), new(7.5, 9.6, 0.3), new(7.5, 9.2, 5.6)], 0.26, 0.26, up);
        mesh.Sweep([new(7.7, 13.2, -3.2), new(7.7, 13.3, 3.6)], 0.2, 0.2, up);
        mesh.Sweep([new(-3, -depth + 0.6, 0), new(-3.2, 1, 0.2), new(-3.9, 6, 1), new(-5.2, 9.5, 2.6)],
            k => 0.74 - k * 0.1, k => 0.74 - k * 0.1, _ => cross);
        mesh.Sweep([new(length + 1.4, 2.6, 0), new(length + 8, 5.2, 0.2)], 0.34, 0.34, up);
        return mesh.Build();

        static double HalfBeam(double x) => beam * Sqrt(Max(1 - Pow(Abs(x / length), x < 0 ? 3.6 : 2.6), 0));
        static double Draught(double x) => depth * (1 - 0.36 * Pow(Abs(x / length), 2.5));
        static double Sheer(double x) => 0.5 + (x > 0 ? 3 : 3.8) * Pow(Abs(x / length), 2.2);
        static DVec Point(double x, double angle, double offset = 0)
        {
            double sheer = Sheer(x);
            return new(x, sheer - (Draught(x) + offset + sheer) * Pow(Max(Cos(angle), 0), 0.66),
                (HalfBeam(x) + offset) * Sign(angle) * Pow(Abs(Sin(angle)), 0.8));
        }

        bool HasHole(double x, double angle)
        {
            foreach (var hole in holes)
                if (Square((x - hole.X) / hole.Length) + Square((angle - hole.Angle) / hole.Width) <
                    1 + 0.45 * Sin(x * 3.1 + angle * 9))
                    return true;
            return false;
        }
    }

    private static MeshData Manta()
    {
        const int span = 72, sides = 22;
        var mesh = new MeshBuilder();
        for (int j = 0; j <= span; j++)
        {
            double u = (double)j / span * 2 - 1, au = Abs(u);
            double front = 0.52 - 0.74 * Pow(au, 1.35) + 0.07 * Exp(-Square((au - 0.13) / 0.05))
                - 0.05 * Exp(-Square(au / 0.05)) - 0.08 * Pow(au, 5);
            double rear = -0.5 + 0.3 * Pow(au, 0.8) + 0.02 - 0.08 * Pow(au, 5);
            double thickness = 0.08 * Pow(1 - au, 1.15) + 0.004 + 0.12 * Exp(-Square(u / 0.2));
            for (int k = 0; k < sides; k++)
            {
                double angle = (double)k / sides * Tau, cosine = Cos(angle);
                mesh.Vertex(new(u, thickness * Sin(angle) * (0.62 + 0.38 * cosine),
                    Mix(rear, Max(front, rear + 0.004), 0.5 + 0.5 * cosine)), u);
            }
        }
        JoinRings(mesh, span + 1, sides);
        Span<DVec> tail = stackalloc DVec[9];
        for (int i = 0; i < tail.Length; i++)
            tail[i] = new(0, 0, -0.42 - i * 0.19);
        mesh.Tube(tail, k => 0.016 * (1 - k / 9.0), 4, aux: _ => 0);
        foreach (int sign in new[] { -1, 1 })
            mesh.Tube([new(sign * 0.12, -0.02, 0.48), new(sign * 0.135, -0.05, 0.6), new(sign * 0.115, -0.09, 0.69),
                new(sign * 0.085, -0.125, 0.74)], k => 0.045 * (1 - k / 4.4), 6, aux: _ => sign * 0.1);
        return mesh.Build();
    }

    private static MeshData Fish()
    {
        var mesh = new MeshBuilder();
        ReadOnlySpan<(double X, double Height, double Width)> profile =
        [
            (0.5, 0.01, 0.01), (0.3, 0.13, 0.05), (0.05, 0.17, 0.065), (-0.22, 0.09, 0.03), (-0.34, 0.035, 0.012)
        ];
        foreach (var (x, height, width) in profile)
        for (int k = 0; k < 6; k++)
        {
            double angle = k / 6.0 * Tau;
            mesh.Vertex(new(x, Sin(angle) * height, Cos(angle) * width));
        }
        JoinRings(mesh, profile.Length, 6);
        int a = mesh.Vertex(new(-0.33, 0, 0)), b = mesh.Vertex(new(-0.56, 0.2, 0));
        int c = mesh.Vertex(new(-0.47, 0, 0)), d = mesh.Vertex(new(-0.56, -0.2, 0));
        mesh.Triangle(a, b, c);
        mesh.Triangle(a, c, d);
        return mesh.Build();
    }
}
