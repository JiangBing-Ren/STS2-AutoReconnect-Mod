using Godot;

namespace AutoReconnect.Scripts;

/// <summary>
/// UI overlay shown during reconnection.
/// Displays status text, attempt progress, and manual retry button.
/// </summary>
public partial class ReconnectOverlay : Control
{
    private Label? _statusLabel;
    private Label? _attemptLabel;
    private Button? _manualButton;
    private Button? _cancelButton;
    private ReconnectRunner? _runner;

    private float _dotTimer;
    private int _dotCount;
    private int _currentAttempt;
    private int _maxAttempts;

    public static ReconnectOverlay Create(ReconnectRunner? runner = null)
    {
        var overlay = new ReconnectOverlay();
        overlay._runner = runner;
        overlay.Setup();
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root.AddChild(overlay);
        return overlay;
    }

    private void Setup()
    {
        Name = "ReconnectOverlay";
        AnchorRight = 1.0f;
        AnchorBottom = 1.0f;
        ZIndex = 1000;

        // Semi-transparent dark background
        var bg = new ColorRect();
        bg.Name = "Background";
        bg.Color = new Color(0, 0, 0, 0.7f);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Center container
        var center = new VBoxContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        center.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        center.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        AddChild(center);

        // Main status label
        _statusLabel = new Label();
        _statusLabel.Name = "StatusLabel";
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1, 1));
        _statusLabel.AddThemeFontSizeOverride("font_size", 28);
        _statusLabel.Text = "Reconnecting";
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
        center.AddChild(_statusLabel);

        // Subtitle / attempt counter
        _attemptLabel = new Label();
        _attemptLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _attemptLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 1));
        _attemptLabel.AddThemeFontSizeOverride("font_size", 16);
        _attemptLabel.Text = "Attempting to rejoin the game...";
        _attemptLabel.AutowrapMode = TextServer.AutowrapMode.Word;
        center.AddChild(_attemptLabel);

        // Spacer
        var spacer = new Control();
        spacer.CustomMinimumSize = new Vector2(0, 30);
        center.AddChild(spacer);

        // Manual reconnect button (initially hidden)
        _manualButton = new Button();
        _manualButton.Name = "ManualRetryBtn";
        _manualButton.Text = "Retry Reconnect";
        _manualButton.CustomMinimumSize = new Vector2(240, 44);
        _manualButton.Visible = false;
        _manualButton.Pressed += OnManualRetry;
        center.AddChild(_manualButton);

        // Cancel / Return to Menu button
        _cancelButton = new Button();
        _cancelButton.Name = "CancelBtn";
        _cancelButton.Text = "Return to Main Menu";
        _cancelButton.CustomMinimumSize = new Vector2(240, 44);
        _cancelButton.Pressed += OnCancel;
        center.AddChild(_cancelButton);

        _currentAttempt = 0;
        _maxAttempts = 3;
    }

    public void SetAttempt(int attempt, int maxAttempts)
    {
        _currentAttempt = attempt;
        _maxAttempts = maxAttempts;
        if (_attemptLabel != null)
            _attemptLabel.Text = $"Attempt {attempt}/{maxAttempts}";
    }

    public void ShowManualButton()
    {
        if (_statusLabel != null)
            _statusLabel.Text = "Reconnection failed";

        if (_attemptLabel != null)
            _attemptLabel.Text = "The host may still be in the game. You can retry or return to menu.";

        if (_manualButton != null)
            _manualButton.Visible = true;
    }

    public void HideManualButton()
    {
        if (_manualButton != null)
            _manualButton.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_statusLabel == null) return;

        _dotTimer += (float)delta;
        if (_dotTimer > 0.5f)
        {
            _dotTimer = 0;
            _dotCount = (_dotCount + 1) % 4;
            var dots = new string('.', _dotCount);

            if (_manualButton != null && _manualButton.Visible)
                _statusLabel.Text = "Reconnection failed";
            else
                _statusLabel.Text = $"Reconnecting{dots}";
        }
    }

    private void OnManualRetry()
    {
        if (_runner != null && GodotObject.IsInstanceValid(_runner))
            _runner.OnManualRetry();
    }

    private void OnCancel()
    {
        if (_runner != null && GodotObject.IsInstanceValid(_runner))
            _runner.OnCancel();

        // If runner is gone/unavailable, do direct cleanup
        if (_runner == null || !GodotObject.IsInstanceValid(_runner))
        {
            HostInfoTracker.IsReconnecting = false;
            HostInfoTracker.Reset();
            QueueFree();

            var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            var method = rm?.GetType().GetMethod("ReturnToMainMenuWithError",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            method?.Invoke(rm, new object?[] { null });
        }
    }
}
