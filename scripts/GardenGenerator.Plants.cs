using System;
using System.Collections.Generic;
using static System.Math;

internal sealed partial class GardenGenerator
{
    private static readonly (double X, double Z, double Radius, double Density)[] KelpZones =
    [
        (-32, -5, 13, 0.85), (0, -86, 46, 1), (72, -26, 27, 0.9), (-60, -4, 11, 0.8),
        (0, -36, 12, 0.55), (-17, -8, 6, 0.8), (15, -8, 6, 0.8), (-4, 120, 20, 0.4),
        (-45, -63, 15, 0.9), (62, -26, 12, 0.9), (-100, 70, 30, 0.8), (100, 60, 30, 0.8),
        (-95, -90, 32, 0.8), (105, -85, 30, 0.7)
    ];
    private static readonly (double X, double Z, double Radius)[] KelpClearings =
    [
        (-5, -106.5, 8), (4.6, -109.4, 10), (14.2, -112.3, 12), (-26, -100, 6), (-20.5, -101.7, 4.5),
        (-14.8, -104.5, 6), (-7.5, -107.8, 7), (4, 112, 11), (2, 4, 15), (42, 22, 12),
        (6, 78, 19), (-58, -16, 8), (9, -66, 9), (8, -76, 8)
    ];

    private static double KelpDensity(double x, double z)
    {
        foreach (var p in KelpClearings)
            if (Hypot(x - p.X, z - p.Z) < p.Radius)
                return 0;
        double density = Smooth(0.52, 0.64, FractalNoise(x * 0.022, 4, z * 0.022, 3)) * 0.5;
        foreach (var p in KelpZones)
            density = Max(density, p.Density * Smooth(p.Radius, p.Radius * 0.75, Hypot(x - p.X, z - p.Z)));
        return density;
    }

    private void GrowKelp()
    {
        var occupied = new HashSet<int>();
        Seed(1);
        ReadOnlySpan<(int View, double Ahead, double Right, int Kind, double Density)> columns =
        [
            (0, 11, -8.5, 0, 1), (0, 16, -10.5, 0, 1), (0, 21, -9, 0, 1), (0, 9, 7, 0, 1),
            (0, 14, 9.5, 0, 1), (0, 19, 8, 0, 1), (0, 26, 6, 0, 1),
            (5, 6.5, 5.2, 1, 1), (5, 12, 7, 1, 1), (5, 15, -8, 0, 1), (5, 22, -5, 1, 1),
            (3, 12, -5.5, 0, 1), (3, 16, 5.5, 0, 1), (3, 20, -4.5, 0, 1), (3, 25, 6, 0, 1),
            (3, 13, 8, 0, 1), (3, 30, -7, 0, 1), (-1, 24, 62, 1, 1), (-1, -6, 96, 1, 1),
            (-1, 20, 96, 2, 1), (-1, -10, 70, 0, 1), (-1, 16, 52, 1, 1),
            (4, 15, -9, 0, 1.2), (4, 19, -13, 0, 1.2), (4, 23, -8.5, 0, 1.2), (4, 25, -15, 0, 1.2),
            (4, 25, 0.5, 2, 1.2), (4, 27, -3, 2, 1.2), (4, 28, 3.5, 2, 1.2), (4, 31, 0, 0, 1.3),
            (4, 33, -5, 0, 1.3), (4, 33, 5, 0, 1.3), (4, 21, 9, 0, 1.2), (4, 27, 11, 0, 1.2),
            (2, 6.5, -5.5, 0, 1.5), (2, 9.5, -8, 0, 1.3), (2, 8, 6, 0, 1.4), (2, 12, 3.6, 0, 1.3),
            (2, 17, -2.5, 0, 1.2), (2, 21, 4, 0, 1.2), (2, 12, -4.5, 2, 1.2), (2, 27, -9, 0, 1.2),
            (2, 30, 8.5, 0, 1.2), (2, 37, -3, 0, 1.1), (2, 13.5, 6.5, 0, 1.3), (2, 14.5, -7, 0, 1.3),
            (2, 6.5, 6.5, 0, 1.3), (2, 9, -8, 2, 1.3)
        ];
        foreach (var p in columns)
        {
            var (x, z) = p.View < 0 ? (p.Ahead, p.Right) : RelativeToView(p.View, p.Ahead, p.Right);
            TryColumn(x, z, p.Kind, true, p.Density);
        }
        Seed(2);
        for (int i = 0; i < 15000; i++)
        {
            double a = Random() * Tau, r = Sqrt(Random()) * 170, x = Cos(a) * r, z = Sin(a) * r;
            if (Random() < KelpDensity(x, z))
                TryColumn(x, z, Purple(x, z) > 0.5 && Random() < 0.5 ? 1 : Random() < 0.03 ? 2 : 0, false, 1);
        }

        void TryColumn(double x, double z, int kind, bool placed, double density)
        {
            int key = (int)Floor(x / 2.3) * 4096 + (int)Floor(z / 2.3);
            if (occupied.Contains(key))
                return;
            int n = FindFloors(x, z);
            Span<int> candidates = stackalloc int[Ny / 2];
            int count = 0;
            for (int i = 0; i < n; i += 2)
                if (_floors[i + 1] > 9)
                    candidates[count++] = i;
            if (count == 0)
                return;
            int floor = candidates[(int)(Random() * count)];
            DVec p = new(x, _floors[floor], z);
            if (Gradient(p).Y < 0.55 || (!placed && NearFlight(p, 5, ignoreHeight: true)))
                return;
            occupied.Add(key);
            GrowColumn(p with { Y = p.Y - 0.3 },
                Min(_floors[floor + 1] - Range(0.8, 5), placed ? Range(30, 42) : Range(15, 40)), kind, density);
        }
    }

    private void GrowColumn(DVec p, double height, int kind, double density)
    {
        int strands = 2 + (Random() < 0.6 ? 1 : 0) + (Random() < 0.3 ? 1 : 0);
        double phase = Random() * Tau, twist = Range(0.1, 0.28), radius = Range(0.2, 0.55), scale = Range(0.75, 1.25);
        double leanAngle = Random() * Tau, lean = Range(0, 0.11), fullness = Range(0.55, 1.3) * density;
        double leanX = Cos(leanAngle), leanZ = Sin(leanAngle);
        for (int s = 0; s < strands; s++)
        for (double h = 0; h < height - 1; h += 1.7)
        {
            var (x, z, angle) = Stem(s, h);
            AddLeaf(_bigLeaves, x, p.Y + h, z, 2.1, angle + PI / 2, 0.1, 0.17, h, 1, Random(), Random(), 0.3);
        }
        int count = (int)Floor(height * 30 * fullness);
        for (int i = 0; i < count; i++)
        {
            double t = Pow(Random(), 0.9), h = t * height;
            var (x, z, _) = Stem((int)(Random() * strands), h);
            double length = ((0.55 + 0.75 * Random() * Random()) * (1 - 0.5 * t * t) * scale + 0.25) * (kind == 1 ? 1.25 : 1.15);
            double tilt = Mix(Range(0.4, 1.0), Range(0.15, 0.45), t);
            int type = kind == 1 ? (Random() < 0.8 ? 3 : 4) : kind == 2 && Random() < 0.5 ? 4 : 0;
            AddLeaf(_bigLeaves, x, p.Y + h, z, length, Random() * Tau, tilt,
                length * (kind == 1 ? Range(0.09, 0.13) : Range(0.15, 0.21)) + 0.03, h, type, Random(), Random(), Range(0.38, 0.95));
        }
        for (int i = (int)Floor(height * (kind == 1 ? 16 : 13)); i > 0; i--)
        {
            double h = Random() * height, a = Random() * Tau, r = Range(0.15, 0.8) * (1 - 0.5 * h / height);
            double offset = lean * h * h / height;
            AddGlint(p.X + Cos(a) * r + leanX * offset, p.Y + h, p.Z + Sin(a) * r + leanZ * offset,
                h, Random(), kind == 1 ? Random() : Random() * 0.6, Range(0.018, 0.05));
        }
        int hx = RoundInt((p.X - GridOrigin.X) / VoxelSize / 2), hz = RoundInt((p.Z - GridOrigin.Z) / VoxelSize / 2);
        if (hx <= 0 || hx >= Nx / 2 - 1 || hz <= 0 || hz >= Nz / 2 - 1)
            return;
        int low = Max(0, (int)Floor((p.Y - Bottom) / VoxelSize / 2));
        int high = Min(Ny / 2 - 1, (int)Floor((p.Y + height - Bottom) / VoxelSize / 2));
        for (int y = low; y <= high; y++)
        {
            int i = hx + Nx / 2 * (y + Ny / 2 * hz);
            _kelpAbsorption[i] = (float)(_kelpAbsorption[i] + 0.17);
        }

        (double X, double Z, double Angle) Stem(int s, double h)
        {
            double a = phase + s * Tau / strands + h * twist;
            double r = radius * (1 - 0.6 * h / height), offset = lean * h * h / height;
            return (p.X + Cos(a) * r + leanX * offset, p.Z + Sin(a) * r + leanZ * offset, a);
        }
    }

    private void GrowRibbons()
    {
        Seed(3);
        for (int i = 0; i < 40000; i++)
        {
            var (p, normal) = PickTerrain();
            double x = p.X, y = p.Y, z = p.Z, ny = normal.Y;
            if (Hypot(x, z) > 172 || y > -6)
                continue;
            double hot = Hot(x, z), purple = Purple(x, z), u = Random();
            if (NearFlight(p, 4) || Hypot(x + 1, z - 2) < 9 || NearView(x, z, 8))
                continue;
            if (ny > 0.75 && u < 0.10 * hot)
                Cluster(p, (int)(8 + Random() * 14), 2, 1, 3, 0.13, 0.26, 0.6, 2.5);
            else if (ny > 0.6 && u < 0.10 * hot + 0.07 * Max(purple, hot * 0.4))
                Cluster(p, (int)(10 + Random() * 18), Random() < 0.8 ? 3 : 4, 0.9, 2.6, 0.14, 0.28, 0.9, 1.5);
            else if (ny < -0.35 && u < 0.5 * Max(purple, 0.2))
            {
                int n = (int)(12 + Random() * 20);
                for (int j = 0; j < n; j++)
                    AddLeaf(_bigLeaves, x + Range(-0.6, 0.6), y + 0.2, z + Range(-0.6, 0.6), Range(0.8, 3.2),
                        Random() * Tau, PI - Range(0, 0.4), Range(0.08, 0.18), 0, purple > 0.3 || Random() < 0.5 ? 6 : 0,
                        Random(), Random(), Range(0.3, 0.6));
            }
            else if (ny > -0.2 && ny < 0.8 && u < 0.07)
            {
                double yaw = Atan2(normal.Z, normal.X);
                int n = (int)(5 + Random() * 7);
                for (int j = 0; j < n; j++)
                    AddLeaf(_bigLeaves, x, y, z, Range(0.5, 1.4), yaw + Range(-0.9, 0.9), Range(0.3, 1.1),
                        Range(0.06, 0.12), 0.5, purple > 0.4 && Random() < 0.6 ? 3 : 0, Random(), Random(), Range(0.2, 0.6));
            }
        }
    }

    private void Cluster(DVec p, int count, int type, double minLength, double maxLength,
        double minWidth, double maxWidth, double maxTilt, double sway)
    {
        for (int i = 0; i < count; i++)
        {
            double a = Random() * Tau, r = Random() * 0.5, length = Range(minLength, maxLength);
            AddLeaf(_bigLeaves, p.X + Cos(a) * r, p.Y - 0.15, p.Z + Sin(a) * r, length, a,
                Range(0.05, maxTilt), Range(minWidth, maxWidth), sway, type, Random(), Random(), Range(0.3, 0.8));
        }
    }

    private void GrowGroundCover()
    {
        Seed(4);
        foreach (var patch in MagentaPatches)
        for (int i = (int)Ceiling(patch.Radius * patch.Radius * PI * 45); i > 0; i--)
        {
            double a = Random() * Tau, r = Sqrt(Random()) * patch.Radius;
            double x = patch.X + Cos(a) * r, z = patch.Z + Sin(a) * r;
            if (Random() > Magenta(x, z) + 0.12 || NearView(x, z))
                continue;
            int floors = FindFloors(x, z);
            if (floors == 0)
                continue;
            double y = _floors[floors - 2];
            if (Gradient(new(x, y, z)).Y < 0.7)
                continue;
            int n = 2 + (int)(Random() * 3);
            double yaw = Random() * Tau;
            for (int j = 0; j < n; j++)
                AddLeaf(_smallLeaves, x, y - 0.03, z, Range(0.08, 0.26), yaw + j * Tau / n + Range(-0.4, 0.4),
                    Range(0.15, 0.85), Range(0.035, 0.07), 0, 5, Random(), Random(), Range(0.3, 0.7));
            if (Random() < 0.3)
                for (int j = 0; j < 2; j++)
                    AddLeaf(_smallLeaves, x + Range(-0.2, 0.2), y - 0.04, z + Range(-0.2, 0.2), Range(0.2, 0.55),
                        Random() * Tau, Range(0.05, 0.6), Range(0.03, 0.06), 0, 7, Random(), Random(), Range(0.3, 0.8));
            if (Random() < 0.3)
                AddGlint(x, y + Range(0.08, 0.22), z, 0, Random(), 0.82 + Random() * 0.18, Range(0.01, 0.02));
        }
        Seed(5);
        for (int i = 0; i < 620000; i++)
        {
            var (p, normal) = PickTerrain();
            if (normal.Y < 0.72)
                continue;
            double x = p.X + Range(-0.45, 0.45), z = p.Z + Range(-0.45, 0.45), y = p.Y;
            if (Hypot(x, z) > 170 || NearView(x, z))
                continue;
            double m = Magenta(x, z), purple = Purple(x, z), u = Random();
            if (m > 0.3 && u < m * 0.9)
                for (int j = 0; j < 4; j++)
                    AddLeaf(_smallLeaves, x + Range(-0.1, 0.1), y - 0.05, z + Range(-0.1, 0.1), Range(0.12, 0.34),
                        Random() * Tau, Range(0.05, 0.75), Range(0.04, 0.08), 0, 5, Random(), Random(), Range(0.3, 0.7));
            else if (u < m * 0.9 + 0.5 * Max(purple, Hot(x, z)) * Smooth(0.42, 0.6, FractalNoise(x * 0.09, 9, z * 0.09, 2)))
                for (int j = 0; j < 3; j++)
                    AddLeaf(_smallLeaves, x + Range(-0.1, 0.1), y - 0.05, z + Range(-0.1, 0.1), Range(0.25, 0.7),
                        Random() * Tau, Range(0.1, 0.8), Range(0.05, 0.1), 0, 7, Random(), Random(), Range(0.3, 0.8));
        }
    }
}
