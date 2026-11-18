using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private static readonly (double X, double Z, double Radius, double Weight)[] SpongeZones =
    [
        (-58, 8, 30, 1), (0, 0, 30, 0.7), (6, 84, 36, 0.9), (4, -88, 22, 0.6), (44, 20, 24, 0.5)
    ];
    private static readonly (double X, double Z, double Radius, int Count)[] SpongeGardens =
    [
        (-68, -6, 9, 260), (-66, 6, 7, 160), (16, 72, 7, 200), (-6, 74, 6, 120), (-13, -2, 6, 80),
        (8, -88, 8, 160), (44, 12, 7, 120), (-62, 22, 6, 140), (-47, 40, 6, 140), (-30, 47, 6, 120),
        (-12, 48, 5, 100), (14, 28, 6, 140), (28, 45, 6, 120), (-36, -76, 6, 130), (-49, -58, 6, 130), (-58, -38, 6, 120)
    ];

    private void GrowSponges()
    {
        Seed(6);
        for (int i = 0; i < 13000; i++)
        {
            var (p, n) = PickTerrain();
            if (Hypot(p.X, p.Z) > 170 || p.Y > -8 || n.Y < -0.15 || Random() > Weight(p.X, p.Z) || NearFlight(p, 3))
                continue;
            DVec tangent = (Abs(n.Y) < 0.9 ? new DVec(n.Z, 0, -n.X) : new DVec(1, 0, 0)).Unit();
            DVec bitangent = n.Cross(tangent);
            bool coral = n.Y > 0.85 || Random() < 0.35;
            int count = coral ? (int)(2 + Random() * 5) : (int)(3 + Random() * 9);
            double seed = Random();
            for (int j = 0; j < count; j++)
            {
                double a = Range(-1, 1) * (0.5 + count * 0.13), b = Range(-1, 1) * (0.5 + count * 0.13);
                if (!SnapToRock(p + tangent * a + bitangent * b, n, out var location, out var normal))
                    continue;
                DVec up = (normal + new DVec(0, coral ? 0.9 : 1.1, 0)).Unit();
                DQuat q = AlignUp(up) * AxisAngle(0, 1, 0, Random() * Tau);
                double scale = coral ? Range(0.5, 1.5) : Range(0.35, 0.9) * (Random() < 0.1 ? 1.6 : 1);
                int mesh = (coral ? 7 : 4) + (int)(Random() * 3);
                double variation = seed + Range(-0.03, 0.03);
                AddSolid(mesh, location - up * (0.12 * scale), scale, q, coral ? 5 : 4, variation - Floor(variation));
            }
            if (!coral && Random() < 0.3)
                _spongeVents.Add(p + n + new DVec(0, 0.5, 0));
        }

        Seed(7);
        foreach (var garden in SpongeGardens)
        {
            int made = 0;
            for (int i = 0; i < 400000 && made < garden.Count; i++)
            {
                var (p, n) = PickTerrain();
                if (Hypot(p.X - garden.X, p.Z - garden.Z) > garden.Radius || p.Y > -14 || n.Y < -0.1 || NearFlight(p, 2.2))
                    continue;
                made++;
                bool coral = n.Y > 0.85 || Random() < 0.3;
                DVec up = (n + new DVec(0, coral ? 0.9 : 1.1, 0)).Unit();
                double scale = coral ? Range(0.5, 1.4) : Range(0.35, 0.9) * (Random() < 0.15 ? 1.7 : 1);
                AddSolid((coral ? 7 : 4) + (int)(Random() * 3), p - up * (0.1 * scale), scale,
                    AlignUp(up) * AxisAngle(0, 1, 0, Random() * Tau), coral ? 5 : 4, Random());
            }
        }

        static double Weight(double x, double z)
        {
            double weight = 0.45;
            foreach (var p in SpongeZones)
                weight = Max(weight, p.Weight * Smooth(p.Radius, p.Radius * 0.4, Hypot(x - p.X, z - p.Z)));
            return weight;
        }
    }

    private void ScatterRocks()
    {
        var groups = new List<(double X, double Y, double Z, int Count, double Radius)>
        {
            (-11, -11, -105, 14, 5), (-56, -19, -18, 18, 6), (10, -13, -88, 22, 7),
            (24, -15, -24, 22, 7), (-18, -12, 4, 14, 6), (30, -14, 30, 16, 6),
            (36, -12, -70, 22, 8), (-30, -13, -84, 16, 7), (-58, -14, 0, 14, 6),
            (19, -13, 98, 18, 6), (62, -9, 34, 14, 6)
        };
        Seed(8);
        for (int i = 0; i < 9; i++)
        {
            double angle = Random() * Tau, r = Range(60, 150);
            groups.Add((Cos(angle) * r, Range(-20, -9), Sin(angle) * r, (int)(10 + Random() * 12), Range(5, 9)));
        }
        foreach (var group in groups)
        for (int i = 0; i < group.Count; i++)
        {
            double scale = Random() < 0.12 ? Range(1.8, 3.2) : Range(0.35, 1.5);
            DVec p = new(group.X + Range(-1, 1) * group.Radius, group.Y + Range(-1, 1) * group.Radius * 0.6,
                group.Z + Range(-1, 1) * group.Radius);
            if (Field(p) > -scale)
                continue;
            double bob = Range(0.12, 0.4), seed = Random();
            p = p with { Y = Min(p.Y, -2 - scale) };
            AddSolid((int)(Random() * 4), p, scale, RandomRotation(), 1, seed, bob);
            if (scale > 1.1)
                for (int j = 0; j < 7; j++)
                    AddSolid((int)(Random() * 4), p + new DVec(Range(-1.6, 1.6), Range(-1.4, 1), Range(-1.6, 1.6)) * scale,
                        Range(0.1, 0.35), RandomRotation(), 1, seed, bob);
        }
        Seed(9);
        for (int i = 0; i < 2600; i++)
        {
            var (p, n) = PickTerrain();
            if (n.Y < 0.7 || Hypot(p.X, p.Z) > 170 || NearFlight(p, 6))
                continue;
            double scale = Range(0.25, 1.3) * (Random() < 0.1 ? 2 : 1);
            AddSolid((int)(Random() * 4), p with { Y = p.Y + scale * 0.2 }, scale, RandomRotation(), 1, Random());
        }
        Seed(10);
        for (int i = 0; i < 60000; i++)
        {
            var (p, n) = PickTerrain();
            if (Hypot(p.X, p.Z) > 172 || p.Y > -3 || (n.Y > 0.8 && Random() < 0.6) || NearFlight(p, 3.5))
                continue;
            double scale = (0.35 + 2.1 * Pow(Random(), 2.6)) * (n.Y > 0.8 ? 0.5 : 1);
            DQuat q = RandomRotation();
            int mesh = (scale > 1.15 ? 3 : 0) + (int)(Random() * 3);
            _chunks[mesh][Cell(p.X, p.Z)].Add(SolidInstance(p - n * (scale * 0.3), scale, q, 1, Random(), scale > 1.15 ? 0 : 96, 0));
        }
        Seed(11);
        for (int i = 0; i < 170000; i++)
        {
            var (p, n) = PickTerrain();
            p += new DVec(Range(-0.4, 0.4), 0, Range(-0.4, 0.4));
            if (n.Y < 0.8 || Hypot(p.X, p.Z) > 170 || NearFlight(p, 1.3))
                continue;
            double scale = 0.05 + 0.3 * Square(Random());
            DQuat q = RandomRotation();
            if (KeepInstance())
                _chunks[6][Cell(p.X, p.Z)].Add(SolidInstance(p with { Y = p.Y - scale * 0.25 }, scale, q, 1, Random(), 44, 0));
        }
    }

    private void PlaceLandmarks()
    {
        AddSolid(10, new(-5, Ground(-5, -4) - 0.6, -4), 1,
            AxisAngle(0, 1, 0, 1.2) * (AxisAngle(0, 0, 1, 0.16) * AxisAngle(1, 0, 0, -0.3)), 2, 0.3);
        AddSolid(11, new(59.5, -23, 6), 1,
            AxisAngle(0, 1, 0, -PI / 2) * (AxisAngle(0, 0, 1, -0.1) * AxisAngle(1, 0, 0, 0.32)), 3, 0.5);
        Seed(12);
        ReadOnlySpan<(double Ahead, double Right, double Scale, int Mesh)> sponges =
        [
            (7, 3.2, 1.3, 4), (8.4, 4.6, 1, 6), (9.6, 2.6, 1.5, 4), (11, 5, 1.1, 5),
            (8, -3.6, 1.2, 4), (12.5, 2, 1.2, 6), (9, 3.9, 0.8, 4)
        ];
        foreach (var sponge in sponges)
        {
            var (x, z) = RelativeToView(3, sponge.Ahead, sponge.Right);
            var camera = _views[3].Position;
            var up = new DVec((camera.X - x) * 0.07, 1, (camera.Z - z) * 0.07).Unit();
            AddSolid(sponge.Mesh, new(x, Ground(x, z) - 0.1, z), sponge.Scale, AlignUp(up), 4, Random());
        }
        for (int i = 0; i < 9; i++)
        {
            double angle = Random() * Tau;
            double x = -5 + Cos(angle) * Range(1.2, 2.6), z = -4 + Sin(angle) * Range(1.5, 4);
            AddSolid(6, new(x, Ground(x, z) + 0.05, z), Range(0.4, 0.9), AxisAngle(0, 1, 0, Random() * 6), 4, Random());
        }
    }

    private void AddBubbles()
    {
        var vents = new List<DVec>
        {
            new(4, -44, -3), new(4.5, -44, -3.5), new(-1, -44, 2), new(2, -44, -1),
            new(5.5, -44, -7), new(3, -44, -9), new(6, -44, -90), new(8, -44, -84),
            new(4, -44, -93), new(-9, -44, 3), new(5, -35, -86), new(9, -37, -92),
            new(40, -44, 20), new(-56, -44, 12), new(10, -44, 80), new(-2, -44, 30),
            new(0, -44, -30), new(12, -44, 2), new(6, -44, -2), new(46, -44, 30),
            new(-20, -44, -103), new(-52, -44, -12), new(4, -44, 70), new(9, -44, -74),
            new(4, -44, -78), new(14, -44, 26), new(26, -44, 40), new(-45, -44, -64),
            new(-55, -44, -40), new(-58, -44, 22), new(-40, -44, 40), new(-14, -44, 45),
            new(-1, -44, -9), new(-7, -44, -6)
        };
        Seed(13);
        foreach (var spot in _spongeVents)
            if (Random() < 0.28)
                vents.Add(spot);
        foreach (var vent in vents)
        {
            int count = (int)(90 + Random() * 70);
            double y = vent.Y == -44 ? Ground(vent.X, vent.Z) : vent.Y;
            for (int i = 0; i < count; i++)
            {
                double size = Random() < 0.24 ? Range(0.1, 0.3) : Range(0.025, 0.08);
                _bubbles.Add(new((float)(vent.X + Range(-0.3, 0.3)), (float)y, (float)(vent.Z + Range(-0.3, 0.3)),
                    (float)size, (float)Random(), (float)(Range(1.1, 2.4) * (0.6 + size * 3)), (float)(-y - 0.2), (float)Range(0.05, 0.16)));
            }
        }
    }

    private byte[][] Animate()
    {
        Seed(14);
        ReadOnlySpan<(float X, float Y, float Z, float Radius, int Count, float Size, int Direction, double? Seed)> schools =
        [
            (-2, -36, -10, 15, 520, 0.3f, 1, 0.945), (8, -30, -70, 22, 420, 0.32f, -1, null),
            (48, -30, 14, 18, 360, 0.3f, 1, null), (-58, -30, -8, 11, 420, 0.28f, -1, 0.3),
            (8, -36, 84, 16, 320, 0.28f, 1, null), (-90, -28, -70, 25, 300, 0.3f, 1, null),
            (95, -30, 70, 25, 300, 0.3f, -1, null), (0, -22, 20, 30, 380, 0.34f, -1, null),
            (-30, -30, -100, 18, 300, 0.3f, 1, null), (16, -36, 30, 12, 300, 0.3f, 1, null),
            (-48, -34, -58, 12, 300, 0.3f, -1, null), (-45, -35, 38, 14, 320, 0.3f, 1, null)
        ];
        var fish = new List<Particle>();
        foreach (var school in schools)
        {
            float seed = (float)(school.Seed ?? Random());
            for (int i = 0; i < school.Count; i++)
                fish.Add(new(school.X, school.Y, school.Z, school.Radius, (float)Random(),
                    (float)(school.Direction * 1.9 / school.Radius), school.Size, seed));
        }
        ReadOnlySpan<Particle> mantas =
        [
            new(1, -28, 3.5f, 14, 3.012f, 0.085f, 3.6f, 0.1f), new(49, -31, 24.5f, 5, 0.029f, -0.08f, 3.7f, 0.4f),
            new(0, -14, -80, 34, 0.3f, 0.06f, 5.2f, 0.7f), new(-50, -18, 20, 26, 5, -0.075f, 3.8f, 0.55f),
            new(10, -16, 86, 26, 2.2f, 0.07f, 4.4f, 0.85f), new(20, -11, -20, 46, 4.2f, -0.045f, 5.6f, 0.25f)
        ];
        var snow = new float[9000 * 4];
        for (int i = 0; i < snow.Length; i++)
            snow[i] = (float)Random();
        return [Pack<Particle>(CollectionsMarshal.AsSpan(fish)), Pack(mantas), Pack<Particle>(CollectionsMarshal.AsSpan(_bubbles)),
            Pack<float>(snow), Pack<Particle>(CollectionsMarshal.AsSpan(_glints))];
    }
}
