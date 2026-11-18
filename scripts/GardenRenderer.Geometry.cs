using System;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

internal sealed partial class GardenRenderer
{
    private sealed record Geometry(Rid Vertices, Rid Indices, int Count);
    private sealed record CellDraw(Geometry Geometry, GardenWorld.CellsData Cells, GardenWorld.MeshRange Mesh,
        int Material, int LeafVertices, float Margin, float MinDistance, float MaxDistance);
    private readonly record struct DrawRun(Geometry Geometry, int Material, int Offset, int Count);

    private readonly Pass[] _scenePasses = new Pass[13];
    private readonly Geometry[] _batches = new Geometry[17];
    private readonly Geometry[] _actors = new Geometry[5];
    private readonly CellDraw[] _cellDraws = new CellDraw[23];
    private readonly DrawRun[] _runs = new DrawRun[26];
    private readonly int[] _commands = new int[32768];
    private readonly int[] _rowOrder = new int[GardenWorld.Cells];
    private long _solidFormat, _leafFormat, _animatedFormat, _spriteFormat, _snowFormat;
    private Rid _indirect;
    private Geometry _landmarks, _shadowLeaves;
    private int _runCount, _commandCount;

    private void UploadGeometry(GardenWorld world)
    {
        _solidFormat = VertexFormat([
            Attribute(0, 0, 0, 16, RD.DataFormat.R32G32B32Sfloat),
            Attribute(0, 1, 12, 16, RD.DataFormat.R8G8B8A8Snorm),
            Attribute(1, 2, 0, 48, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(1, 3, 16, 48, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(1, 4, 32, 48, RD.DataFormat.R32G32B32A32Sfloat, true)
        ]);
        _leafFormat = VertexFormat([
            Attribute(0, 0, 0, 36, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(0, 1, 16, 36, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(0, 2, 32, 36, RD.DataFormat.R8G8B8A8Unorm, true)
        ]);
        _animatedFormat = VertexFormat([
            Attribute(0, 0, 0, 16, RD.DataFormat.R32G32B32Sfloat),
            Attribute(0, 1, 12, 16, RD.DataFormat.R8G8B8A8Snorm),
            Attribute(1, 2, 0, 32, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(1, 3, 16, 32, RD.DataFormat.R32G32B32A32Sfloat, true)
        ]);
        _spriteFormat = VertexFormat([
            Attribute(0, 0, 0, 32, RD.DataFormat.R32G32B32A32Sfloat, true),
            Attribute(0, 1, 16, 32, RD.DataFormat.R32G32B32A32Sfloat, true)
        ]);
        _snowFormat = VertexFormat([Attribute(0, 0, 0, 16, RD.DataFormat.R32G32B32A32Sfloat, true)]);
        _indirect = Track(_rd.StorageBufferCreate((uint)_commands.Length * 4, [],
            RD.StorageBufferUsage.Indirect), _static, "indirect draws");

        uint terrainVertices = (uint)world.TerrainVertices.Length / 16;
        Rid terrain = Vertices(world.TerrainVertices);
        Rid terrainIndices = Indices(world.TerrainIndices);
        uint meshVertices = (uint)world.MeshVertices.Length / 16;
        Rid meshes = Vertices(world.MeshVertices);
        Rid meshIndices = Indices(world.MeshIndices);
        Rid landmarkInstances = Vertices(world.Solids);
        int landmarkCount = world.Solids.Length / 48 - 1;
        // Terrain uses the identity instance after the landmarks in the packed buffer.
        _batches[0] = new(VertexArray(terrainVertices, _solidFormat, [terrain, landmarkInstances]), terrainIndices,
            world.TerrainIndices.Length / 4);
        _landmarks = new(VertexArray(meshVertices, _solidFormat, [meshes, landmarkInstances]), meshIndices, landmarkCount);
        int job = 0;
        _cellDraws[job++] = new(_batches[0], world.TerrainCells, new(0, 0, landmarkCount), 0, 0, 1.5f, 0, 240);

        for (int i = 0; i < world.Batches.Length; i++)
        {
            GardenWorld.Batch batch = world.Batches[i];
            bool leaf = i >= 13;
            int count = batch.Instances.Length / (leaf ? 36 : 48);
            Rid instances = Vertices(count > 0 ? batch.Instances : new byte[48]);
            Geometry geometry = leaf
                ? new(VertexArray(18, _leafFormat, [instances]), default, count)
                : new(VertexArray(meshVertices, _solidFormat, [meshes, instances]), meshIndices, count);
            _batches[i + 1] = geometry;
            if (i < 7)
            {
                bool large = i is >= 3 and <= 5;
                _cellDraws[job++] = new(geometry, batch.Cells, world.Meshes[14 + i], 1, 0, 3, 0,
                    i == 6 ? 30 : large ? 40 : 96);
                if (large)
                    _cellDraws[job++] = new(geometry, batch.Cells, world.Meshes[20 + i], 1, 0, 3, 40, 150);
            }
            else if (i < 13)
            {
                int prop = i - 7;
                bool hasLod = prop is 3 or 4;
                _cellDraws[job++] = new(geometry, batch.Cells, world.Meshes[4 + prop], 1, 0, 3, 0, hasLod ? 6 : 110);
                if (hasLod)
                    _cellDraws[job++] = new(geometry, batch.Cells, world.Meshes[18 + prop], 1, 0, 3, 6, 110);
            }
            else if (i == 13)
            {
                _shadowLeaves = new(VertexArray(6, _leafFormat, [instances]), default, count);
                _cellDraws[job++] = new(geometry, batch.Cells, default, 4, 18, 5, 0, 24);
                _cellDraws[job++] = new(geometry, batch.Cells, default, 5, 10, 5, 24, 58);
            }
            else if (i == 14)
            {
                _cellDraws[job++] = new(geometry, batch.Cells, default, 6, 6, 5, 58, 150);
            }
            else
            {
                _cellDraws[job++] = new(geometry, batch.Cells, default, 7, 6, 1.5f, 0, 70);
            }
        }

        for (int i = 0; i < _actors.Length; i++)
        {
            byte[] data = world.Animated[i];
            int count = data.Length / (i == 3 ? 16 : 32);
            Rid instances = Vertices(count > 0 ? data : new byte[32]);
            _actors[i] = i < 2
                ? new(VertexArray(meshVertices, _animatedFormat, [meshes, instances]), meshIndices, count)
                : new(VertexArray(4, i == 3 ? _snowFormat : _spriteFormat, [instances]), default, count);
        }
    }

    private void CreateScenePipelines()
    {
        Binding[] bindings = SceneBindings(_sceneBuffer);
        Pass Make(string name, long vertices, bool strip = false, Blend blend = Blend.Opaque,
            RD.PolygonCullMode cull = RD.PolygonCullMode.Disabled) =>
            new(_sceneFramebuffer, Pipeline(name, _sceneFramebuffer, vertices, strip, depth: true,
                blend: blend, cull: cull), Uniforms(name, _sized, bindings));

        _scenePasses[0] = Make("solid", _solidFormat, cull: RD.PolygonCullMode.Back);
        _scenePasses[1] = Make("solid", _solidFormat);
        _scenePasses[2] = Make("manta", _animatedFormat);
        _scenePasses[3] = Make("fish", _animatedFormat);
        _scenePasses[4] = Make("leafNear", _leafFormat, true);
        _scenePasses[5] = Make("leafMid", _leafFormat, true);
        _scenePasses[6] = Make("leafFar", _leafFormat, true);
        _scenePasses[7] = Make("leafSmall", _leafFormat, true);
        _scenePasses[8] = Make("surface", RD.InvalidId, true);
        _scenePasses[9] = Make("background", RD.InvalidId, true);
        _scenePasses[10] = Make("bubble", _spriteFormat, true, Blend.Premultiplied);
        _scenePasses[11] = Make("glint", _spriteFormat, true, Blend.Add);
        _scenePasses[12] = Make("snow", _snowFormat, true, Blend.Add);
    }

    private void BakeShadow(GardenWorld world)
    {
        Rid framebuffer = Framebuffer(_static, _shadow);
        Binding[] bindings = [Ubo(_shadowBuffer)];
        var terrain = new Pass(framebuffer, Pipeline("shadowSolid", framebuffer, _solidFormat,
            depth: true, shadow: true, cull: RD.PolygonCullMode.Front), Uniforms("shadowSolid", _static, bindings));
        var solid = terrain with
        {
            Pipeline = Pipeline("shadowSolid", framebuffer, _solidFormat, depth: true, shadow: true)
        };
        var leaves = new Pass(framebuffer, Pipeline("shadowLeaf", framebuffer, _leafFormat, strip: true,
            depth: true, shadow: true), Uniforms("shadowLeaf", _static, bindings));

        _runCount = _commandCount = 0;
        int ground = _commandCount;
        WriteIndexed(_batches[0].Count, 1, 0, 0, _landmarks.Count);
        AddLandmarks(world, 1);
        if (_quality == 0)
        {
            for (int i = 0; i < 13; i++)
            {
                Geometry geometry = _batches[i + 1];
                if (geometry.Count == 0)
                    continue;
                var mesh = world.Meshes[i < 7 ? 14 + i : 4 + i - 7];
                int offset = _commandCount;
                WriteIndexed(mesh.Count, geometry.Count, mesh.Index, mesh.Vertex, 0);
                AddRun(geometry, 1, offset, 5);
            }
        }
        UploadCommands();
        long list = _rd.DrawListBegin(framebuffer, RD.DrawFlags.ClearDepth, [], 1);
        Bind(list, terrain);
        DrawGeometry(list, new(_batches[0], 0, ground, 1));
        Bind(list, solid);
        for (int i = 0; i < _runCount; i++)
            DrawGeometry(list, _runs[i]);
        Bind(list, leaves);
        _rd.DrawListBindVertexArray(list, _shadowLeaves.Vertices);
        _rd.DrawListDraw(list, false, (uint)_shadowLeaves.Count);
        _rd.DrawListEnd();
    }

    private void PrepareDraws(DiveCamera.Frame frame, GardenWorld world, float distance)
    {
        _commandCount = _runCount = 0;
        int center = Mathf.Clamp(Mathf.FloorToInt((frame.Position.Z - GardenWorld.Origin) / GardenWorld.CellSize),
            0, GardenWorld.Cells - 1);
        int n = 0;
        _rowOrder[n++] = center;
        for (int i = 1; n < _rowOrder.Length; i++)
        {
            if (center - i >= 0)
                _rowOrder[n++] = center - i;
            if (center + i < _rowOrder.Length)
                _rowOrder[n++] = center + i;
        }
        AddCells(_cellDraws[0], frame, distance);
        AddLandmarks(world, 1);
        for (int i = 1; i < _cellDraws.Length && _cellDraws[i].Material == 1; i++)
            AddCells(_cellDraws[i], frame, distance);
        AddActor(1, 2, world.Meshes[12]);
        AddActor(0, 3, world.Meshes[13]);
        for (int i = _cellDraws.Length - 4; i < _cellDraws.Length; i++)
            AddCells(_cellDraws[i], frame, distance);
        UploadCommands();
    }

    private void AddLandmarks(GardenWorld world, int material)
    {
        int offset = _commandCount;
        for (int i = 0; i < world.SolidRanges.Length; i++)
        {
            var range = world.SolidRanges[i];
            if (range.Count == 0)
                continue;
            var mesh = world.Meshes[i];
            WriteIndexed(mesh.Count, range.Count, mesh.Index, mesh.Vertex, range.First);
        }
        AddRun(_landmarks, material, offset, 5);
    }

    private void AddActor(int actor, int material, GardenWorld.MeshRange mesh)
    {
        Geometry geometry = _actors[actor];
        if (geometry.Count == 0)
            return;
        int offset = _commandCount;
        WriteIndexed(mesh.Count, geometry.Count, mesh.Index, mesh.Vertex, 0);
        AddRun(geometry, material, offset, 5);
    }

    private void AddCells(CellDraw draw, DiveCamera.Frame frame, float distance)
    {
        const float half = GardenWorld.CellSize * 0.5f;
        int offset = _commandCount;
        bool terrain = draw.Material == 0;
        float range = terrain ? 1 : distance;
        float minimum = draw.MinDistance * range;
        float maximum = draw.MaxDistance * range;
        minimum *= minimum;
        maximum *= maximum;
        float tanY = DiveCamera.TanHalfFov;
        float tanX = tanY * _size.X / _size.Y;
        float extent = half + draw.Margin;
        float forwardXz = extent * (Mathf.Abs(frame.Forward.X) + Mathf.Abs(frame.Forward.Z));
        float rightXz = extent * (Mathf.Abs(frame.Right.X) + Mathf.Abs(frame.Right.Z));
        float upXz = extent * (Mathf.Abs(frame.Up.X) + Mathf.Abs(frame.Up.Z));
        float forwardY = Mathf.Abs(frame.Forward.Y);
        float upY = Mathf.Abs(frame.Up.Y);
        foreach (int z in _rowOrder)
        {
            int row = z * GardenWorld.Cells;
            float pz = GardenWorld.Origin + (z + 0.5f) * GardenWorld.CellSize - frame.Position.Z;
            float hz = Mathf.Max(Mathf.Abs(pz) - half, 0);
            int run = -1;
            for (int x = 0; x <= GardenWorld.Cells; x++)
            {
                int cell = row + x;
                bool visible = false;
                if (x < GardenWorld.Cells && draw.Cells.Start[cell] != draw.Cells.Start[cell + 1])
                {
                    float low = draw.Cells.Low[cell];
                    float high = draw.Cells.High[cell];
                    float px = GardenWorld.Origin + (x + 0.5f) * GardenWorld.CellSize - frame.Position.X;
                    float hx = Mathf.Max(Mathf.Abs(px) - half, 0);
                    float horizontalDistance = hx * hx + hz * hz;
                    if (horizontalDistance >= minimum && horizontalDistance < maximum)
                    {
                        float ey = (high - low) * 0.5f + draw.Margin;
                        var relative = new Vector3(px, (low + high) * 0.5f - frame.Position.Y, pz);
                        float limit = relative.Dot(frame.Forward) + forwardXz + ey * forwardY;
                        visible = limit >= 0
                            && Mathf.Abs(relative.Dot(frame.Right)) - rightXz <= limit * tanX
                            && Mathf.Abs(relative.Dot(frame.Up)) - upXz - ey * upY <= limit * tanY;
                    }
                }
                if (visible)
                {
                    if (run < 0)
                        run = cell;
                }
                else if (run >= 0)
                {
                    int first = draw.Cells.Start[run];
                    int count = draw.Cells.Start[cell] - first;
                    if (terrain)
                        WriteIndexed(count * 6, 1, first * 6, 0, _landmarks.Count);
                    else if (draw.LeafVertices > 0)
                    {
                        _commands[_commandCount++] = draw.LeafVertices;
                        _commands[_commandCount++] = count;
                        _commands[_commandCount++] = 0;
                        _commands[_commandCount++] = first;
                    }
                    else
                        WriteIndexed(draw.Mesh.Count, count, draw.Mesh.Index, draw.Mesh.Vertex, first);
                    run = -1;
                }
            }
        }
        AddRun(draw.Geometry, draw.Material, offset, draw.LeafVertices > 0 ? 4 : 5);
    }

    private void DrawScene()
    {
        long list = _rd.DrawListBegin(_sceneFramebuffer, RD.DrawFlags.ClearColor0 | RD.DrawFlags.ClearDepth, Clear, 0);
        int material = -1;
        for (int i = 0; i < _runCount; i++)
        {
            var run = _runs[i];
            if (run.Material != material)
            {
                material = run.Material;
                Bind(list, _scenePasses[material]);
            }
            DrawGeometry(list, run);
        }
        Bind(list, _scenePasses[8]);
        _rd.DrawListDraw(list, false, 1, 4);
        Bind(list, _scenePasses[9]);
        _rd.DrawListDraw(list, false, 1, 4);
        DrawSprite(2, 10);
        DrawSprite(4, 11);
        DrawSprite(3, 12);
        _rd.DrawListEnd();

        void DrawSprite(int actor, int pass)
        {
            Geometry geometry = _actors[actor];
            if (geometry.Count == 0)
                return;
            Bind(list, _scenePasses[pass]);
            _rd.DrawListBindVertexArray(list, geometry.Vertices);
            _rd.DrawListDraw(list, false, (uint)geometry.Count);
        }
    }

    private void DrawGeometry(long list, DrawRun run)
    {
        bool indexed = run.Geometry.Indices.IsValid;
        _rd.DrawListBindVertexArray(list, run.Geometry.Vertices);
        if (indexed)
            _rd.DrawListBindIndexArray(list, run.Geometry.Indices);
        _rd.DrawListDrawIndirect(list, indexed, _indirect, (uint)run.Offset * 4, (uint)run.Count,
            indexed ? 20u : 16u);
    }

    private void WriteIndexed(int count, int instances, int first, int vertex, int instance)
    {
        _commands[_commandCount++] = count;
        _commands[_commandCount++] = instances;
        _commands[_commandCount++] = first;
        _commands[_commandCount++] = vertex;
        _commands[_commandCount++] = instance;
    }

    private void AddRun(Geometry geometry, int material, int offset, int stride)
    {
        int count = (_commandCount - offset) / stride;
        if (count > 0)
            _runs[_runCount++] = new(geometry, material, offset, count);
    }

    private void UploadCommands()
    {
        var bytes = MemoryMarshal.AsBytes(_commands.AsSpan(0, _commandCount));
        if (!bytes.IsEmpty)
            _rd.BufferUpdate(_indirect, 0, (uint)bytes.Length, bytes);
    }

    private static RDVertexAttribute Attribute(int binding, int location, uint offset, uint stride, RD.DataFormat format,
        bool instances = false) => new()
        {
            Binding = (uint)binding, Location = (uint)location, Offset = offset, Stride = stride, Format = format,
            Frequency = instances ? RD.VertexFrequency.Instance : RD.VertexFrequency.Vertex
        };

    private long VertexFormat(Godot.Collections.Array<RDVertexAttribute> attributes)
    {
        long format = _rd.VertexFormatCreate(attributes);
        foreach (var attr in attributes)
            attr.Dispose();
        return format;
    }

    private Rid VertexArray(uint vertices, long format, Godot.Collections.Array<Rid> buffers) =>
        Track(_rd.VertexArrayCreate(vertices, format, buffers), _static, "vertex array");

    private Rid Vertices(byte[] data) => Track(_rd.VertexBufferCreate((uint)data.Length, data), _static, "vertex buffer");

    private Rid Indices(byte[] data)
    {
        uint count = (uint)data.Length / 4;
        Rid buffer = Track(_rd.IndexBufferCreate(count, RD.IndexBufferFormat.Uint32, data), _static, "index buffer");
        return Track(_rd.IndexArrayCreate(buffer, 0, count), _static, "index array");
    }
}
