using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

internal sealed partial class GardenRenderer : IDisposable
{
    private const RD.DataFormat Hdr = RD.DataFormat.R16G16B16A16Sfloat;
    private const RD.DataFormat Depth = RD.DataFormat.D32Sfloat;
    private const RD.DataFormat Rgba = RD.DataFormat.R8G8B8A8Unorm;
    private const RD.TextureUsageBits ColorTarget = RD.TextureUsageBits.ColorAttachmentBit | RD.TextureUsageBits.SamplingBit;
    private const RD.TextureUsageBits DepthTarget = RD.TextureUsageBits.DepthStencilAttachmentBit | RD.TextureUsageBits.SamplingBit;
    private const int BloomLevels = 6;
    private static readonly Color[] Clear = [Colors.Transparent];
    private static readonly Vector3 Sun = new Vector3(0.27f, 1, -0.24f).Normalized();

    private readonly record struct Shader(Rid Rid, uint Bindings);
    private readonly record struct Pass(Rid Framebuffer, Rid Pipeline, Rid Uniforms);
    private readonly record struct Mips(Rid Texture, Rid[] Views, Rid[] Framebuffers);
    private readonly record struct Binding(RD.UniformType Type, Rid Resource);
    private readonly record struct PipelineKey(string Shader, long Target, long Vertices, bool Strip,
        bool Depth, bool Shadow, Blend Blend, RD.PolygonCullMode Cull);
    private enum Blend { Opaque, Add, Premultiplied }

    private readonly RD _rd;
    private readonly int _quality;
    private readonly RD.TextureSamples _samples;
    private readonly Dictionary<string, Shader> _shaders = new();
    private readonly Dictionary<PipelineKey, Rid> _pipelines = new();
    private readonly List<Rid> _static = new();
    private readonly List<Rid> _sized = new();
    private readonly float[] _scene = new float[60];
    private readonly float[] _waterTime = new float[4];
    private readonly Pass[] _bloom = new Pass[2 * BloomLevels - 1];
    private readonly Texture2Drd _canvas;
    private Rid _shadow, _sceneBuffer, _shadowBuffer, _waterBuffer;
    private Rid _repeat, _linear, _compare, _volume, _map;
    private Mips _water, _detail;
    private Pass[] _waterPasses;
    private Pass _rays, _rayBlur, _post;
    private Rid _sceneFramebuffer, _output;
    private Vector2I _size;
    private bool _disposed;

    public double GpuMilliseconds { get; private set; }

    public GardenRenderer(RD rd, int quality, Texture2Drd canvas)
    {
        _rd = rd;
        _quality = quality;
        _canvas = canvas;
        _samples = quality == 2 ? RD.TextureSamples.Samples1 : RD.TextureSamples.Samples4;
    }

    public void Initialize(GardenWorld world)
    {
        LoadShaders();
        _repeat = Sampler(repeat: true, anisotropy: _quality < 2);
        _linear = Sampler();
        _compare = Sampler(compare: true);
        int shadowSize = _quality == 0 ? 4096 : 2048;
        _shadow = Texture(shadowSize, shadowSize, Depth, DepthTarget, _static);
        _water = MakeMips(_quality == 2 ? 256 : 512, Hdr, 5);
        _detail = MakeMips(1024, Rgba, 11);
        _volume = Texture(256, 40, RD.DataFormat.R8G8Unorm, RD.TextureUsageBits.SamplingBit, _static,
            depth: 256, data: world.Density);
        _map = Texture(512, 512, Rgba, RD.TextureUsageBits.SamplingBit, _static, data: world.SurfaceMap);
        _sceneBuffer = Track(_rd.UniformBufferCreate(240), _static, "camera buffer");
        _shadowBuffer = Track(_rd.UniformBufferCreate(240, MemoryMarshal.AsBytes(ShadowUniforms().AsSpan())), _static, "sun buffer");
        _waterBuffer = Track(_rd.UniformBufferCreate(16), _static, "water clock");
        _waterPasses = new Pass[5];
        _waterPasses[0] = Fullscreen("water", _water.Framebuffers[0], _static, [Ubo(_waterBuffer)]);
        MakeMipPasses(_water, _waterPasses, 1);
        var detailPasses = new Pass[11];
        detailPasses[0] = Fullscreen("detail", _detail.Framebuffers[0], _static, []);
        MakeMipPasses(_detail, detailPasses, 1);
        foreach (var pass in detailPasses)
            DrawPass(pass);

        UploadGeometry(world);
        BakeShadow(world);
        world.ReleaseUploadData();
    }

    public void Render(GardenWorld world, DiveCamera.Frame camera, Vector2I size, float distance, bool profile)
    {
        if (size != _size)
            Resize(size);
        UpdateUniforms(camera, distance);
        PrepareDraws(camera, world, distance);
        if (profile)
        {
            ReadTimestamps();
            _rd.CaptureTimestamp("Garden begin");
        }
        foreach (var pass in _waterPasses)
            DrawPass(pass);
        DrawScene();
        DrawPass(_rays);
        DrawPass(_rayBlur);
        for (int i = 0; i < _bloom.Length; i++)
            DrawPass(_bloom[i], clear: i < BloomLevels);
        DrawPass(_post);
        if (profile)
            _rd.CaptureTimestamp("Garden end");
    }

    public byte[] Capture() => _rd.TextureGetData(_output, 0);

    private void Resize(Vector2I size)
    {
        _canvas.TextureRdRid = default;
        FreeAll(_sized);
        _size = size;
        int width = size.X, height = size.Y;
        bool msaa = _samples != RD.TextureSamples.Samples1;
        Rid sceneColor = Texture(width, height, Hdr, ColorTarget, _sized, resolve: msaa);
        Rid sceneDepth = Texture(width, height, Depth, DepthTarget, _sized, samples: _samples);
        int rayWidth = (width + 1) / 2, rayHeight = (height + 1) / 2;
        Rid ray = Texture(rayWidth, rayHeight, Hdr, ColorTarget, _sized);
        Rid rayBlur = Texture(rayWidth, rayHeight, Hdr, ColorTarget, _sized);
        _output = Texture(width, height, Rgba, ColorTarget | RD.TextureUsageBits.CanCopyFromBit, _sized);
        if (msaa)
        {
            Rid msColor = Texture(width, height, Hdr, RD.TextureUsageBits.ColorAttachmentBit, _sized,
                samples: _samples, discardable: true);
            using var pass = new RDFramebufferPass
            {
                ColorAttachments = [0],
                DepthAttachment = 1,
                ResolveAttachments = [2]
            };
            _sceneFramebuffer = Track(_rd.FramebufferCreateMultipass([msColor, sceneDepth, sceneColor], [pass]),
                _sized, "MSAA scene framebuffer");
        }
        else
        {
            _sceneFramebuffer = Framebuffer(_sized, sceneColor, sceneDepth);
        }
        CreateScenePipelines();

        Rid[] bloomTextures = new Rid[BloomLevels];
        Rid[] bloomFrames = new Rid[BloomLevels];
        Rid previous = sceneColor;
        for (int i = 0; i < BloomLevels; i++)
        {
            Rid texture = Texture(Math.Max(1, width >> (i + 1)), Math.Max(1, height >> (i + 1)),
                Hdr, ColorTarget, _sized);
            bloomTextures[i] = texture;
            bloomFrames[i] = Framebuffer(_sized, texture);
            _bloom[i] = Fullscreen(i == 0 ? "bloomFirst" : "bloomDown", bloomFrames[i], _sized, Blit(previous));
            previous = texture;
        }
        for (int i = BloomLevels - 1; i > 0; i--)
            _bloom[2 * BloomLevels - 1 - i] = Fullscreen("bloomUp", bloomFrames[i - 1], _sized,
                Blit(bloomTextures[i]), Blend.Add);

        _rays = Fullscreen("rays", Framebuffer(_sized, ray), _sized,
            [Ubo(_sceneBuffer), Sam(_repeat), Sam(_compare), Tex(_shadow), Tex(_water.Texture), Tex(sceneDepth), Tex(_volume), Sam(_linear)]);
        _rayBlur = Fullscreen("rayBlur", Framebuffer(_sized, rayBlur), _sized, [Tex(ray)]);
        _post = Fullscreen("post", Framebuffer(_sized, _output), _sized,
            [Ubo(_sceneBuffer), Sam(_linear), Tex(sceneColor), Tex(rayBlur), Tex(bloomTextures[0]), Tex(sceneDepth)]);
        _canvas.TextureRdRid = _output;
    }

    private float[] ShadowUniforms()
    {
        var data = new float[60];
        Vector3 right = new Vector3(Sun.Y, -Sun.X, 0).Normalized();
        Vector3 up = Sun.Cross(right);
        SetCameraMatrix(data, right / 225, up / 225, -Sun / 300, Vector3.Zero,
            new Vector4(0, 0, 125f / 300, 1));
        data.AsSpan(0, 16).CopyTo(data.AsSpan(16));
        data.AsSpan(0, 16).CopyTo(_scene.AsSpan(16));
        WriteVector(data, 32, new(0, -20, 0), 0);
        WriteVector(data, 48, Sun, 0);
        data[58] = 1;
        return data;
    }

    private void UpdateUniforms(DiveCamera.Frame camera, float distance)
    {
        float tanY = DiveCamera.TanHalfFov;
        float tanX = tanY * _size.X / _size.Y;
        Vector3 right = camera.Right / tanX;
        Vector3 up = camera.Up / tanY;
        Vector3 center = camera.Position;
        SetCameraMatrix(_scene, right, up, Vector3.Zero, camera.Forward,
            new(-right.Dot(center), -up.Dot(center), DiveCamera.Near, -camera.Forward.Dot(center)));
        WriteVector(_scene, 32, center, camera.Time);
        WriteVector(_scene, 36, camera.Right, tanX);
        WriteVector(_scene, 40, camera.Up, tanY);
        WriteVector(_scene, 44, camera.Forward, DiveCamera.Near);
        float zone = Mathf.Clamp((44 - new Vector2(center.X - 6, center.Z - 84).Length()) / 18, 0, 1);
        WriteVector(_scene, 48, Sun, zone * zone * (3 - 2 * zone));
        _scene[52] = _size.X;
        _scene[53] = _size.Y;
        _scene[54] = 1f / _size.X;
        _scene[55] = 1f / _size.Y;
        _scene[56] = 0.62f * (0.6f + 0.4f * Mathf.Clamp((-center.Y - 8) / 26, 0, 1));
        _scene[57] = 0.54f;
        _scene[58] = distance;
        _waterTime[0] = camera.Time;
        _rd.BufferUpdate(_sceneBuffer, 0, 240, MemoryMarshal.AsBytes(_scene.AsSpan()));
        _rd.BufferUpdate(_waterBuffer, 0, 16, MemoryMarshal.AsBytes(_waterTime.AsSpan()));
    }

    private static void SetCameraMatrix(float[] data, Vector3 x, Vector3 y, Vector3 z, Vector3 w, Vector4 t)
    {
        data[0] = x.X; data[4] = x.Y; data[8] = x.Z; data[12] = t.X;
        data[1] = y.X; data[5] = y.Y; data[9] = y.Z; data[13] = t.Y;
        data[2] = z.X; data[6] = z.Y; data[10] = z.Z; data[14] = t.Z;
        data[3] = w.X; data[7] = w.Y; data[11] = w.Z; data[15] = t.W;
    }

    private static void WriteVector(float[] buffer, int offset, Vector3 value, float w)
    {
        buffer[offset] = value.X;
        buffer[offset + 1] = value.Y;
        buffer[offset + 2] = value.Z;
        buffer[offset + 3] = w;
    }

    private void LoadShaders()
    {
        var asset = Godot.FileAccess.GetFileAsBytes($"res://assets/abyssal/shaders-{_quality}.bin");
        using var reader = new BinaryReader(new MemoryStream(asset, writable: false));
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            byte[] vertex = reader.ReadBytes(reader.ReadInt32());
            byte[] fragment = reader.ReadBytes(reader.ReadInt32());
            using var spirv = new RDShaderSpirV { BytecodeVertex = vertex, BytecodeFragment = fragment };
            Rid rid = Track(_rd.ShaderCreateFromSpirV(spirv, $"Garden: {name}"), _static, name);
            _shaders.Add(name, new(rid, BindingsIn(vertex) | BindingsIn(fragment)));
        }
    }

    private static uint BindingsIn(byte[] spirv)
    {
        // SPIR-V OpDecorate arguments: result id, decoration, value. Decoration 33 is Binding.
        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        uint bindings = 0;
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            if ((words[i] & 0xffff) == 71 && words[i + 2] == 33)
                bindings |= 1u << (int)words[i + 3];
        }
        return bindings;
    }

    private Rid Pipeline(string name, Rid framebuffer, long vertices, bool strip = false,
        bool depth = false, bool shadow = false, Blend blend = Blend.Opaque, RD.PolygonCullMode cull = RD.PolygonCullMode.Disabled)
    {
        var key = new PipelineKey(name, _rd.FramebufferGetFormat(framebuffer), vertices, strip, depth, shadow, blend, cull);
        if (_pipelines.TryGetValue(key, out var pipeline))
            return pipeline;
        using var raster = new RDPipelineRasterizationState
        {
            CullMode = cull,
            FrontFace = RD.PolygonFrontFace.CounterClockwise,
            DepthBiasEnabled = shadow,
            DepthBiasConstantFactor = 2,
            DepthBiasSlopeFactor = 1.5f
        };
        using var multisample = new RDPipelineMultisampleState
        {
            SampleCount = depth && !shadow ? _samples : RD.TextureSamples.Samples1
        };
        using var depthState = new RDPipelineDepthStencilState
        {
            EnableDepthTest = depth,
            EnableDepthWrite = depth && blend == Blend.Opaque,
            DepthCompareOperator = shadow ? RD.CompareOperator.Less : RD.CompareOperator.Greater
        };
        RD.BlendFactor dest = blend == Blend.Add ? RD.BlendFactor.One : RD.BlendFactor.OneMinusSrcAlpha;
        using var attachment = new RDPipelineColorBlendStateAttachment
        {
            EnableBlend = blend != Blend.Opaque,
            SrcColorBlendFactor = RD.BlendFactor.One,
            SrcAlphaBlendFactor = RD.BlendFactor.One,
            DstColorBlendFactor = dest,
            DstAlphaBlendFactor = dest
        };
        using var blendState = new RDPipelineColorBlendState { Attachments = shadow ? [] : [attachment] };
        pipeline = Track(_rd.RenderPipelineCreate(_shaders[name].Rid, key.Target,
            vertices, strip ? RD.RenderPrimitive.TriangleStrips : RD.RenderPrimitive.Triangles,
            raster, multisample, depthState, blendState), _static, name + " pipeline");
        _pipelines.Add(key, pipeline);
        return pipeline;
    }

    private Rid Uniforms(string name, List<Rid> owner, Binding[] resources)
    {
        var shader = _shaders[name];
        if (shader.Bindings == 0)
            return default;
        var bindings = new Godot.Collections.Array<RDUniform>();
        for (int i = 0; i < resources.Length; i++)
        {
            if ((shader.Bindings & (1u << i)) == 0)
                continue;
            var uniform = new RDUniform { Binding = i, UniformType = resources[i].Type };
            uniform.AddId(resources[i].Resource);
            bindings.Add(uniform);
        }
        try
        {
            return Track(_rd.UniformSetCreate(bindings, shader.Rid, 0), owner, name + " uniforms");
        }
        finally
        {
            foreach (var binding in bindings)
                binding.Dispose();
        }
    }

    private Pass Fullscreen(string name, Rid framebuffer, List<Rid> owner, Binding[] resources,
        Blend blend = Blend.Opaque) =>
        new(framebuffer, Pipeline(name, framebuffer, RD.InvalidId, blend: blend), Uniforms(name, owner, resources));

    private void DrawPass(Pass pass, bool clear = true)
    {
        long list = _rd.DrawListBegin(pass.Framebuffer, clear ? RD.DrawFlags.ClearColor0 : RD.DrawFlags.DefaultAll,
            clear ? Clear : []);
        Bind(list, pass);
        _rd.DrawListDraw(list, false, 1, 3);
        _rd.DrawListEnd();
    }

    private void Bind(long list, Pass pass)
    {
        _rd.DrawListBindRenderPipeline(list, pass.Pipeline);
        if (pass.Uniforms.IsValid)
            _rd.DrawListBindUniformSet(list, pass.Uniforms, 0);
    }

    private Rid Sampler(bool repeat = false, bool compare = false, bool anisotropy = false)
    {
        using var state = new RDSamplerState
        {
            MagFilter = RD.SamplerFilter.Linear,
            MinFilter = RD.SamplerFilter.Linear,
            MipFilter = repeat ? RD.SamplerFilter.Linear : RD.SamplerFilter.Nearest,
            RepeatU = repeat ? RD.SamplerRepeatMode.Repeat : RD.SamplerRepeatMode.ClampToEdge,
            RepeatV = repeat ? RD.SamplerRepeatMode.Repeat : RD.SamplerRepeatMode.ClampToEdge,
            RepeatW = RD.SamplerRepeatMode.ClampToEdge,
            UseAnisotropy = anisotropy,
            AnisotropyMax = 8,
            EnableCompare = compare,
            CompareOp = RD.CompareOperator.LessOrEqual
        };
        return Track(_rd.SamplerCreate(state), _static, "sampler");
    }

    private Mips MakeMips(int size, RD.DataFormat format, int count)
    {
        Rid texture = Texture(size, size, format, ColorTarget, _static, mips: count);
        var views = new Rid[count];
        var targets = new Rid[count];
        using var view = new RDTextureView();
        for (uint i = 0; i < count; i++)
        {
            views[i] = Track(_rd.TextureCreateSharedFromSlice(view, texture, 0, i, 1, RD.TextureSliceType.Slice2D),
                _static, "mip view");
            targets[i] = Framebuffer(_static, views[i]);
        }
        return new(texture, views, targets);
    }

    private void MakeMipPasses(Mips mips, Pass[] passes, int first)
    {
        for (int i = first; i < passes.Length; i++)
            passes[i] = Fullscreen("mip", mips.Framebuffers[i], _static, Blit(mips.Views[i - 1]));
    }

    private Rid Texture(int width, int height, RD.DataFormat format, RD.TextureUsageBits usage, List<Rid> owner,
        int depth = 1, int mips = 1, RD.TextureSamples samples = RD.TextureSamples.Samples1,
        bool resolve = false, bool discardable = false, byte[] data = null)
    {
        using var descriptor = new RDTextureFormat
        {
            Width = (uint)width, Height = (uint)height, Depth = (uint)depth, ArrayLayers = 1, Mipmaps = (uint)mips,
            TextureType = depth == 1 ? RD.TextureType.Type2D : RD.TextureType.Type3D,
            Format = format, UsageBits = usage, Samples = samples, IsResolveBuffer = resolve, IsDiscardable = discardable
        };
        using var view = new RDTextureView();
        return Track(_rd.TextureCreate(descriptor, view, data == null ? null : [data]), owner, $"{format} texture");
    }

    private Rid Framebuffer(List<Rid> owner, params Rid[] textures) =>
        Track(_rd.FramebufferCreate(new Godot.Collections.Array<Rid>(textures)), owner, "framebuffer");

    private static Binding Ubo(Rid value) => new(RD.UniformType.UniformBuffer, value);
    private static Binding Sam(Rid value) => new(RD.UniformType.Sampler, value);
    private static Binding Tex(Rid value) => new(RD.UniformType.Texture, value);
    private Binding[] Blit(Rid value) => [Tex(value), Sam(_linear)];
    private Binding[] SceneBindings(Rid buffer) =>
        [Ubo(buffer), Sam(_repeat), Sam(_linear), Sam(_compare), Tex(_shadow),
            Tex(_water.Texture), Tex(_detail.Texture), Tex(_volume), Tex(_map)];

    private void ReadTimestamps()
    {
        ulong start = 0;
        for (uint i = 0, count = _rd.GetCapturedTimestampsCount(); i < count; i++)
        {
            string name = _rd.GetCapturedTimestampName(i);
            if (name == "Garden begin")
                start = _rd.GetCapturedTimestampGpuTime(i);
            else if (name == "Garden end" && start > 0)
                GpuMilliseconds = (_rd.GetCapturedTimestampGpuTime(i) - start) / 1e6;
        }
    }

    private static Rid Track(Rid rid, List<Rid> owner, string label)
    {
        if (!rid.IsValid)
            throw new InvalidOperationException($"Creating {label} failed. See the Godot graphics log.");
        owner.Add(rid);
        return rid;
    }

    private void FreeAll(List<Rid> owner)
    {
        for (int i = owner.Count - 1; i >= 0; i--)
            _rd.FreeRid(owner[i]);
        owner.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _canvas.TextureRdRid = default;
        FreeAll(_sized);
        FreeAll(_static);
    }
}
