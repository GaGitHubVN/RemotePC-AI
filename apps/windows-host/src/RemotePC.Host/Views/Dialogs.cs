using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using RemotePC.Core.Security;
using RemotePC.Host.Services;
using RemotePC.Host.ViewModels;

namespace RemotePC.Host.Views;

internal static class Ui
{
    public static Brush B(string key) => ThemeManager.Brush(key);

    public static Window Frame(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = B("WindowBg"),
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
    };

    public static TextBlock Text(string s, double size = 14, bool bold = false, string brush = "TextPrimary") => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = B(brush),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
    };

    public static Button Btn(string s, string? style = null)
    {
        var b = new Button { Content = s, Margin = new Thickness(0, 0, 8, 0) };
        if (style is not null) b.Style = (Style)Application.Current.FindResource(style);
        return b;
    }
}

/// <summary>Shows the QR code and one-time code; refreshes when the offer expires or is used.</summary>
public sealed class PairingWindow : Window
{
    private readonly HostRuntime _rt;
    private readonly Image _qr = new() { Width = 260, Height = 260, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _code = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _expiry = Ui.Text("", 12, false, "TextSecondary");
    private readonly DispatcherTimer _timer;
    private PairingOffer? _qrOffer;

    public PairingWindow(HostRuntime rt)
    {
        _rt = rt;
        var f = Ui.Frame(Loc.Instance["pair.title"], 420);
        Title = f.Title; Width = f.Width; SizeToContent = f.SizeToContent; ResizeMode = f.ResizeMode;
        WindowStartupLocation = f.WindowStartupLocation; Background = f.Background; FontFamily = f.FontFamily;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text(Loc.Instance["pair.title"], 20, true));
        panel.Children.Add(Ui.Text(Loc.Instance["pair.scan"], 13, false, "TextSecondary"));
        panel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = _qr });
        panel.Children.Add(Ui.Text(Loc.Instance["pair.code"], 13, false, "TextSecondary"));
        panel.Children.Add(_code);
        panel.Children.Add(_expiry);
        var close = Ui.Btn(Loc.Instance["btn.close"], "Secondary");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        NewOffers();
        Closed += (_, _) => { _timer.Stop(); _rt.Pairing.CancelOffer(); };
    }

    private void NewOffers()
    {
        // Code mode and QR mode share the single active offer slot; the code is shown for LAN pairing,
        // and the QR (which carries a 256-bit secret) is preferred. We create the QR offer last so it is active,
        // and re-create the code offer only when the user chooses it on the phone (code entry hits the same slot).
        var code = _rt.Pairing.CreateOffer(PairingMode.Code);
        _code.Text = code.DisplayCode;
        _qrOffer = _rt.Pairing.CreateOffer(PairingMode.Qr);
        _qr.Source = Qr(_rt.QrUri(_qrOffer));
        _rt.Log.Write("pairing", "pairing offer created (QR, valid 5 minutes)");
        _codeOffer = code;
    }

    private PairingOffer? _codeOffer;

    private void Tick()
    {
        var current = _rt.Pairing.CurrentOffer;
        if (current is null) { NewOffers(); return; }
        var left = current.ExpiresAt - DateTimeOffset.UtcNow;
        _expiry.Text = $"{Loc.Instance["pair.expires"]} ({left:mm\\:ss})";
        _ = _codeOffer;
    }

    private static BitmapImage Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}

/// <summary>New-device approval: shows the verify code and lets the owner choose permissions.</summary>
public sealed class ApprovalWindow : Window
{
    public PairingDecision? Decision { get; private set; }

    public ApprovalWindow(PairingApprovalRequest req)
    {
        var f = Ui.Frame(Loc.Instance["approve.title"], 460);
        Title = f.Title; Width = f.Width; SizeToContent = f.SizeToContent; ResizeMode = f.ResizeMode;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = f.Background; FontFamily = f.FontFamily; Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text(Loc.Instance["approve.title"], 19, true));
        panel.Children.Add(Ui.Text($"{req.DeviceName} ({req.Platform}{(string.IsNullOrEmpty(req.AppVersion) ? "" : " " + req.AppVersion)})", 15, true));
        panel.Children.Add(Ui.Text(Loc.Instance["approve.verify"], 13, false, "TextSecondary"));
        panel.Children.Add(new TextBlock
        {
            Text = $"{req.VerifyCode[..3]} {req.VerifyCode[3..]}", FontSize = 34, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"),
            Foreground = Ui.B("Accent"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14),
        });
        panel.Children.Add(Ui.Text(Loc.Instance["approve.permissions"], 13, true));
        var boxes = new List<(Permission P, CheckBox Box)>();
        var defaults = PermissionNames.DefaultForNewDevice;
        foreach (var p in PermissionNames.All)
        {
            var cb = new CheckBox { Content = Loc.Instance["perm." + PermissionNames.NameOf(p)], IsChecked = defaults.HasFlag(p), Style = (Style)Application.Current.FindResource("Switch") };
            boxes.Add((p, cb));
            panel.Children.Add(cb);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var reject = Ui.Btn(Loc.Instance["btn.reject"], "Secondary");
        var allow = Ui.Btn(Loc.Instance["btn.approve"]);
        reject.Click += (_, _) => { Decision = new PairingDecision(false, Permission.None); Close(); };
        allow.Click += (_, _) =>
        {
            var perms = boxes.Where(b => b.Box.IsChecked == true).Aggregate(Permission.None, (a, b) => a | b.P);
            Decision = new PairingDecision(true, perms, req.DeviceName);
            Close();
        };
        buttons.Children.Add(reject);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        Content = panel;
    }
}

/// <summary>Always-on-top strip at the top of the screen while a remote session or agent task is active.</summary>
public sealed class SessionBanner : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

    public SessionBanner(HostRuntime rt)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        var stop = new Button { Content = Loc.Instance["btn.disconnectAll"], Style = (Style)Application.Current.FindResource("DangerButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0) };
        stop.Click += async (_, _) => await rt.EmergencyDisconnectAsync();
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Brushes.OrangeRed, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Content = new Border
        {
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 46)),
            Padding = new Thickness(14, 8, 8, 8),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, _text, stop } },
        };
        Loaded += (_, _) => { Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2; Top = 0; };
        SizeChanged += (_, _) => Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
    }

    public void Update(int sessions, bool agent, string devices)
    {
        var parts = new List<string>();
        if (sessions > 0) parts.Add($"{Loc.Instance["banner.active"]}: {devices}");
        if (agent) parts.Add(Loc.Instance["banner.agent"]);
        _text.Text = string.Join("  ·  ", parts);
    }
}

public static class InputDialog
{
    public static string? Prompt(Window owner, string title, string initial)
    {
        var w = Ui.Frame(title, 380);
        w.Owner = owner;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        var ok = Ui.Btn("OK");
        var cancel = Ui.Btn(Loc.Instance["btn.cancel"], "Secondary");
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { Ui.Text(title, 15, true), box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
        };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }
}
