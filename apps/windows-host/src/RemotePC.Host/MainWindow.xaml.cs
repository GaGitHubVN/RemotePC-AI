using System.Windows;
using System.Windows.Media.Animation;
using RemotePC.Core.Security;
using RemotePC.Host.Services;
using RemotePC.Host.ViewModels;
using RemotePC.Host.Views;

namespace RemotePC.Host;

public partial class MainWindow : Window
{
    private readonly HostRuntime _rt;
    private SessionBanner? _banner;

    public MainWindow(MainViewModel vm, HostRuntime rt)
    {
        InitializeComponent();
        _rt = rt;
        DataContext = vm;
        vm.RequestPairing += () => new PairingWindow(rt) { Owner = this }.ShowDialog();
        vm.ThemeChanged += theme => ThemeManager.Apply(theme, rt.Config.ReducedMotion);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Page)) AnimatePage(); };

        // Pairing approval always happens on this PC, with the owner comparing the verify code.
        rt.Pairing.ApprovalHandler = (req, ct) =>
        {
            var tcs = new TaskCompletionSource<PairingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.BeginInvoke(() =>
            {
                var dlg = new ApprovalWindow(req) { Owner = this };
                Activate();
                using var reg = ct.Register(() => Dispatcher.BeginInvoke(() => { if (dlg.IsVisible) dlg.Close(); }));
                dlg.ShowDialog();
                tcs.TrySetResult(dlg.Decision ?? new PairingDecision(false, Permission.None));
            });
            return tcs.Task;
        };
        rt.StateChanged += () => Dispatcher.BeginInvoke(UpdateBanner);
    }

    private void AnimatePage()
    {
        if (ThemeManager.ReducedMotion) return;
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = ease });
    }

    /// <summary>A visible, always-on-top indicator whenever someone is connected or the agent is working.</summary>
    private void UpdateBanner()
    {
        var sessions = _rt.Sessions.Sessions.Count;
        var agentBusy = _rt.Agent.Tasks.Any(t => t.State is Core.Agent.AgentTaskState.Running or Core.Agent.AgentTaskState.Planning or Core.Agent.AgentTaskState.Verifying);
        if (sessions == 0 && !agentBusy)
        {
            _banner?.Hide();
            return;
        }
        _banner ??= new SessionBanner(_rt);
        _banner.Update(sessions, agentBusy, string.Join(", ", _rt.Sessions.Sessions.Select(s => s.Device?.Name ?? "?")));
        if (!_banner.IsVisible) _banner.Show();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_rt.Sessions.Sessions.Count > 0 &&
            MessageBox.Show("Closing RemotePC AI ends all remote sessions. Continue?", "RemotePC AI", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _banner?.Close();
        base.OnClosing(e);
    }
}
