using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetingBuddy;

public partial class AlertWindow : Window
{
    private readonly DispatcherTimer flash = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Action<int>? onSnooze;
    private readonly Action? onJoinClicked;
    private readonly Action? onAcknowledged;
    private readonly string joinUrl;
    private readonly bool isLiveMeeting;
    private readonly string positionMode;
    private bool bright;
    private bool canClose;

    public AlertWindow(
        string title,
        string description,
        DateTime meetingStart,
        string url = "",
        bool isLive = false,
        bool flashing = true,
        bool sound = true,
        string soundChoice = "Exclamation",
        string customSoundPath = "",
        string position = "Center",
        Action<int>? snoozeCallback = null,
        Action? joinCallback = null,
        Action? acknowledgeCallback = null)
    {
        InitializeComponent();

        MeetingTitle.Text = title;
        MeetingTime.Text = description;
        joinUrl = url;
        isLiveMeeting = isLive;
        positionMode = position;
        onSnooze = snoozeCallback;
        onJoinClicked = joinCallback;
        onAcknowledged = acknowledgeCallback;

        bool hasLink = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
        JoinButton.Visibility = hasLink ? Visibility.Visible : Visibility.Collapsed;
        if (hasLink)
        {
            var host = uri!.Host.ToLowerInvariant();
            JoinButton.Content = host == "teams.microsoft.com" || host.EndsWith(".teams.microsoft.com", StringComparison.Ordinal) ? "Join Teams"
                : host == "meet.google.com" ? "Join Google Meet"
                : host == "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal) ? "Join Zoom"
                : "Join meeting";
        }

        int minutesUntilStart = (int)Math.Floor((meetingStart - DateTime.Now).TotalMinutes);
        if (!isLiveMeeting && minutesUntilStart >= 1 && onSnooze != null)
        {
            int snoozeMins = Math.Min(2, minutesUntilStart);
            SnoozeButton.Tag = snoozeMins;
            SnoozeButton.Content = snoozeMins == 1 ? "Snooze 1 min" : $"Snooze {snoozeMins} min";
            SnoozeButton.Visibility = Visibility.Visible;
        }
        else
        {
            SnoozeButton.Visibility = Visibility.Collapsed;
        }

        if (isLiveMeeting)
        {
            BadgeText.Text = "●  MEETING BUDDY  /  LIVE NOW";
            LiveHintText.Visibility = Visibility.Visible;
            DismissButton.Visibility = Visibility.Collapsed;
            AcknowledgeButton.Visibility = Visibility.Visible;
            AcknowledgeButton.Content = hasLink ? "Acknowledge (No Join)" : "Acknowledge";
        }
        else
        {
            canClose = true;
            DismissButton.Visibility = Visibility.Visible;
            AcknowledgeButton.Visibility = Visibility.Collapsed;
        }

        flash.Tick += (_, _) =>
        {
            bright = !bright;
            Frame.BorderBrush = new SolidColorBrush(bright
                ? System.Windows.Media.Color.FromRgb(37, 99, 235)
                : System.Windows.Media.Color.FromRgb(96, 165, 250));
        };
        if (flashing) flash.Start();
        Closed += (_, _) => flash.Stop();

        if (sound) PlayAlertSound(soundChoice, customSoundPath);

        Loaded += (_, _) =>
        {
            PositionWindow();
            Activate();
            Focus();
        };
    }

    private void PositionWindow()
    {
        // Use the monitor containing the pointer when the alert appears.
        // Convert physical monitor pixels to WPF device-independent units.
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var source = System.Windows.PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(area.Right, area.Bottom));
        var workArea = new Rect(topLeft, bottomRight);
        const double margin = 20;
        switch (positionMode)
        {
            case "TopRight":
                Left = workArea.Right - Width - margin;
                Top = workArea.Top + margin;
                break;
            case "BottomRight":
                Left = workArea.Right - Width - margin;
                Top = workArea.Bottom - Height - margin;
                break;
            default:
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
                break;
        }
    }

    private static void PlayAlertSound(string soundChoice, string customSoundPath)
    {
        try
        {
            if (soundChoice == "Custom" && !string.IsNullOrWhiteSpace(customSoundPath) && File.Exists(customSoundPath))
            {
                using var player = new SoundPlayer(customSoundPath);
                player.Play();
                return;
            }

            switch (soundChoice)
            {
                case "Asterisk":
                    SystemSounds.Asterisk.Play();
                    break;
                case "Beep":
                    SystemSounds.Beep.Play();
                    break;
                default:
                    SystemSounds.Exclamation.Play();
                    break;
            }
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (isLiveMeeting && !canClose)
        {
            e.Cancel = true;
            Activate();
            return;
        }
        base.OnClosing(e);
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (isLiveMeeting) return;
        int mins = SnoozeButton.Tag is int m ? m : 2;
        onSnooze?.Invoke(mins);
        canClose = true;
        Close();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(joinUrl) { UseShellExecute = true });
            onJoinClicked?.Invoke();
            canClose = true;
            Close();
        }
        catch
        {
            System.Windows.MessageBox.Show("Could not open the meeting link.");
        }
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        onAcknowledged?.Invoke();
        canClose = true;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        canClose = true;
        Close();
    }
}