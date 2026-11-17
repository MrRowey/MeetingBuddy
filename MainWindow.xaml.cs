using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MeetingBuddy;

public class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public string Repeat { get; set; } = "Once";
    public int MinutesBefore { get; set; } = 5;
    public List<DayOfWeek> Weekdays { get; set; } = new();
    public string JoinUrl { get; set; } = "";
    public string EventColor { get; set; } = "#2563EB";
    public string SourceUid { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> SkippedDates { get; set; } = new();
    public DateTime? RepeatUntil { get; set; }
    public DateTime? LastLinkOpenedUtc { get; set; }

    public DateTime End => Start.AddMinutes(DurationMinutes > 0 ? DurationMinutes : 30);
    public bool HasJoinUrl => Uri.TryCreate(JoinUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
    public bool IsRecurring => !string.Equals(Repeat, "Once", StringComparison.OrdinalIgnoreCase);
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public string Display => $"{Start:ddd dd MMM yyyy, HH:mm}–{End:HH:mm}  •  {Title}  •  {Repeat}  •  {MinutesBefore}m warning";
}

public sealed class MissedMeetingEntry
{
    public string Key { get; set; } = "";
    public Guid MeetingId { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string JoinUrl { get; set; } = "";
    public bool HasJoinUrl => Uri.TryCreate(JoinUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Meeting> meetings = new();
    private readonly ObservableCollection<Meeting> upcoming = new();
    private readonly ObservableCollection<MissedMeetingEntry> missedEntries = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingBuddy", "meetings.json");
    private readonly List<(DateTime Due, DateTime MeetingStart, string Title, string Url)> snoozes = new();
    private readonly Forms.NotifyIcon tray;
    private bool exiting;
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingBuddy", "settings.json");
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "MeetingBuddy";
    private bool loadingSettings;
    private Guid? editingId;
    private Meeting? inspectingOccurrence;
    private string viewMode = "Today";
    private string searchQuery = "";
    private bool paused;
    private bool syncingReminderToggle;
    private sealed class PtoPeriod
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public override string ToString() => $"{Start:dd MMM yyyy} – {End:dd MMM yyyy}";
    }
    private bool IsOnPto(DateTime date) => settings.TimeOff?.Any(p => date.Date >= p.Start.Date && date.Date <= p.End.Date) == true;
    private void RefreshPtoList()
    {
        if (PtoList == null) return;
        PtoList.ItemsSource = null;
        PtoList.ItemsSource = settings.TimeOff;
    }
    private void AddPto_Click(object sender, RoutedEventArgs e)
    {
        if (PtoStartDate.SelectedDate is not DateTime start || PtoEndDate.SelectedDate is not DateTime end || end.Date < start.Date)
        {
            MessageBox.Show("Choose a valid start and end date.", "Time Off"); return;
        }
        settings.TimeOff ??= new();
        settings.TimeOff.Add(new PtoPeriod { Start = start.Date, End = end.Date });
        PersistSettings();
        RefreshPtoList();
        RefreshStatus();
    }
    private void RemovePto_Click(object sender, RoutedEventArgs e)
    {
        if (PtoList.SelectedItem is not PtoPeriod selected) return;
        settings.TimeOff?.Remove(selected);
        PersistSettings();
        RefreshPtoList();
        RefreshStatus();
    }
    private readonly HashSet<string> missedKeys = new();
    private readonly Dictionary<string, HashSet<int>> alertStages = new();
    private DateTime lastReminderCheck = DateTime.Now;
    private string lastDailySummaryDate = "";
    private AppSettings settings = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeTimePickers();
        MeetingDate.SelectedDate = DateTime.Today;
        MeetingList.ItemsSource = upcoming;
        MissedList.ItemsSource = missedEntries;
        MainTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, MainTabs)) UpdateSidebarSelection(); };
        UpdateSidebarSelection();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        LoadSettings();
        PtoStartDate.SelectedDate = DateTime.Today;
        PtoEndDate.SelectedDate = DateTime.Today;
        RefreshPtoList();
        paused = settings.RemindersPaused;
        MainTabs.SelectedIndex = 0;
        UpdateSidebarSelection();
        UpdateResponsiveLayout();
        ViewBox.SelectedIndex = 0;
        ApplyTheme();
        RefreshStatus();
        RepeatBox.SelectionChanged += (_, _) => UpdateWeekdaysVisibility();
        UpdateWeekdaysVisibility();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information, Text = "Meeting Buddy", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(ShowApp));
        menu.Items.Add("Add meeting", null, (_, _) => Dispatcher.Invoke(() => { ShowApp(); CancelEdit_Click(this, new RoutedEventArgs()); OpenMeetingDialog(); }));
        menu.Items.Add("Daily Summary", null, (_, _) => Dispatcher.Invoke(() => { ShowApp(); ShowDailySummary(); }));
        menu.Items.Add("Pause / Resume alerts", null, (_, _) => Dispatcher.Invoke(() => { ToggleReminders(); }));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowApp);
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Dispatcher.Invoke(() =>
                {
                    RefreshUpcoming();
                    var next = upcoming.FirstOrDefault();
                    tray.ShowBalloonTip(3500, "Meeting Buddy", next == null ? "No upcoming meetings" : $"Next: {next.Title} at {next.Start:ddd HH:mm} ({Math.Max(0, (int)(next.Start - DateTime.Now).TotalMinutes)} min)", Forms.ToolTipIcon.Info);
                });
        };
        Load();
        InitializeImportFolder();
        InitializeStreamDeckBridge();
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
            Dispatcher.BeginInvoke(new Action(() => { Hide(); ShowInTaskbar = true; }));
        }
        ticker.Tick += (_, _) => { CheckMeetings(); RefreshUpcoming(); RefreshStatus(); };
        ticker.Start();
        CheckMeetings();
        RefreshUpcoming();
    }

    private void EventColor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedEventColourPreview == null) return;
        var hex = (EventColorBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "#2563EB";
        try { SelectedEventColourPreview.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { SelectedEventColourPreview.Background = System.Windows.Media.Brushes.DodgerBlue; }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            e.Key == System.Windows.Input.Key.N &&
            MeetingDialogOverlay.Visibility != Visibility.Visible &&
            MeetingDetailsOverlay.Visibility != Visibility.Visible)
        {
            OpenMeetingDialog();
            e.Handled = true;
            return;
        }
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            e.Key == System.Windows.Input.Key.P &&
            MeetingDialogOverlay.Visibility != Visibility.Visible &&
            MeetingDetailsOverlay.Visibility != Visibility.Visible)
        {
            ToggleReminders();
            e.Handled = true;
            return;
        }
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (DailySummaryOverlay.Visibility == Visibility.Visible)
        {
            DailySummaryOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (MissedCentreOverlay.Visibility == Visibility.Visible)
        {
            MissedCentreOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (MeetingDetailsOverlay.Visibility == Visibility.Visible)
        {
            CloseDetailsDialog();
            e.Handled = true;
        }
        else if (MeetingDialogOverlay.Visibility == Visibility.Visible)
        {
            CancelEdit_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void LaunchJoinUrl(string? url, Guid? meetingId = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            MessageBox.Show("No valid meeting link is attached to this meeting.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            if (meetingId.HasValue)
            {
                var orig = meetings.FirstOrDefault(m => m.Id == meetingId.Value);
                if (orig != null)
                {
                    orig.LastLinkOpenedUtc = DateTime.UtcNow;
                    Save();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Zoom link: {ex.Message}");
        }
    }

    private void MeetingItem_Join_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Meeting meeting })
            LaunchJoinUrl(meeting.JoinUrl, meeting.Id);
    }

    private void MeetingItem_Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Meeting meeting })
            OpenDetailsDialog(meeting);
    }

    private void MeetingList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MeetingList.SelectedItem is Meeting meeting)
            OpenDetailsDialog(meeting);
    }

    private void MeetingItem_Skip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Meeting occurrence }) return;
        SkipOccurrenceDate(occurrence);
    }

    private void SkipOccurrenceDate(Meeting occurrence)
    {
        var original = meetings.FirstOrDefault(m => m.Id == occurrence.Id);
        if (original == null || !original.IsRecurring) return;

        var dateKey = occurrence.Start.ToString("yyyy-MM-dd");
        if (MessageBox.Show($"Skip '{original.Title}' on {occurrence.Start:ddd dd MMM yyyy}?\n\nFuture occurrences will remain scheduled.", "Skip occurrence", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        original.SkippedDates ??= new List<string>();
        if (!original.SkippedDates.Contains(dateKey))
            original.SkippedDates.Add(dateKey);

        Save();
        RefreshUpcoming();
        RefreshStatus();
    }

    private void OpenDetailsDialog(Meeting occurrence)
    {
        var original = meetings.FirstOrDefault(m => m.Id == occurrence.Id) ?? occurrence;
        inspectingOccurrence = occurrence;
        DetailsTitleText.Text = original.Title;
        DetailsWhenText.Text = $"{occurrence.Start:dddd, dd MMMM yyyy  •  HH:mm} – {occurrence.End:HH:mm} ({original.DurationMinutes} min)";
        DetailsRecurrenceText.Text = original.RepeatUntil.HasValue
            ? $"Recurrence: {original.Repeat} (until {original.RepeatUntil.Value:dd MMM yyyy})"
            : $"Recurrence: {original.Repeat}";
        DetailsUrlText.Text = original.HasJoinUrl ? original.JoinUrl : "No Zoom link attached";
        DetailsJoinButton.Visibility = original.HasJoinUrl ? Visibility.Visible : Visibility.Collapsed;
        DetailsCopyLinkButton.Visibility = original.HasJoinUrl ? Visibility.Visible : Visibility.Collapsed;
        DetailsSkipOccurrenceButton.Visibility = original.IsRecurring ? Visibility.Visible : Visibility.Collapsed;
        DetailsNotesBox.Text = original.Notes ?? "";
        MeetingDetailsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDetailsDialog()
    {
        MeetingDetailsOverlay.Visibility = Visibility.Collapsed;
        inspectingOccurrence = null;
    }

    private void CloseDetailsDialog_Click(object sender, RoutedEventArgs e) => CloseDetailsDialog();

    private void DetailsJoin_Click(object sender, RoutedEventArgs e)
    {
        if (inspectingOccurrence != null)
            LaunchJoinUrl(inspectingOccurrence.JoinUrl, inspectingOccurrence.Id);
    }

    private void DetailsCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (inspectingOccurrence != null && inspectingOccurrence.HasJoinUrl)
        {
            Clipboard.SetText(inspectingOccurrence.JoinUrl);
            MessageBox.Show("Zoom link copied to clipboard.", "Meeting Buddy");
        }
    }

    private void DetailsSaveNotes_Click(object sender, RoutedEventArgs e)
    {
        if (inspectingOccurrence == null) return;
        var original = meetings.FirstOrDefault(m => m.Id == inspectingOccurrence.Id);
        if (original != null)
        {
            original.Notes = DetailsNotesBox.Text.Trim();
            Save();
            RefreshUpcoming();
        }
        CloseDetailsDialog();
    }

    private void DetailsEdit_Click(object sender, RoutedEventArgs e)
    {
        if (inspectingOccurrence == null) return;
        var original = meetings.FirstOrDefault(m => m.Id == inspectingOccurrence.Id);
        CloseDetailsDialog();
        if (original != null)
            EditMeeting(original);
    }

    private void DetailsSkipOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (inspectingOccurrence == null) return;
        var occ = inspectingOccurrence;
        CloseDetailsDialog();
        SkipOccurrenceDate(occ);
    }

    private void MeetingItem_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Meeting meeting })
        {
            MeetingList.SelectedItem = upcoming.FirstOrDefault(m => m.Id == meeting.Id && m.Start == meeting.Start);
            Edit_Click(sender, e);
        }
    }

    private void MeetingItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Meeting meeting }) return;
        var label = meeting.Repeat == "Once" ? "Delete this meeting?" : "Delete this entire recurring meeting series?";
        if (MessageBox.Show(label, "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var original = meetings.FirstOrDefault(m => m.Id == meeting.Id);
        if (original == null) return;
        meetings.Remove(original);
        Save();
        RefreshUpcoming();
        RefreshStatus();
    }

    private void OpenMissedCentre_Click(object sender, RoutedEventArgs e) => MissedCentreOverlay.Visibility = Visibility.Visible;
    private void CloseMissedCentre_Click(object sender, RoutedEventArgs e) => MissedCentreOverlay.Visibility = Visibility.Collapsed;

    private void MissedItem_Join_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: MissedMeetingEntry entry })
        {
            LaunchJoinUrl(entry.JoinUrl, entry.MeetingId);
            missedKeys.Remove(entry.Key);
            missedEntries.Remove(entry);
            RefreshStatus();
        }
    }

    private void MissedItem_Ack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: MissedMeetingEntry entry })
        {
            missedKeys.Remove(entry.Key);
            missedEntries.Remove(entry);
            RefreshStatus();
        }
    }

    private void ClearAllMissed_Click(object sender, RoutedEventArgs e)
    {
        missedKeys.Clear();
        missedEntries.Clear();
        RefreshStatus();
    }

    private void ShowDailySummary_Click(object sender, RoutedEventArgs e) => ShowDailySummary();
    private void CloseDailySummary_Click(object sender, RoutedEventArgs e) => DailySummaryOverlay.Visibility = Visibility.Collapsed;

    private void ShowDailySummary()
    {
        var today = DateTime.Today;
        var todaysMeetings = meetings
            .Where(m => OccursOn(m, today))
            .OrderBy(m => m.Start.TimeOfDay)
            .Select(m => new Meeting
            {
                Id = m.Id,
                Title = m.Title,
                Start = today + m.Start.TimeOfDay,
                DurationMinutes = m.DurationMinutes,
                Repeat = m.Repeat,
                JoinUrl = m.JoinUrl
            })
            .ToList();

        DailySummarySubtitle.Text = todaysMeetings.Count == 0
            ? $"{today:dddd, dd MMMM yyyy} — No meetings scheduled today."
            : $"{today:dddd, dd MMMM yyyy} — {todaysMeetings.Count} meeting{(todaysMeetings.Count == 1 ? "" : "s")} scheduled.";
        DailySummaryList.ItemsSource = todaysMeetings;
        DailySummaryOverlay.Visibility = Visibility.Visible;
    }

    private void RefreshOverviewUi()
    {
        if (EmptyMeetingsState != null)
            EmptyMeetingsState.Visibility = upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ListViewButton != null && CalendarViewButton != null)
        {
            var accent = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["NavActiveBrush"];
            var neutral = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SubtleBrush"];
            ListViewButton.Background = calendarMode ? neutral : accent;
            CalendarViewButton.Background = calendarMode ? accent : neutral;
        }
        UpdateDashboardCards();
    }

    private void UpdateDashboardCards()
    {
        if (DashboardCardsPanel == null) return;
        DashboardCardsPanel.Visibility = settings.ShowDashboardCards ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.ShowDashboardCards) return;

        var now = DateTime.Now;
        var todayCount = meetings.Count(m => OccursOn(m, now.Date));
        var weekItems = meetings
            .Select(m => (Meeting: m, Next: NextOccurrence(m, now)))
            .Where(x => x.Next.HasValue && x.Next.Value < now.Date.AddDays(7))
            .OrderBy(x => x.Next!.Value)
            .ToList();

        KpiTodayCount.Text = todayCount.ToString(CultureInfo.InvariantCulture);
        KpiWeekCount.Text = weekItems.Count.ToString(CultureInfo.InvariantCulture);
        KpiZoomCount.Text = weekItems.Count(x => x.Meeting.HasJoinUrl).ToString(CultureInfo.InvariantCulture);

        var nextItem = weekItems.FirstOrDefault();
        if (nextItem.Meeting == null || !nextItem.Next.HasValue)
        {
            KpiNextCountdown.Text = "None scheduled";
        }
        else if (nextItem.Next.Value <= now)
        {
            KpiNextCountdown.Text = $"In progress ({nextItem.Meeting.Title})";
        }
        else
        {
            var diff = nextItem.Next.Value - now;
            KpiNextCountdown.Text = diff.TotalHours >= 24
                ? $"{nextItem.Next.Value:ddd HH:mm}"
                : diff.TotalHours >= 1
                    ? $"in {(int)diff.TotalHours}h {diff.Minutes}m"
                    : $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes))}m";
        }
    }

    private void NavigateOverview_Click(object sender, RoutedEventArgs e) => NavigateTo(0);
    private void NavigatePreferences_Click(object sender, RoutedEventArgs e) => NavigateTo(2);
    private void NewMeeting_Click(object sender, RoutedEventArgs e) { CancelEdit_Click(sender, e); MeetingDate.SelectedDate = DateTime.Today; OpenMeetingDialog(); }
    private void CloseMeetingDialog_Click(object sender, RoutedEventArgs e) => CloseMeetingDialog();
    private void OpenMeetingDialog() { MeetingDialogOverlay.Visibility = Visibility.Visible; TitleBox.Focus(); }
    private void CloseMeetingDialog() { MeetingDialogOverlay.Visibility = Visibility.Collapsed; }

    private void NavigateTo(int index)
    {
        MainTabs.SelectedIndex = index;
        UpdateSidebarSelection();
    }

    private void UpdateResponsiveLayout()
    {
        if (SidebarColumn == null) return;
        SidebarColumn.Width = new GridLength(ActualWidth < 960 ? 175 : 220);
    }

    private void UpdateSidebarSelection()
    {
        if (OverviewNavButton == null || PreferencesNavButton == null) return;
        var buttons = new[] { OverviewNavButton, PreferencesNavButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = MainTabs.SelectedIndex == (i == 0 ? 0 : 2);
            buttons[i].Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(active ? "#29466D" : (settings.DarkMode ? "#101722" : "#101D35")));
            buttons[i].Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(active ? "#FFFFFF" : "#D6E3F5"));
        }
    }

    private void RepeatEnabled_Changed(object sender, RoutedEventArgs e) => UpdateWeekdaysVisibility();

    private void UpdateWeekdaysVisibility()
    {
        if (RecurrencePanel == null || WeekdayPanel == null || RepeatBox == null || RepeatEnabledBox == null) return;
        bool enabled = RepeatEnabledBox.IsChecked == true;
        RecurrencePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        WeekdayPanel.Visibility = enabled && (RepeatBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "Weekdays" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool OccursOn(Meeting m, DateTime day)
    {
        if (day.Date < m.Start.Date) return false;
        if (m.RepeatUntil.HasValue && day.Date > m.RepeatUntil.Value.Date) return false;
        if (m.SkippedDates?.Contains(day.ToString("yyyy-MM-dd")) == true) return false;
        int elapsed = (day.Date - m.Start.Date).Days;
        return m.Repeat switch
        {
            "Once" => elapsed == 0,
            "Daily" => true,
            "Weekly" => elapsed % 7 == 0,
            "Every 2 weeks" or "Biweekly" => elapsed % 14 == 0,
            "Weekdays" => (m.Weekdays ?? new List<DayOfWeek>()).Contains(day.DayOfWeek),
            _ => false
        };
    }

    private void LoadSettings()
    {
        loadingSettings = true;
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)) ?? new(); }
        catch { settings = new(); }
        StartWithWindows.IsChecked = settings.StartWithWindows;
        SoundBox.IsChecked = settings.Sound;
        FlashBox.IsChecked = settings.Flash;
        ThemeBox.SelectedIndex = settings.DarkMode ? 1 : 0;
        TimeFormatBox.SelectedIndex = settings.Use24HourTime ? 1 : 0;
        InitializeTimePickers();
        DurationBox.Text = settings.DefaultDuration.ToString();
        if (ShowDashboardCardsBox != null) ShowDashboardCardsBox.IsChecked = settings.ShowDashboardCards;
        if (DailySummaryEnabledBox != null) DailySummaryEnabledBox.IsChecked = settings.DailySummaryEnabled;
        if (DailySummaryTimeBox != null) DailySummaryTimeBox.Text = settings.DailySummaryTime;
        if (WorkStartBox != null) WorkStartBox.Text = settings.WorkdayStart;
        if (WorkEndBox != null) WorkEndBox.Text = settings.WorkdayEnd;
        if (StreamDeckEnabledBox != null) StreamDeckEnabledBox.IsChecked = settings.StreamDeckEnabled;
        if (StreamDeckPortBox != null) StreamDeckPortBox.Text = settings.StreamDeckPort.ToString();
        if (CustomSoundPathText != null) CustomSoundPathText.Text = string.IsNullOrWhiteSpace(settings.CustomSoundPath) ? "" : $"Custom sound: {settings.CustomSoundPath}";

        foreach (System.Windows.Controls.ComboBoxItem item in AlertPositionBox.Items)
            if (string.Equals(item.Tag?.ToString(), settings.AlertPosition, StringComparison.OrdinalIgnoreCase)) { AlertPositionBox.SelectedItem = item; break; }

        foreach (System.Windows.Controls.ComboBoxItem item in SoundChoiceBox.Items)
            if (string.Equals(item.Tag?.ToString(), settings.SoundChoice, StringComparison.OrdinalIgnoreCase)) { SoundChoiceBox.SelectedItem = item; break; }

        var workDays = settings.WorkingDays ?? new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        if (WorkMonBox != null) WorkMonBox.IsChecked = workDays.Contains(DayOfWeek.Monday);
        if (WorkTueBox != null) WorkTueBox.IsChecked = workDays.Contains(DayOfWeek.Tuesday);
        if (WorkWedBox != null) WorkWedBox.IsChecked = workDays.Contains(DayOfWeek.Wednesday);
        if (WorkThuBox != null) WorkThuBox.IsChecked = workDays.Contains(DayOfWeek.Thursday);
        if (WorkFriBox != null) WorkFriBox.IsChecked = workDays.Contains(DayOfWeek.Friday);
        if (WorkSatBox != null) WorkSatBox.IsChecked = workDays.Contains(DayOfWeek.Saturday);
        if (WorkSunBox != null) WorkSunBox.IsChecked = workDays.Contains(DayOfWeek.Sunday);
        loadingSettings = false;
    }

    private sealed class AppSettings
    {
        public bool StartWithWindows { get; set; }
        public bool Sound { get; set; } = true;
        public bool Flash { get; set; } = true;
        public bool DarkMode { get; set; }
        public bool Use24HourTime { get; set; } = false;
        public int DefaultDuration { get; set; } = 30;
        public bool ShowDashboardCards { get; set; } = true;
        public string AlertPosition { get; set; } = "Center";
        public string SoundChoice { get; set; } = "Exclamation";
        public string CustomSoundPath { get; set; } = "";
        public bool DailySummaryEnabled { get; set; } = true;
        public string DailySummaryTime { get; set; } = "08:30";
        public string WorkdayStart { get; set; } = "08:00";
        public string WorkdayEnd { get; set; } = "17:00";
        public List<DayOfWeek> WorkingDays { get; set; } = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        public bool StreamDeckEnabled { get; set; } = false;
        public bool RemindersPaused { get; set; } = false;
        public List<PtoPeriod> TimeOff { get; set; } = new();
        public int StreamDeckPort { get; set; } = 48291;
    }

    private void PersistSettings()
    {
        if (!IsLoaded || loadingSettings || SoundBox == null || FlashBox == null || ThemeBox == null || DurationBox == null || StartWithWindows == null) return;
        settings.StartWithWindows = StartWithWindows.IsChecked == true;
        settings.Sound = SoundBox.IsChecked == true;
        settings.Flash = FlashBox.IsChecked == true;
        settings.DarkMode = ThemeBox.SelectedIndex == 1;
        settings.Use24HourTime = TimeFormatBox.SelectedIndex == 1;
        settings.DefaultDuration = int.TryParse(DurationBox.Text, out var n) && n > 0 && n <= 1440 ? n : 30;
        if (ShowDashboardCardsBox != null) settings.ShowDashboardCards = ShowDashboardCardsBox.IsChecked == true;
        if (AlertPositionBox?.SelectedItem is System.Windows.Controls.ComboBoxItem posItem)
            settings.AlertPosition = posItem.Tag?.ToString() ?? "Center";
        if (SoundChoiceBox?.SelectedItem is System.Windows.Controls.ComboBoxItem sndItem)
            settings.SoundChoice = sndItem.Tag?.ToString() ?? "Exclamation";
        if (DailySummaryEnabledBox != null) settings.DailySummaryEnabled = DailySummaryEnabledBox.IsChecked == true;
        if (DailySummaryTimeBox != null && TimeSpan.TryParseExact(DailySummaryTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _))
            settings.DailySummaryTime = DailySummaryTimeBox.Text.Trim();
        if (WorkStartBox != null && TimeSpan.TryParseExact(WorkStartBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _))
            settings.WorkdayStart = WorkStartBox.Text.Trim();
        if (WorkEndBox != null && TimeSpan.TryParseExact(WorkEndBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _))
            settings.WorkdayEnd = WorkEndBox.Text.Trim();
        if (StreamDeckEnabledBox != null) settings.StreamDeckEnabled = StreamDeckEnabledBox.IsChecked == true;
        if (StreamDeckPortBox != null && int.TryParse(StreamDeckPortBox.Text.Trim(), out var sdPort) && sdPort is >= 1024 and <= 65535)
            settings.StreamDeckPort = sdPort;

        if (WorkMonBox != null && WorkTueBox != null && WorkWedBox != null && WorkThuBox != null && WorkFriBox != null && WorkSatBox != null && WorkSunBox != null)
        {
            var days = new List<DayOfWeek>();
            if (WorkMonBox.IsChecked == true) days.Add(DayOfWeek.Monday);
            if (WorkTueBox.IsChecked == true) days.Add(DayOfWeek.Tuesday);
            if (WorkWedBox.IsChecked == true) days.Add(DayOfWeek.Wednesday);
            if (WorkThuBox.IsChecked == true) days.Add(DayOfWeek.Thursday);
            if (WorkFriBox.IsChecked == true) days.Add(DayOfWeek.Friday);
            if (WorkSatBox.IsChecked == true) days.Add(DayOfWeek.Saturday);
            if (WorkSunBox.IsChecked == true) days.Add(DayOfWeek.Sunday);
            settings.WorkingDays = days;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        settings.RemindersPaused = paused;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "WAV audio (*.wav)|*.wav", Title = "Choose reminder sound" };
        if (picker.ShowDialog() == true)
        {
            settings.CustomSoundPath = picker.FileName;
            SoundChoiceBox.SelectedIndex = 3;
            CustomSoundPathText.Text = $"Custom sound: {picker.FileName}";
            PersistSettings();
        }
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || loadingSettings || RootPanel == null || SoundBox == null || FlashBox == null || DurationBox == null) return;
        PersistSettings();
        ApplyTheme();
        InitializeTimePickers();
        RefreshStatus();
        RenderMonth();
        InitializeStreamDeckBridge();
    }

    private string FormatMeetingTime(DateTime value) => value.ToString(settings.Use24HourTime ? "HH:mm" : "h:mm tt", CultureInfo.GetCultureInfo("en-GB"));

    private bool TryReadMeetingTime(string text, out TimeSpan time)
    {
        if (TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1)) return true;
        if (DateTime.TryParseExact(text.Trim(), new[] { "h:mm tt", "hh:mm tt", "h tt", "htt", "h:mmtt" }, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var value))
        { time = value.TimeOfDay; return true; }
        time = default;
        return false;
    }

    private bool updatingTimes;

    private void UpdateEndFromDuration()
    {
        if (updatingTimes || TimeBox == null || EndTimeBox == null || MeetingDurationBox == null) return;
        if (MeetingDurationBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selected ||
            !int.TryParse(selected.Tag?.ToString(), out int duration)) return;
        if (!TryReadMeetingTime(TimeBox.Text, out var start)) return;
        if (start.TotalMinutes + duration >= 1440) return;
        updatingTimes = true;
        EndTimeBox.Text = FormatMeetingTime(DateTime.Today + start + TimeSpan.FromMinutes(duration));
        updatingTimes = false;
    }

    private void MeetingDuration_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateEndFromDuration();
    private void StartTime_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateEndFromDuration();
    private void StartTime_LostFocus(object sender, RoutedEventArgs e) => UpdateEndFromDuration();
    private void EndTime_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => DetectManualDuration();
    private void EndTime_LostFocus(object sender, RoutedEventArgs e) => DetectManualDuration();

    private void DetectManualDuration()
    {
        if (updatingTimes || MeetingDurationBox == null || EndTimeBox == null || TimeBox == null) return;
        if (!TryReadMeetingTime(TimeBox.Text, out var start) ||
            !TryReadMeetingTime(EndTimeBox.Text, out var end) || end <= start) return;
        int duration = (int)(end - start).TotalMinutes;
        updatingTimes = true;
        MeetingDurationBox.SelectedIndex = duration == 30 ? 0 : duration == 60 ? 1 : 2;
        updatingTimes = false;
    }

    private void InitializeTimePickers()
    {
        if (TimeBox == null || EndTimeBox == null) return;
        var start = TryReadMeetingTime(TimeBox.Text, out var existingStart) ? existingStart : TimeSpan.FromHours(14);
        var end = TryReadMeetingTime(EndTimeBox.Text, out var existingEnd) ? existingEnd : TimeSpan.FromHours(14.5);
        updatingTimes = true;
        foreach (var picker in new[] { TimeBox, EndTimeBox })
        {
            picker.Items.Clear();
            for (int hour = 0; hour < 24; hour++)
                for (int minute = 0; minute < 60; minute += 15)
                    picker.Items.Add(FormatMeetingTime(DateTime.Today.AddHours(hour).AddMinutes(minute)));
        }
        TimeBox.Text = FormatMeetingTime(DateTime.Today + start);
        EndTimeBox.Text = FormatMeetingTime(DateTime.Today + end);
        MeetingDurationBox.SelectedIndex = 0;
        updatingTimes = false;
    }

    private void ApplyTheme()
    {
        if (RootPanel == null) return;
        bool dark = settings.DarkMode;
        var palette = new Dictionary<string, string>
        {
            ["PageBrush"] = dark ? "#15181E" : "#F5F7FB",
            ["CardBrush"] = dark ? "#222731" : "#FFFFFF",
            ["TextBrush"] = dark ? "#F2F5FA" : "#172033",
            ["MutedBrush"] = dark ? "#AAB5C7" : "#667085",
            ["BorderBrushTheme"] = dark ? "#3B4350" : "#E4E8F0",
            ["InputBrush"] = dark ? "#2A303B" : "#FFFFFF",
            ["InputTextBrush"] = dark ? "#F2F5FA" : "#172033",
            ["SidebarBrush"] = dark ? "#101722" : "#101D35",
            ["SidebarTextBrush"] = dark ? "#D6E3F5" : "#D6E3F5",
            ["AccentTextBrush"] = dark ? "#78B8FF" : "#0067C0",
            ["SubtleBrush"] = dark ? "#2B313C" : "#F3F6FA",
            ["NavActiveBrush"] = dark ? "#29466D" : "#243C60"
        };
        foreach (var item in palette)
        {
            System.Windows.Application.Current.Resources[item.Key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(item.Value));
        }
        Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PageBrush"];
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextBrush"];
        foreach (var tb in new[] { TitleBox, JoinBox, NotesBox, SearchBox, DurationBox, DailySummaryTimeBox, WorkStartBox, WorkEndBox })
        {
            if (tb == null) continue;
            tb.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputBrush"];
            tb.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputTextBrush"];
        }
        foreach (var picker in new[] { TimeBox, EndTimeBox, MeetingDurationBox })
        {
            picker.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputBrush"];
            picker.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputTextBrush"];
        }
        foreach (var control in new System.Windows.Controls.Control[] { MeetingDate, ViewBox, BeforeBox, RepeatBox, ThemeBox, AlertPositionBox, SoundChoiceBox })
        {
            if (control == null) continue;
            control.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputBrush"];
            control.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["InputTextBrush"];
            control.BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BorderBrushTheme"];
        }
        MeetingList.Foreground = Foreground;
        UpdateSidebarSelection();
        RefreshOverviewUi();
    }

    private void RefreshStatus()
    {
        var now = DateTime.Now;
        var next = meetings
            .Select(m => (Original: m, When: NextOccurrence(m, now)))
            .Where(x => x.When.HasValue)
            .OrderBy(x => x.When!.Value)
            .FirstOrDefault();

        NextMeetingText.Text = next.Original == null
            ? "No upcoming meetings"
            : $"Next: {next.Original.Title} • {next.When:ddd HH:mm} • starts in {Math.Max(0, (int)Math.Ceiling((next.When!.Value - now).TotalMinutes))} min";
        if (SidebarReminderStatus != null)
        {
            SidebarReminderStatus.Text = IsOnPto(DateTime.Today) ? "On leave" : (paused ? "Paused" : "Active");
            SidebarReminderStatus.Foreground = new System.Windows.Media.SolidColorBrush(paused ? System.Windows.Media.Colors.Orange : System.Windows.Media.Color.FromRgb(134, 239, 172));
        }
        if (SidebarRemindersToggle != null)
        {
            syncingReminderToggle = true;
            SidebarRemindersToggle.IsChecked = !paused;
            syncingReminderToggle = false;
        }
        PauseButton.Content = paused ? "Resume alerts" : "Pause alerts";
        MissedText.Text = $"Missed reminders: {missedEntries.Count}";
        if (MissedCentreNavButton != null)
            MissedCentreNavButton.Content = $"Missed Meetings ({missedEntries.Count})";
    }

    private void SidebarReminders_Changed(object sender, RoutedEventArgs e)
    {
        if (syncingReminderToggle || !IsLoaded || loadingSettings) return;
        paused = SidebarRemindersToggle.IsChecked != true;
        settings.RemindersPaused = paused;
        PersistSettings();
        RefreshStatus();
    }

    private void ToggleReminders()
    {
        paused = !paused;
        settings.RemindersPaused = paused;
        PersistSettings();
        RefreshStatus();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => ToggleReminders();

    private void ResetCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (meetings.Count == 0) { MessageBox.Show("Your calendar is already empty.", "Meeting Buddy"); return; }
        if (MessageBox.Show($"Delete all {meetings.Count} meetings and recurring series? A backup will be saved first. This cannot be undone from within the app.",
                "Reset calendar", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var backupFolder = Path.Combine(Path.GetDirectoryName(dataPath)!, "Backups");
            Directory.CreateDirectory(backupFolder);
            var backup = Path.Combine(backupFolder, $"reset-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(backup, JsonSerializer.Serialize(meetings.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            meetings.Clear();
            snoozes.Clear();
            missedEntries.Clear();
            missedKeys.Clear();
            alertStages.Clear();
            Save();
            RefreshUpcoming();
            RefreshStatus();
            MessageBox.Show($"Calendar reset. Backup saved to:\\n{backup}", "Meeting Buddy");
        }
        catch (Exception ex) { MessageBox.Show($"Could not reset calendar: {ex.Message}", "Meeting Buddy", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private bool calendarMode;
    private DateTime visibleMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void ListView_Click(object sender, RoutedEventArgs e)
    {
        calendarMode = false;
        CalendarCard.Visibility = Visibility.Collapsed;
        MeetingsCard.Visibility = Visibility.Visible;
        ViewBox.Visibility = Visibility.Visible;
        RefreshUpcoming();
    }

    private void CalendarView_Click(object sender, RoutedEventArgs e)
    {
        calendarMode = true;
        CalendarCard.Visibility = Visibility.Visible;
        MeetingsCard.Visibility = Visibility.Collapsed;
        ViewBox.Visibility = Visibility.Collapsed;
        RenderMonth();
        RefreshOverviewUi();
    }

    private void CalendarPrevious_Click(object sender, RoutedEventArgs e) { visibleMonth = visibleMonth.AddMonths(-1); RenderMonth(); }
    private void CalendarNext_Click(object sender, RoutedEventArgs e) { visibleMonth = visibleMonth.AddMonths(1); RenderMonth(); }

    private void CalendarToday_Click(object sender, RoutedEventArgs e)
    {
        visibleMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderMonth();
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not DateTime day) return;
        CancelEdit_Click(sender, e);
        MeetingDate.SelectedDate = day;
        OpenMeetingDialog();
    }

    private void CalendarMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not Meeting occurrence) return;
        OpenDetailsDialog(occurrence);
    }

    private static System.Windows.Media.Brush GetEventBrush(string? hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch { return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)); }
    }

    private void SelectEventColor(string? hex)
    {
        foreach (System.Windows.Controls.ComboBoxItem option in EventColorBox.Items)
            if (string.Equals(option.Tag?.ToString(), hex, StringComparison.OrdinalIgnoreCase))
            { EventColorBox.SelectedItem = option; return; }
        EventColorBox.SelectedIndex = 0;
    }

    private void RenderMonth()
    {
        if (!calendarMode || MonthCells == null) return;
        CalendarMonthLabel.Text = visibleMonth.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        MonthCells.Children.Clear();
        var first = visibleMonth.AddDays(-((int)visibleMonth.DayOfWeek + 6) % 7);
        var textBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextBrush"];
        var muted = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["MutedBrush"];
        var borderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BorderBrushTheme"];
        var accent = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
        for (int i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var currentMonth = day.Month == visibleMonth.Month;
            var compact = MonthCells.ActualWidth > 0 && MonthCells.ActualWidth < 780;
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(compact ? 2 : 5) };
            var dayButton = new System.Windows.Controls.Button
            {
                Content = day.Day.ToString(CultureInfo.InvariantCulture),
                Tag = day,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                MinWidth = 30,
                Padding = new Thickness(5, 2, 5, 2),
                Foreground = currentMonth ? textBrush : muted,
                Background = day.Date == DateTime.Today ? accent : System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontWeight = day.Date == DateTime.Today ? FontWeights.Bold : FontWeights.Normal
            };
            if (day.Date == DateTime.Today) dayButton.Foreground = System.Windows.Media.Brushes.White;
            dayButton.Click += CalendarDay_Click;
            panel.Children.Add(dayButton);
            var occurrences = meetings.Where(m => OccursOn(m, day)).OrderBy(m => m.Start.TimeOfDay).ToList();
            if (compact && occurrences.Count > 0)
            {
                var countButton = new System.Windows.Controls.Button
                {
                    Content = $"{occurrences.Count} meeting{(occurrences.Count == 1 ? "" : "s")}",
                    Tag = day,
                    FontSize = 10,
                    Padding = new Thickness(2),
                    ToolTip = string.Join("\n", occurrences.Select(m => $"{FormatMeetingTime(day.Date + m.Start.TimeOfDay)}  {m.Title}")),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
                };
                countButton.Click += CalendarDay_Click;
                panel.Children.Add(countButton);
            }
            foreach (var meeting in (compact ? Enumerable.Empty<Meeting>() : occurrences.Take(3)))
            {
                var occurrence = new Meeting
                {
                    Id = meeting.Id,
                    Title = meeting.Title,
                    Start = day.Date + meeting.Start.TimeOfDay,
                    DurationMinutes = meeting.DurationMinutes,
                    Repeat = meeting.Repeat,
                    MinutesBefore = meeting.MinutesBefore,
                    Weekdays = meeting.Weekdays,
                    JoinUrl = meeting.JoinUrl,
                    EventColor = meeting.EventColor,
                    Notes = meeting.Notes
                };
                var chip = new System.Windows.Controls.Button
                {
                    Content = $"{FormatMeetingTime(occurrence.Start)}  {occurrence.Title}",
                    Tag = occurrence,
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 3, 0, 0),
                    Padding = new Thickness(5, 5, 5, 5),
                    Background = GetEventBrush(meeting.EventColor),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    ToolTip = $"{occurrence.Title} • {FormatMeetingTime(occurrence.Start)}–{FormatMeetingTime(occurrence.End)} • {occurrence.Repeat}"
                };
                chip.Click += CalendarMeeting_Click;
                panel.Children.Add(chip);
            }
            if (!compact && occurrences.Count > 3)
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"+{occurrences.Count - 3} more", Foreground = muted, Margin = new Thickness(3, 5, 0, 0), FontSize = 11 });
            MonthCells.Children.Add(new System.Windows.Controls.Border
            {
                Child = panel,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(5),
                MinHeight = compact ? 55 : 100,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["CardBrush"]
            });
        }
    }

    private void MonthCells_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((e.PreviousSize.Width < 780) != (e.NewSize.Width < 780)) RenderMonth();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        searchQuery = SearchBox?.Text?.Trim() ?? "";
        RefreshUpcoming();
    }

    private void View_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewBox == null) return;
        viewMode = (ViewBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Today";
        RefreshUpcoming();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is not Meeting selected) return;
        var m = meetings.FirstOrDefault(x => x.Id == selected.Id);
        if (m == null) return;
        EditMeeting(m);
    }

    private void EditMeeting(Meeting m)
    {
        editingId = m.Id;
        TitleBox.Text = m.Title;
        MeetingDate.SelectedDate = m.Start.Date;
        SelectEventColor(m.EventColor);
        updatingTimes = true;
        TimeBox.Text = FormatMeetingTime(m.Start);
        EndTimeBox.Text = FormatMeetingTime(m.End);
        MeetingDurationBox.SelectedIndex = m.DurationMinutes == 30 ? 0 : m.DurationMinutes == 60 ? 1 : 2;
        updatingTimes = false;
        JoinBox.Text = m.JoinUrl;
        NotesBox.Text = m.Notes ?? "";
        RepeatEnabledBox.IsChecked = m.Repeat != "Once";
        RepeatBox.SelectedIndex = 1;
        foreach (System.Windows.Controls.ComboBoxItem item in RepeatBox.Items)
            if (item.Content?.ToString() == (m.Repeat == "Biweekly" ? "Every 2 weeks" : m.Repeat)) { RepeatBox.SelectedItem = item; break; }
        UpdateWeekdaysVisibility();
        foreach (System.Windows.Controls.ComboBoxItem item in BeforeBox.Items) if (item.Tag?.ToString() == m.MinutesBefore.ToString()) { BeforeBox.SelectedItem = item; break; }
        foreach (var (day, box) in DayBoxes()) box.IsChecked = m.Weekdays.Contains(day);
        AddButton.Content = "Save changes";
        CancelEditButton.Visibility = Visibility.Visible;
        OpenMeetingDialog();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        editingId = null;
        AddButton.Content = "+  Save meeting";
        CancelEditButton.Visibility = Visibility.Collapsed;
        TitleBox.Clear();
        JoinBox.Clear();
        NotesBox.Clear();
        EventColorBox.SelectedIndex = 0;
        RepeatEnabledBox.IsChecked = false;
        RepeatBox.SelectedIndex = 1;
        UpdateWeekdaysVisibility();
        CloseMeetingDialog();
    }

    private IEnumerable<(DayOfWeek, System.Windows.Controls.CheckBox)> DayBoxes() => new[]
    {
        (DayOfWeek.Monday, MonBox),
        (DayOfWeek.Tuesday, TueBox),
        (DayOfWeek.Wednesday, WedBox),
        (DayOfWeek.Thursday, ThuBox),
        (DayOfWeek.Friday, FriBox),
        (DayOfWeek.Saturday, SatBox),
        (DayOfWeek.Sunday, SunBox)
    };

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        var enabled = StartWithWindows.IsChecked == true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(StartupKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
                if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Publish the app before enabling Windows startup.");
                key.SetValue(StartupName, $"\"{exe}\" --tray");
            }
            else key.DeleteValue(StartupName, false);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            settings.StartWithWindows = enabled;
            PersistSettings();
        }
        catch (Exception ex)
        {
            loadingSettings = true;
            StartWithWindows.IsChecked = !enabled;
            loadingSettings = false;
            MessageBox.Show("Could not update Windows startup: " + ex.Message);
        }
    }

    private void ShowApp() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        ticker.Stop();
        StopStreamDeckBridge();
        tray.Visible = false;
        tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!exiting && !System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(dataPath))
            {
                foreach (var m in JsonSerializer.Deserialize<Meeting[]>(File.ReadAllText(dataPath)) ?? [])
                {
                    if (m.DurationMinutes <= 0) m.DurationMinutes = 30;
                    meetings.Add(m);
                }
                return;
            }
        }
        catch
        {
            // Attempt automatic recovery from the most recent daily backup
            try
            {
                var backupDir = Path.Combine(Path.GetDirectoryName(dataPath)!, "Backups");
                var latestBackup = Directory.Exists(backupDir)
                    ? Directory.GetFiles(backupDir, "meetings-*.json").OrderByDescending(x => x).FirstOrDefault()
                    : null;
                if (latestBackup != null)
                {
                    foreach (var m in JsonSerializer.Deserialize<Meeting[]>(File.ReadAllText(latestBackup)) ?? [])
                    {
                        if (m.DurationMinutes <= 0) m.DurationMinutes = 30;
                        meetings.Add(m);
                    }
                    MessageBox.Show($"Primary data file was unreadable. Automatically recovered {meetings.Count} meetings from backup:\n{Path.GetFileName(latestBackup)}", "Meeting Buddy Recovery");
                    return;
                }
            }
            catch { }
            MessageBox.Show("Could not read saved meetings or backups.");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        BackupMeetings();
        var tmpPath = dataPath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(meetings.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, dataPath, overwrite: true);
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(dataPath)!, "Backups");
        var backups = Directory.Exists(backupDir) ? Directory.GetFiles(backupDir, "meetings-*.json").OrderByDescending(x => x).ToArray() : Array.Empty<string>();
        var latestBackup = backups.FirstOrDefault() is string b ? Path.GetFileName(b) : "None yet";
        int port = settings.StreamDeckPort is >= 1024 and <= 65535 ? settings.StreamDeckPort : 48291;

        var report =
            $"Meeting Buddy v3.3 — Diagnostics & Reliability\n\n" +
            $"Total stored meetings: {meetings.Count}\n" +
            $"Recurring series: {meetings.Count(m => m.IsRecurring)}\n" +
            $"Pending snoozes: {snoozes.Count}\n" +
            $"Unacknowledged missed alerts: {missedEntries.Count}\n" +
            $"Stream Deck API: {(settings.StreamDeckEnabled ? $"Active (http://127.0.0.1:{port})" : "Disabled")}\n\n" +
            $"Data file: {dataPath}\n" +
            $"Watched ICS folder: {ImportFolder}\n" +
            $"Daily backups retained: {backups.Length} (Latest: {latestBackup})\n\n" +
            $"Open the Meeting Buddy data & backups folder in Explorer?";

        if (MessageBox.Show(report, "Diagnostics & Troubleshooting", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(dataPath)!) { UseShellExecute = true }); } catch { }
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text) || MeetingDate.SelectedDate is null || !TryReadMeetingTime(TimeBox.Text, out var time) || time >= TimeSpan.FromDays(1))
        {
            MessageBox.Show("Enter a title, date, and valid meeting time (e.g. 2:30 PM or 14:30).");
            return;
        }
        if (!TryReadMeetingTime(EndTimeBox.Text, out var endTime) || endTime >= TimeSpan.FromDays(1) || endTime <= time)
        {
            MessageBox.Show("Enter an end time after the start time.");
            return;
        }
        var start = MeetingDate.SelectedDate.Value.Date + time;
        var repeat = RepeatEnabledBox.IsChecked == true ? ((RepeatBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Weekly") : "Once";
        if (repeat == "Once" && start < DateTime.Now && !editingId.HasValue)
        {
            MessageBox.Show("Choose a future meeting time.");
            return;
        }
        var selectedDays = DayBoxes().Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList();
        if (repeat == "Weekdays" && selectedDays.Count == 0)
        {
            MessageBox.Show("Select at least one weekday.");
            return;
        }
        // Warn about one-off overlaps without silently blocking intentional double bookings.
        // Recurring series require occurrence expansion and are handled separately.
        if (repeat == "Once")
        {
            var proposedEnd = start.Add(endTime - time);
            var conflicts = meetings
                .Where(m => m.Id != editingId && !m.IsRecurring &&
                            m.Start < proposedEnd && m.End > start)
                .OrderBy(m => m.Start)
                .Take(3)
                .ToList();
            if (conflicts.Count > 0)
            {
                var details = string.Join("\n", conflicts.Select(m =>
                    $"• {m.Title} ({m.Start:HH:mm}–{m.End:HH:mm})"));
                var result = MessageBox.Show(
                    $"This meeting overlaps with an existing meeting:\n\n{details}\n\nSave anyway?",
                    "Scheduling conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
        }
        var item = editingId.HasValue ? meetings.FirstOrDefault(x => x.Id == editingId.Value) : null;
        if (item == null) { item = new Meeting(); meetings.Add(item); }
        item.Title = TitleBox.Text.Trim();
        item.Start = start;
        item.DurationMinutes = (int)(endTime - time).TotalMinutes;
        item.Repeat = repeat;
        item.Weekdays = selectedDays;
        item.MinutesBefore = int.Parse((BeforeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "5");
        item.JoinUrl = JoinBox.Text.Trim();
        item.Notes = NotesBox.Text.Trim();
        item.EventColor = (EventColorBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "#2563EB";
        Save();
        CancelEdit_Click(sender, e);
        CloseMeetingDialog();
        RefreshUpcoming();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is Meeting m)
        {
            var original = meetings.FirstOrDefault(x => x.Id == m.Id);
            if (original != null) meetings.Remove(original);
            Save();
            RefreshUpcoming();
        }
    }

    private void Test_Click(object sender, RoutedEventArgs e) =>
        ShowAlert("Test Pre-Meeting Reminder", "Starts in 5 minutes — snooze is available before start.", DateTime.Now.AddMinutes(5), "", false);

    private void TestLive_Click(object sender, RoutedEventArgs e) =>
        ShowAlert("Test Live Meeting Alert", "Meeting starts now — stays open until Join Zoom or Acknowledge is clicked.", DateTime.Now, "https://zoom.us/j/123456789", true);

    private void ShowAlert(string title, string message, DateTime meetingStart, string url = "", bool isLive = false, Guid? meetingId = null, string? missedKey = null)
    {
        if (paused || IsOnPto(meetingStart)) return;
        var popup = new AlertWindow(
            title,
            message,
            meetingStart,
            url,
            isLive,
            settings.Flash,
            settings.Sound,
            settings.SoundChoice,
            settings.CustomSoundPath,
            settings.AlertPosition,
            snoozeMins =>
            {
                var target = DateTime.Now.AddMinutes(snoozeMins);
                if (target < meetingStart)
                {
                    // Keep only the latest snooze for this occurrence; avoid duplicate popups.
                    snoozes.RemoveAll(s => s.MeetingStart == meetingStart &&
                        string.Equals(s.Title, title, StringComparison.Ordinal) &&
                        string.Equals(s.Url, url, StringComparison.Ordinal));
                    snoozes.Add((target, meetingStart, title, url));
                }
            },
            () =>
            {
                if (meetingId.HasValue)
                {
                    var orig = meetings.FirstOrDefault(m => m.Id == meetingId.Value);
                    if (orig != null)
                    {
                        orig.LastLinkOpenedUtc = DateTime.UtcNow;
                        Save();
                    }
                }
                if (!string.IsNullOrEmpty(missedKey))
                {
                    missedKeys.Remove(missedKey);
                    var existing = missedEntries.FirstOrDefault(x => x.Key == missedKey);
                    if (existing != null) missedEntries.Remove(existing);
                    RefreshStatus();
                }
            },
            () =>
            {
                if (!string.IsNullOrEmpty(missedKey))
                {
                    missedKeys.Remove(missedKey);
                    var existing = missedEntries.FirstOrDefault(x => x.Key == missedKey);
                    if (existing != null) missedEntries.Remove(existing);
                    RefreshStatus();
                }
            });
        popup.Show();
    }

    private DateTime? NextOccurrence(Meeting m, DateTime now)
    {
        var duration = TimeSpan.FromMinutes(m.DurationMinutes > 0 ? m.DurationMinutes : 30);
        var firstDay = now.Date.AddDays(-1);
        for (var offset = 0; offset < 370; offset++)
        {
            var day = firstDay.AddDays(offset);
            if (day < m.Start.Date) continue;
            if (!OccursOn(m, day) || (m.SkippedDates?.Contains(day.ToString("yyyy-MM-dd")) == true)) continue;
            var start = day + m.Start.TimeOfDay;
            if (start + duration > now) return start;
        }
        return null;
    }

    private DateTime? LastPastOccurrence(Meeting m, DateTime now)
    {
        var duration = TimeSpan.FromMinutes(m.DurationMinutes > 0 ? m.DurationMinutes : 30);
        for (var offset = 0; offset <= 180; offset++)
        {
            var day = now.Date.AddDays(-offset);
            if (day < m.Start.Date) break;
            if (!OccursOn(m, day)) continue;
            var start = day + m.Start.TimeOfDay;
            if (start + duration <= now) return start;
        }
        return null;
    }

    private bool MatchesSearch(Meeting m)
    {
        if (string.IsNullOrWhiteSpace(searchQuery)) return true;
        return m.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(m.Notes) && m.Notes.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshUpcoming()
    {
        var now = DateTime.Now;
        if (calendarMode)
        {
            RenderMonth();
            RefreshOverviewUi();
            return;
        }

        Meeting[] next;
        if (viewMode == "History")
        {
            next = meetings
                .Where(MatchesSearch)
                .Select(m => (Original: m, When: LastPastOccurrence(m, now)))
                .Where(x => x.When.HasValue)
                .OrderByDescending(x => x.When!.Value)
                .Select(x => new Meeting
                {
                    Id = x.Original.Id,
                    Title = x.Original.Title,
                    Start = x.When!.Value,
                    DurationMinutes = x.Original.DurationMinutes,
                    Repeat = x.Original.Repeat,
                    MinutesBefore = x.Original.MinutesBefore,
                    Weekdays = x.Original.Weekdays,
                    JoinUrl = x.Original.JoinUrl,
                    EventColor = x.Original.EventColor,
                    Notes = x.Original.Notes
                }).ToArray();
        }
        else
        {
            next = meetings
                .Where(MatchesSearch)
                .Select(m => (Original: m, When: NextOccurrence(m, now)))
                .Where(x => x.When.HasValue && (viewMode == "All" || (viewMode == "Today" ? x.When!.Value.Date == now.Date : x.When!.Value < now.Date.AddDays(7))))
                .OrderBy(x => x.When!.Value)
                .Select(x => new Meeting
                {
                    Id = x.Original.Id,
                    Title = x.Original.Title,
                    Start = x.When!.Value,
                    DurationMinutes = x.Original.DurationMinutes,
                    Repeat = x.Original.Repeat,
                    MinutesBefore = x.Original.MinutesBefore,
                    Weekdays = x.Original.Weekdays,
                    JoinUrl = x.Original.JoinUrl,
                    EventColor = x.Original.EventColor,
                    Notes = x.Original.Notes
                }).ToArray();
        }

        if (upcoming.Count == next.Length && upcoming.Zip(next).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.Start == pair.Second.Start &&
            pair.First.Notes == pair.Second.Notes &&
            pair.First.JoinUrl == pair.Second.JoinUrl))
        {
            RefreshOverviewUi();
            return;
        }

        var selectedId = (MeetingList.SelectedItem as Meeting)?.Id;
        upcoming.Clear();
        foreach (var m in next) upcoming.Add(m);
        MeetingList.SelectedItem = upcoming.FirstOrDefault(x => x.Id == selectedId);
        RefreshOverviewUi();
    }

    private void CheckMeetings()
    {
        var now = DateTime.Now;
        // Detect resume after sleep or a suspended UI thread. Missed reminders are
        // collected by the existing missed-meeting centre rather than replayed as popups.
        var resumedAfterGap = now - lastReminderCheck > TimeSpan.FromMinutes(2);
        lastReminderCheck = now;
        var workingDays = settings.WorkingDays ?? new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

        if (!paused && !IsOnPto(now) && settings.DailySummaryEnabled && workingDays.Contains(now.DayOfWeek) &&
            TimeSpan.TryParseExact(settings.DailySummaryTime, @"hh\:mm", CultureInfo.InvariantCulture, out var summaryTime))
        {
            var todayKey = now.ToString("yyyy-MM-dd");
            if (lastDailySummaryDate != todayKey && now.TimeOfDay >= summaryTime && now.TimeOfDay < summaryTime.Add(TimeSpan.FromMinutes(5)))
            {
                lastDailySummaryDate = todayKey;
                ShowDailySummary();
            }
        }

        foreach (var s in snoozes.ToArray())
        {
            if (now >= s.Due)
            {
                snoozes.Remove(s);
                if (now < s.MeetingStart)
                    ShowAlert(s.Title, $"Snoozed reminder — starts at {s.MeetingStart:HH:mm}", s.MeetingStart, s.Url, false);
            }
        }

        foreach (var m in meetings)
        {
            for (var day = now.Date.AddDays(-1); day <= now.Date.AddDays(1); day = day.AddDays(1))
            {
                if (day < m.Start.Date) continue;
                if (!workingDays.Contains(day.DayOfWeek) || IsOnPto(day)) continue;
                if (!OccursOn(m, day) || (m.SkippedDates?.Contains(day.ToString("yyyy-MM-dd")) == true)) continue;

                var when = day + m.Start.TimeOfDay;
                var key = $"{m.Id}:{when:O}";
                if (!alertStages.TryGetValue(key, out var stages)) alertStages[key] = stages = new();

                var offsets = new[] { m.MinutesBefore, 5, 0 }
                    .Distinct()
                    .Where(n => n <= Math.Max(0, m.MinutesBefore))
                    .OrderByDescending(n => n);

                foreach (var offset in offsets)
                {
                    var due = when.AddMinutes(-offset);
                    var mKey = $"{key}:{offset}";
                    if (!resumedAfterGap && now >= due && now < due.AddSeconds(75) && stages.Add(offset))
                    {
                        bool isLive = offset == 0;
                        ShowAlert(
                            m.Title,
                            isLive ? $"Meeting starts now ({when:HH:mm})" : $"Starts at {when:HH:mm} — {offset} minutes remaining",
                            when,
                            m.JoinUrl,
                            isLive,
                            m.Id,
                            mKey);
                    }
                    else if ((now >= due.AddSeconds(75) || (resumedAfterGap && now >= due)) && !stages.Contains(offset) && now < m.End)
                    {
                        stages.Add(offset);
                        if (missedKeys.Add(mKey))
                        {
                            missedEntries.Add(new MissedMeetingEntry
                            {
                                Key = mKey,
                                MeetingId = m.Id,
                                Title = m.Title,
                                Subtitle = offset == 0 ? $"Started at {when:ddd HH:mm}" : $"{offset}m warning missed • {when:ddd HH:mm}",
                                JoinUrl = m.JoinUrl
                            });
                        }
                    }
                }
            }
        }

        // Retain recent occurrence state; never clear the entire set and re-alert.
        if (alertStages.Count > 3000)
        {
            var cutoff = now.AddDays(-7);
            foreach (var key in alertStages.Keys.ToArray())
            {
                var separator = key.LastIndexOf(':');
                if (separator >= 0 && DateTime.TryParse(key[(separator + 1)..],
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurrence) &&
                    occurrence < cutoff)
                    alertStages.Remove(key);
            }
        }
    }
}