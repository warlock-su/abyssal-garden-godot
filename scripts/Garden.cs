using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class Garden : Node3D
{
    private readonly DiveCamera _camera = new();
    private readonly CancellationTokenSource _generationCancellation = new();
    private readonly Texture2Drd _texture = new();
    private readonly List<double> _frameTimes = new();
    private readonly List<double> _gpuTimes = new();
    private RenderingDevice _device;
    private GardenRenderer _renderer;
    private GardenWorld _world;
    private Task<GardenWorld> _worldGeneration;
    private Label _hud;
    private Label _fpsCounter;
    private int _quality;
    private int _pendingFrame;
    private bool _initializing;
    private bool _ready;
    private bool _exiting;
    private bool _showHud;
    private bool _flying;
    private bool _benchmark;
    private bool _benchmarkOnStart;
    private bool _automated;
    private string _capturePath;
    private int _capturedView;
    private int _initialView;
    private float _flightTime;
    private float _elapsed;
    private float _fpsElapsed;
    private int _fpsFrames;
    private float _worstFrame;
    private Vector2I _drawSize;
    private Vector2I _commandRenderSize;
    private GardenAudio _audio;
    private Vector3 _previousPosition;
    private int _touch = -1;
    private int _touchCount;
    private Vector2 _touchOrigin;
    private Vector2 _touchPosition;

    public override void _Ready()
    {
        int quality = LoadPreferences();
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--quality=", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(10), out int q))
                quality = Mathf.Clamp(q, 0, 2);
            else if (arg.StartsWith("--pose=", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(7), out int pose))
                _initialView = Mathf.Clamp(pose - 1, 0, 5);
            else if (arg.StartsWith("--capture=", StringComparison.Ordinal))
                _capturePath = arg[10..];
            else if (arg.StartsWith("--render-size=", StringComparison.Ordinal))
                _commandRenderSize = ParseRenderSize(arg.AsSpan(14));
            else if (arg == "--benchmark")
                _benchmarkOnStart = true;
        }
        _automated = _capturePath != null || _benchmarkOnStart;
        CreateInterface();
        InitializeWindowSettings();
        _fpsCounter.Visible = !_automated;
        _device = RenderingServer.GetRenderingDevice();
        if (_device == null)
        {
            GD.PushError("A RenderingDevice renderer is required. Use Forward+ or Mobile.");
            Fail(LoadStage.MissingRenderer);
            return;
        }
        LoadQuality(quality);
    }

    private async void LoadQuality(int quality)
    {
        if (_initializing)
            return;
        _initializing = true;
        _ready = false;
        _quality = quality;
        SetLoadStage(LoadStage.World);
        _dive.Disabled = true;
        foreach (var button in _qualityButtons)
            button.Disabled = true;
        try
        {
            CancellationToken cancellation = _generationCancellation.Token;
            _worldGeneration = Task.Run(() => GardenGenerator.Generate(quality, cancellation), cancellation);
            GardenWorld world = await _worldGeneration;
            if (_exiting)
                return;
            _world = world;
            if (_screen == Screen.MainMenu)
                _camera.Aim(world.Views[_initialView]);
            _previousPosition = _camera.Current.Position;
            SetLoadStage(LoadStage.Lighting);
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                try
                {
                    _renderer?.Dispose();
                    _renderer = new GardenRenderer(_device, quality, _texture);
                    _renderer.Initialize(world);
                    Callable.From(FinishLoading).CallDeferred();
                }
                catch (Exception error)
                {
                    GD.PushError(error.ToString());
                    Callable.From(() => Fail(LoadStage.Failed)).CallDeferred();
                }
            }));
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception error)
        {
            if (_exiting)
                return;
            GD.PushError(error.ToString());
            Fail(LoadStage.Failed);
        }
    }

    private void FinishLoading()
    {
        if (_exiting)
            return;
        _elapsed = 0;
        _initializing = false;
        _ready = true;
        SetLoadStage(LoadStage.Ready);
        _dive.Disabled = false;
        for (int i = 0; i < _qualityButtons.Length; i++)
            _qualityButtons[i].Disabled = i == _quality;
        if (_automated)
            ShowScreen(Screen.Swimming);
        else
            _dive.GrabFocus();
        GD.Print($"Abyssal Garden ready (quality {_quality}, {RenderingServer.GetVideoAdapterName()}).");
    }

    public override void _Process(double delta)
    {
        if (!_ready || Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0)
            return;
        float dt = Mathf.Min((float)delta, 0.1f);
        UpdateRenderSize();
        _elapsed += dt;
        if (_capturePath == null)
            _camera.Advance(dt);
        if (_benchmarkOnStart && _elapsed >= 3)
        {
            _benchmarkOnStart = false;
            StartFlight(true);
        }
        if (_flying && _screen == Screen.Swimming)
        {
            _flightTime += dt;
            _camera.Fly(_world, _flightTime);
            if (_benchmark)
            {
                _frameTimes.Add(delta * 1000);
                if (_renderer.GpuMilliseconds > 0)
                    _gpuTimes.Add(_renderer.GpuMilliseconds);
            }
            if (_flightTime >= DiveCamera.FlightDuration)
                StopFlight(true);
        }
        else if (_screen == Screen.Swimming && !_automated)
        {
            var input = new Vector3(KeyAxis(Key.D, Key.A), KeyAxis(Key.E, Key.Q), KeyAxis(Key.W, Key.S));
            if (_touch >= 0)
            {
                Vector2 steering = (_touchPosition - _touchOrigin) * (4 / Mathf.Min(GetViewport().GetVisibleRect().Size.X,
                    GetViewport().GetVisibleRect().Size.Y));
                _camera.Look(new Vector2(Mathf.Clamp(steering.X, -1, 1) * 1.5f, Mathf.Clamp(steering.Y, -1, 1))
                    * (dt / 0.0022f));
                input.Z = 0.7f;
            }
            _camera.Swim(_world, input, dt, Input.IsPhysicalKeyPressed(Key.Shift) || _touchCount > 1);
        }

        var frame = _camera.Current;
        float speed = frame.Position.DistanceTo(_previousPosition) / Mathf.Max(dt, 0.001f);
        _previousPosition = frame.Position;
        _audio?.Fill(speed > 60 ? 0 : speed, frame.Position.Y);
        UpdateHud((float)delta);
        Vector2I size = _drawSize;
        bool profile = _benchmark || _benchmarkOnStart || _showHud;
        string capture = _capturePath;
        GardenWorld world = _world;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            bool capturePending = false;
            try
            {
                _renderer.Render(world, frame, size, 1, profile);
                if (capture != null)
                {
                    byte[] data = _renderer.Capture();
                    Callable.From(() => SaveCapture(capture, size, data)).CallDeferred();
                    capturePending = true;
                }
            }
            catch (Exception error)
            {
                GD.PushError(error.ToString());
                Callable.From(() => Fail(LoadStage.Failed)).CallDeferred();
            }
            finally
            {
                if (!capturePending)
                    Interlocked.Exchange(ref _pendingFrame, 0);
            }
        }));
    }

    private void UpdateRenderSize()
    {
        if (_commandRenderSize != Vector2I.Zero)
        {
            _drawSize = _commandRenderSize;
            return;
        }
        if (_automated && _benchmark)
            return;

        Vector2I size = ((Vector2I)GetViewport().GetVisibleRect().Size).Max(Vector2I.One);
        if (_benchmark && size != _drawSize)
            StopFlight(false);
        _drawSize = size;
    }

    private void UpdateHud(float delta)
    {
        if (!_fpsCounter.Visible && (!_showHud || _benchmark))
            return;
        _fpsElapsed += delta;
        _fpsFrames++;
        _worstFrame = Mathf.Max(_worstFrame, delta * 1000);
        if (_fpsElapsed >= 0.5f)
        {
            float fps = _fpsFrames / _fpsElapsed;
            if (_fpsCounter.Visible)
                _fpsCounter.Text = $"{fps:F0} FPS";
            if (_showHud && !_benchmark)
            {
                double gpu = _renderer.GpuMilliseconds;
                _hud.Text = Localize(
                    $"worst {_worstFrame:F1} ms   ·   GPU {gpu:F2} ms   ·   {_drawSize.X} × {_drawSize.Y}",
                    $"Худший кадр {_worstFrame:F1} мс   ·   GPU {gpu:F2} мс   ·   {_drawSize.X} × {_drawSize.Y}");
            }
            _fpsElapsed = _worstFrame = 0;
            _fpsFrames = 0;
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!_ready)
            return;
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.PhysicalKeycode == Key.H)
            {
                _showHud = !_showHud;
                _hud.Visible = _showHud;
            }
            else if (key.PhysicalKeycode == Key.M)
                StartAudio().Muted = !_audio.Muted;
            else if (key.PhysicalKeycode == Key.Escape)
            {
                if (_screen == Screen.PauseMenu)
                    Dive();
                else if (_screen == Screen.Swimming)
                    OpenMenu();
            }
            else if (_screen == Screen.Swimming && key.PhysicalKeycode is >= Key.Key1 and <= Key.Key6)
            {
                StopFlight(false);
                _camera.Aim(_world.Views[(int)(key.PhysicalKeycode - Key.Key1)]);
            }
            else if (_screen == Screen.Swimming && key.PhysicalKeycode is Key.F or Key.B)
            {
                if (_flying)
                    StopFlight(false);
                else
                    StartFlight(key.PhysicalKeycode == Key.B);
            }
        }
        else if (_screen == Screen.Swimming && input is InputEventMouseMotion mouse && Input.MouseMode == Input.MouseModeEnum.Captured)
            _camera.Look(mouse.ScreenRelative);
        else if (_screen == Screen.Swimming && input is InputEventScreenTouch touch)
        {
            _touchCount = Mathf.Max(0, _touchCount + (touch.Pressed ? 1 : -1));
            if (touch.Pressed && _touch < 0)
            {
                _touch = touch.Index;
                _touchOrigin = _touchPosition = touch.Position;
            }
            else if (!touch.Pressed && touch.Index == _touch)
                _touch = -1;
        }
        else if (_screen == Screen.Swimming && input is InputEventScreenDrag drag && drag.Index == _touch)
        {
            _touchPosition = drag.Position;
            _camera.Look(drag.ScreenRelative);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut && _ready && !_automated && !_benchmark)
            OpenMenu();
    }

    private void StartFlight(bool benchmark)
    {
        _flying = true;
        _benchmark = benchmark;
        _flightTime = 0;
        _frameTimes.Clear();
        _gpuTimes.Clear();
        if (benchmark)
        {
            _hud.Show();
            RefreshBenchmarkTitle();
        }
    }

    private void StopFlight(bool complete)
    {
        if (complete && _benchmark)
        {
            string result = $"Abyssal Garden benchmark {_drawSize.X}x{_drawSize.Y}, quality {_quality}, distance {1f:F2}\n"
                + $"Frame {Statistics(_frameTimes)}\nGPU {Statistics(_gpuTimes)}";
            GD.Print(result);
            _hud.Text = _russian
                ? $"Тест производительности {_drawSize.X}×{_drawSize.Y}\nКадр {Statistics(_frameTimes, true)}\nGPU {Statistics(_gpuTimes, true)}"
                : result;
            if (_automated)
                GetTree().Quit();
        }
        else if (_benchmark)
        {
            _hud.Text = "";
            _hud.Visible = _showHud;
        }
        _flying = _benchmark = false;
    }

    private static string Statistics(List<double> values, bool russian = false)
    {
        if (values.Count == 0)
            return russian ? "замер недоступен" : "timestamp unavailable";
        values.Sort();
        double sum = 0;
        foreach (double value in values)
            sum += value;
        double average = sum / values.Count;
        double p99 = values[Math.Min(values.Count - 1, (int)(values.Count * 0.99))];
        return russian ? $"кадров {values.Count}, среднее {average:F2}, p99 {p99:F2}, максимум {values[^1]:F2} мс"
            : $"{values.Count} samples, avg {average:F2}, p99 {p99:F2}, max {values[^1]:F2} ms";
    }

    private void SaveCapture(string path, Vector2I size, byte[] data)
    {
        bool sequence = path.Contains("{view}", StringComparison.Ordinal);
        if (sequence)
            path = path.Replace("{view}", (_initialView + _capturedView + 1).ToString(), StringComparison.Ordinal);
        using var image = Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, data);
        Error result = image.SavePng(path);
        GD.Print($"Abyssal Garden capture {size.X}x{size.Y}: {result} ({path})");
        if (result == Error.Ok && sequence && _initialView + ++_capturedView < _world.Views.Length)
        {
            _camera.Aim(_world.Views[_initialView + _capturedView]);
            Interlocked.Exchange(ref _pendingFrame, 0);
            return;
        }
        _ready = false;
        GetTree().Quit(result == Error.Ok ? 0 : 1);
    }

    private GardenAudio StartAudio() => _audio ??= new GardenAudio(this);

    private void Fail(LoadStage stage)
    {
        _ready = false;
        _initializing = false;
        _dive.Disabled = true;
        ShowScreen(_screen == Screen.MainMenu ? Screen.MainMenu : Screen.PauseMenu);
        SetLoadStage(stage);
        if (_automated)
            GetTree().Quit(1);
        else if (_device != null)
            for (int i = 0; i < _qualityButtons.Length; i++)
                _qualityButtons[i].Disabled = i == _quality;
    }

    public override void _ExitTree()
    {
        _exiting = true;
        _generationCancellation.Cancel();
        // Keep the rendering server alive until the generation worker frees its local device.
        try
        {
            _worldGeneration?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // LoadQuality handles generation failures while this node is active.
        }
        _generationCancellation.Dispose();
        SaveWindowSettings();
        _audio?.Dispose();
        _audio = null;
        if (_device != null)
            RenderingServer.CallOnRenderThread(Callable.From(() => _renderer?.Dispose()));
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private static float KeyAxis(Key positive, Key negative) =>
        (Input.IsPhysicalKeyPressed(positive) ? 1 : 0) - (Input.IsPhysicalKeyPressed(negative) ? 1 : 0);

    private static Vector2I ParseRenderSize(ReadOnlySpan<char> value)
    {
        int separator = value.IndexOf('x');
        if (separator < 0 || !int.TryParse(value[..separator], out int width) ||
            !int.TryParse(value[(separator + 1)..], out int height) ||
            width is < 2 or > 8192 || height is < 2 or > 8192)
        {
            GD.PushWarning("Expected --render-size=WIDTHxHEIGHT with dimensions from 2 to 8192.");
            return Vector2I.Zero;
        }
        return new(width, height);
    }
}
