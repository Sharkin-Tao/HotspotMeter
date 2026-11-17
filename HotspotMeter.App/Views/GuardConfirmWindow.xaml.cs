using System.Windows;
using System.Windows.Input;
using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.App.Views;

/// <summary>
/// 结束进程前的二次确认。确认框只展示本次请求的身份信息，
/// 真正的身份复核仍由 <c>GuardProcessManager</c> 在结束前重新执行。
/// </summary>
public partial class GuardConfirmWindow : Window
{
    public GuardConfirmWindow(GuardProcessCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        NameText = candidate.DisplayName;
        PidText = candidate.ProcessId.ToString();
        PathText = string.IsNullOrWhiteSpace(candidate.ExecutablePath)
            ? candidate.PackageFamilyName ?? "—"
            : candidate.ExecutablePath;
        InitializeComponent();
        DataContext = this;
    }

    public GuardProcessCandidate Candidate { get; }

    public string NameText { get; }

    public string PidText { get; }

    public string PathText { get; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}