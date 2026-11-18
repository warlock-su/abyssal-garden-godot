using System;
using Godot;

public partial class Garden
{
    private enum Screen { MainMenu, Swimming, PauseMenu }
    private enum LoadStage { World, Lighting, Ready, Failed, MissingRenderer }
    private const string Preferences = "user://garden.cfg";
    private static readonly Vector2I[] WindowSizes =
    [
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160)
    ];

    private readonly ConfigFile _preferences = new();
    private readonly Button[] _qualityButtons = new Button[3];
    private Screen _screen;
    private LoadStage _loadStage;
    private bool _russian;
    private Window _window;
    private Vector2I _windowedSize;
    private Control _menu;
    private ScrollContainer _menuScroll;
    private VBoxContainer _contactLinks;
    private Button _dive;
    private Button _returnToMain;
    private Button _menuButton;
    private Label _status;
    private Label _languageLabel;
    private Label _resolutionLabel;
    private OptionButton _resolutionOptions;

    private int LoadPreferences()
    {
        _preferences.Load(Preferences);
        string locale = _preferences.GetValue("ui", "language", OS.GetLocaleLanguage()).AsString();
        _russian = locale == "ru";
        _windowedSize = _preferences.GetValue("video", "window_size", Vector2I.Zero).AsVector2I();
        return Mathf.Clamp(_preferences.GetValue("video", "quality", 0).AsInt32(), 0, 2);
    }

    private void SavePreference(string section, string key, Variant value)
    {
        _preferences.SetValue(section, key, value);
        Error result = _preferences.Save(Preferences);
        if (result != Error.Ok)
            GD.PushWarning($"Could not save preferences: {result}");
    }

    private void Dive()
    {
        if (!_ready)
            return;
        StartAudio();
        ShowScreen(Screen.Swimming);
    }

    private void OpenMenu()
    {
        if (_screen == Screen.Swimming)
            ShowScreen(Screen.PauseMenu);
    }

    private void ReturnToMainMenu()
    {
        StopFlight(false);
        if (_world != null)
        {
            _camera.Aim(_world.Views[_initialView]);
            _previousPosition = _camera.Current.Position;
        }
        ShowScreen(Screen.MainMenu);
    }

    private void ShowScreen(Screen screen)
    {
        _screen = screen;
        bool swimming = screen == Screen.Swimming;
        _menu.Visible = !swimming;
        _returnToMain.Visible = screen == Screen.PauseMenu;
        UpdateContactLinks();
        _dive.Text = screen == Screen.PauseMenu ? Localize("Resume", "Продолжить") : Localize("Dive in", "Погрузиться");

        bool touch = DisplayServer.IsTouchscreenAvailable();
        _menuButton.Visible = swimming && touch && !_automated;
        _touch = -1;
        _touchCount = 0;
        if (!_automated)
        {
            Input.MouseMode = swimming && !touch ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (!swimming && !_dive.Disabled)
                _dive.GrabFocus();
        }
    }

    private void SelectLanguage(long index)
    {
        _russian = index == 0;
        RefreshMenuText();
        SavePreference("ui", "language", _russian ? "ru" : "en");
    }

    private void InitializeWindowSettings()
    {
        _window = GetWindow();
        bool launchSizeSpecified = Array.IndexOf(OS.GetCmdlineArgs(), "--resolution") >= 0;
        if (!_automated && !launchSizeSpecified && _window.Mode == Window.ModeEnum.Windowed &&
            _windowedSize.X > 0 && _windowedSize.Y > 0)
            ResizeWindow(_windowedSize);

        _window.SizeChanged += WindowSizeChanged;
        RefreshWindowSizeOption();
    }

    private void SelectWindowSize(long index)
    {
        if (index >= 0 && index < WindowSizes.Length)
        {
            ResizeWindow(WindowSizes[index]);
            WindowSizeChanged();
        }
    }

    private void ResizeWindow(Vector2I size)
    {
        if (_window.Mode != Window.ModeEnum.Windowed)
            _window.Mode = Window.ModeEnum.Windowed;

        var usable = DisplayServer.ScreenGetUsableRect(_window.CurrentScreen);
        var decorations = (_window.GetSizeWithDecorations() - _window.Size).Max(Vector2I.Zero);
        var maxSize = usable.Size - decorations;
        if (maxSize.X > 0 && maxSize.Y > 0 && (size.X > maxSize.X || size.Y > maxSize.Y))
        {
            float scale = Mathf.Min((float)maxSize.X / size.X, (float)maxSize.Y / size.Y);
            size = new(Mathf.Max(1, Mathf.FloorToInt(size.X * scale)), Mathf.Max(1, Mathf.FloorToInt(size.Y * scale)));
        }
        _window.Size = size;
        _window.MoveToCenter();
    }

    private void WindowSizeChanged()
    {
        if (!_automated && _window.Mode == Window.ModeEnum.Windowed)
            _windowedSize = _window.Size;
        RefreshWindowSizeOption();
    }

    private void RefreshWindowSizeOption()
    {
        Vector2I size = _window.Size;
        int index = Array.IndexOf(WindowSizes, size);
        int custom = WindowSizes.Length;
        if (index < 0)
        {
            index = custom;
            if (_resolutionOptions.ItemCount == custom)
            {
                _resolutionOptions.AddItem(ResolutionText(size));
                _resolutionOptions.SetItemDisabled(custom, true);
            }
            else
                _resolutionOptions.SetItemText(custom, ResolutionText(size));
        }
        else if (_resolutionOptions.ItemCount > custom)
            _resolutionOptions.RemoveItem(custom);
        _resolutionOptions.Select(index);
    }

    private void SaveWindowSettings()
    {
        if (_window == null)
            return;
        _window.SizeChanged -= WindowSizeChanged;
        if (!_automated && _windowedSize.X > 0 && _windowedSize.Y > 0 &&
            _preferences.GetValue("video", "window_size", Vector2I.Zero).AsVector2I() != _windowedSize)
            SavePreference("video", "window_size", _windowedSize);
    }

    private string Localize(string english, string russian) => _russian ? russian : english;

    private void RefreshMenuText()
    {
        _dive.Text = _screen == Screen.PauseMenu ? Localize("Resume", "Продолжить") : Localize("Dive in", "Погрузиться");
        _returnToMain.Text = Localize("Main menu", "В главное меню");
        _menuButton.TooltipText = Localize("Pause", "Пауза");
        _languageLabel.Text = Localize("Language", "Язык");
        _resolutionLabel.Text = Localize("Window size", "Размер окна");
        _qualityButtons[0].Text = Localize("High", "Высокое");
        _qualityButtons[1].Text = Localize("Medium", "Среднее");
        _qualityButtons[2].Text = Localize("Low", "Низкое");
        SetLoadStage(_loadStage);
        if (_benchmark)
            RefreshBenchmarkTitle();
    }

    private void RefreshBenchmarkTitle()
    {
        _hud.Text = $"{Localize("Benchmark", "Тест производительности")} · {_drawSize.X} × {_drawSize.Y}";
    }

    private void SetLoadStage(LoadStage stage)
    {
        _loadStage = stage;
        _status.Text = stage switch
        {
            LoadStage.World => Localize("Loading the underwater world…", "Загрузка подводного мира…"),
            LoadStage.Lighting => Localize("Preparing light and water…", "Подготовка освещения и воды…"),
            LoadStage.Ready => Localize(
                "WASD swim · E/Q up/down · Shift sprint\n1–6 viewpoints · M sound · H stats · Esc pause\nF guided swim · B benchmark",
                "WASD плавание · E/Q вверх/вниз · Shift ускорение\n1–6 точки обзора · M звук · H статистика · Esc пауза\nF маршрут · B тест производительности"),
            LoadStage.MissingRenderer => Localize("Use the Forward+ or Mobile renderer.", "Выберите рендерер Forward+ или Mobile."),
            _ => Localize("Could not load the underwater world.", "Не удалось загрузить подводный мир.")
        };
    }

    private void CreateInterface()
    {
        var canvas = new TextureRect
        {
            Texture = _texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        AddChild(canvas);
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hud = new Label { Position = new(16, 10), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 5);
        AddChild(_hud);
        _menuButton = new Button { Text = "☰", Position = new(12, 12), Visible = false };
        AddChild(_menuButton);
        _menuButton.Pressed += OpenMenu;

        _menu = new ColorRect { Name = "Menu", Color = new(0.015f, 0.024f, 0.06f, 0.62f) };
        AddChild(_menu);
        _menu.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _menuScroll = new ScrollContainer { FollowFocus = true };
        _menu.AddChild(_menuScroll);
        _menuScroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _menuScroll.AddChild(center);
        var options = new VBoxContainer();
        options.AddThemeConstantOverride("separation", 8);
        center.AddChild(options);
        var title = new Label { Text = "Abyssal Garden", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 42);
        options.AddChild(title);

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 8);
        options.AddChild(actions);
        _dive = new Button { Name = "Dive", Disabled = true, CustomMinimumSize = new(148, 44) };
        _dive.Pressed += Dive;
        actions.AddChild(_dive);
        _returnToMain = new Button { Name = "ReturnToMain", Visible = false, CustomMinimumSize = new(148, 44) };
        _returnToMain.Pressed += ReturnToMainMenu;
        actions.AddChild(_returnToMain);
        _status = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new(460, 0)
        };
        _status.AddThemeFontSizeOverride("font_size", 15);
        options.AddChild(_status);

        var levels = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        levels.AddThemeConstantOverride("separation", 8);
        options.AddChild(levels);
        for (int i = 0; i < _qualityButtons.Length; i++)
        {
            int level = i;
            var button = new Button { Disabled = true, CustomMinimumSize = new(90, 32) };
            button.Pressed += () =>
            {
                if (_benchmark)
                    StopFlight(false);
                SavePreference("video", "quality", level);
                LoadQuality(level);
            };
            levels.AddChild(button);
            _qualityButtons[i] = button;
        }

        var settings = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        settings.AddThemeConstantOverride("h_separation", 12);
        settings.AddThemeConstantOverride("v_separation", 6);
        options.AddChild(settings);
        _languageLabel = new Label();
        settings.AddChild(_languageLabel);
        var languages = new OptionButton { Name = "Language", CustomMinimumSize = new(148, 32) };
        languages.AddItem("Русский");
        languages.AddItem("English");
        languages.Select(_russian ? 0 : 1);
        languages.ItemSelected += SelectLanguage;
        settings.AddChild(languages);

        _resolutionLabel = new Label();
        settings.AddChild(_resolutionLabel);
        _resolutionOptions = new OptionButton { Name = "Resolution", CustomMinimumSize = new(148, 32) };
        foreach (var size in WindowSizes)
            _resolutionOptions.AddItem(ResolutionText(size));
        _resolutionOptions.ItemSelected += SelectWindowSize;
        settings.AddChild(_resolutionOptions);

        _contactLinks = new VBoxContainer { Name = "ContactLinks", MouseFilter = Control.MouseFilterEnum.Ignore };
        _contactLinks.AddThemeConstantOverride("separation", 4);
        _contactLinks.AddChild(CreateContactLink("Telegram", "https://t.me/warlocksu"));
        _contactLinks.AddChild(CreateContactLink("Website", "https://warlock.su"));
        _menu.AddChild(_contactLinks);
        _contactLinks.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 12);
        UpdateContactLinks();

        _fpsCounter = new Label
        {
            Name = "FpsCounter", Text = "— FPS", HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _fpsCounter.AddThemeColorOverride("font_outline_color", Colors.Black);
        _fpsCounter.AddThemeConstantOverride("outline_size", 5);
        AddChild(_fpsCounter);
        _fpsCounter.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _fpsCounter.OffsetLeft = -128;
        _fpsCounter.OffsetRight = -16;
        _fpsCounter.OffsetTop = 10;
        _fpsCounter.OffsetBottom = 34;
        RefreshMenuText();
    }

    private void UpdateContactLinks()
    {
        bool mainMenu = _screen == Screen.MainMenu;
        _contactLinks.Visible = mainMenu;
        _menuScroll.OffsetBottom = mainMenu ? -(_contactLinks.GetCombinedMinimumSize().Y + 24) : 0;
    }

    private static LinkButton CreateContactLink(string title, string address)
    {
        var link = new LinkButton
        {
            Text = $"{title}: {address}",
            Uri = address,
            Underline = LinkButton.UnderlineMode.OnHover,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd
        };
        link.AddThemeFontSizeOverride("font_size", 14);
        link.AddThemeColorOverride("font_color", new Color(0.69f, 0.87f, 0.96f));
        link.AddThemeColorOverride("font_outline_color", Colors.Black);
        link.AddThemeConstantOverride("outline_size", 4);
        return link;
    }

    private static string ResolutionText(Vector2I size) => $"{size.X} × {size.Y}";
}
