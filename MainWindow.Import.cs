using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Clipboard = System.Windows.Clipboard;

namespace MeetingBuddy;

public partial class MainWindow
{
    private FileSystemWatcher? importWatcher;
    private readonly HashSet<string> handledImports = new(StringComparer.OrdinalIgnoreCase);
    private string ImportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meeting Buddy", "Imports");

    private sealed class ParsedVEvent
    {
        public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Params { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string RawValue, string RawParams)> ExDates { get; } = new();
    }

    private void InitializeImportFolder()
    {
        Directory.CreateDirectory(ImportFolder);
        importWatcher = new FileSystemWatcher(ImportFolder, "*.ics")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        importWatcher.Created += (_, e) => Dispatcher.BeginInvoke(new Action(() => OfferImport(e.FullPath)));
        importWatcher.Renamed += (_, e) => Dispatcher.BeginInvoke(new Action(() => OfferImport(e.FullPath)));
        Closed += (_, _) => importWatcher?.Dispose();
    }

    private void OfferImport(string path)
    {
        if (!File.Exists(path)) return;
        if (!handledImports.Add(path + ":" + File.GetLastWriteTimeUtc(path).Ticks)) return;
        if (MessageBox.Show($"A new ICS file was detected:\n{Path.GetFileName(path)}\n\nPreview its Zoom meetings?", "Meeting Buddy", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            ImportFile(path);
    }

    private void ImportIcs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "Calendar exports (*.ics;*.zip)|*.ics;*.zip|All files|*.*",
            Title = "Import Google Calendar export"
        };
        if (picker.ShowDialog() == true) ImportFile(picker.FileName);
    }

    private static IEnumerable<string> ReadCalendarFiles(string path)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length > 8_000_000) continue;
                using var reader = new StreamReader(entry.Open());
                yield return reader.ReadToEnd();
            }
        }
        else if (new FileInfo(path).Length <= 8_000_000)
        {
            yield return File.ReadAllText(path);
        }
    }

    private static IEnumerable<ParsedVEvent> ParseEvents(string text)
    {
        var unfolded = Regex.Replace(text.Replace("\r\n", "\n"), "\n[ \t]", "");
        ParsedVEvent? current = null;
        foreach (var line in unfolded.Split('\n'))
        {
            if (line == "BEGIN:VEVENT") { current = new ParsedVEvent(); continue; }
            if (line == "END:VEVENT") { if (current != null) yield return current; current = null; continue; }
            if (current == null) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var propPart = line[..colon];
            var valPart = line[(colon + 1)..];
            var semi = propPart.IndexOf(';');
            var key = semi >= 0 ? propPart[..semi] : propPart;
            var paramStr = semi >= 0 ? propPart[(semi + 1)..] : "";

            if (key.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
            {
                current.ExDates.Add((valPart, paramStr));
            }
            else if (!current.Props.ContainsKey(key))
            {
                current.Props[key] = valPart;
                current.Params[key] = paramStr;
            }
        }
    }

    private static string? ExtractTzid(string paramStr)
    {
        if (string.IsNullOrWhiteSpace(paramStr)) return null;
        foreach (var part in paramStr.Split(';'))
        {
            if (part.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
                return part[5..].Trim('"');
        }
        return null;
    }

    private static bool TryResolveTimeZone(string tzid, out TimeZoneInfo tz)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out tz!)) return true;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(tzid, out var winId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(winId, out tz!)) return true;
        tz = TimeZoneInfo.Local;
        return false;
    }

    private static bool TryIcsDateTime(string value, string paramStr, out DateTime localDateTime, out bool isAllDay)
    {
        isAllDay = false;
        localDateTime = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            isAllDay = true;
            localDateTime = dateOnly;
            return true;
        }

        var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm'Z'", "yyyyMMdd'T'HHmm" };
        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                continue;

            if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                localDateTime = DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToLocalTime();
                return true;
            }

            var tzid = ExtractTzid(paramStr);
            if (!string.IsNullOrWhiteSpace(tzid))
            {
                if (!TryResolveTimeZone(tzid, out var sourceTz)) return false;
                var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                if (sourceTz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
                localDateTime = TimeZoneInfo.ConvertTime(unspecified, sourceTz, TimeZoneInfo.Local);
                return true;
            }

            localDateTime = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
            return true;
        }

        return false;
    }

    private static string? ExtractZoomUrl(ParsedVEvent ev)
    {
        var raw = string.Join(" ", new[]
        {
            ev.Props.GetValueOrDefault("DESCRIPTION", ""),
            ev.Props.GetValueOrDefault("LOCATION", ""),
            ev.Props.GetValueOrDefault("URL", "")
        });

        var cleaned = raw.Replace("\\n", " ").Replace("\\,", ",").Replace("\\;", ";");
        cleaned = WebUtility.HtmlDecode(cleaned);

        cleaned = Regex.Replace(cleaned, @"https?://(?:www\.)?google\.com/url\?q=([^&\s""'<>]+)", m =>
            Uri.UnescapeDataString(m.Groups[1].Value), RegexOptions.IgnoreCase);

        var match = Regex.Match(cleaned, @"https?://[\w.-]*zoom\.us/(?:j|my|w)/[^\s\\""'<>]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.TrimEnd('.', ',', ';', ')') : null;
    }

    private static string UnescapeIcsText(string text) =>
        text.Replace("\\n", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private bool IsWithinWorkingHours(DateTime localStart)
    {
        var startSpan = TimeSpan.TryParseExact(settings.WorkdayStart, @"hh\:mm", CultureInfo.InvariantCulture, out var s) ? s : new TimeSpan(8, 0, 0);
        var endSpan = TimeSpan.TryParseExact(settings.WorkdayEnd, @"hh\:mm", CultureInfo.InvariantCulture, out var e) ? e : new TimeSpan(17, 0, 0);
        return localStart.TimeOfDay >= startSpan && localStart.TimeOfDay <= endSpan;
    }

    private static bool TryParseRRule(string rrule, DateTime localStart, out string repeat, out List<DayOfWeek> weekdays, out DateTime? repeatUntil)
    {
        repeat = "Once";
        weekdays = new List<DayOfWeek>();
        repeatUntil = null;

        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim().ToUpperInvariant(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("FREQ", out var freq)) return false;

        int interval = 1;
        if (parts.TryGetValue("INTERVAL", out var intStr) && int.TryParse(intStr, out var parsedInt) && parsedInt > 0)
            interval = parsedInt;

        if (parts.TryGetValue("UNTIL", out var untilRaw) && TryIcsDateTime(untilRaw, "", out var untilLocal, out _))
        {
            repeatUntil = untilLocal.Date;
        }
        else if (parts.TryGetValue("COUNT", out var countStr) && int.TryParse(countStr, out var count) && count > 0)
        {
            repeatUntil = freq.ToUpperInvariant() switch
            {
                "DAILY" => localStart.Date.AddDays((count - 1) * interval),
                "WEEKLY" => localStart.Date.AddDays((count - 1) * 7 * interval),
                _ => null
            };
        }

        if (parts.TryGetValue("BYDAY", out var byDayRaw))
        {
            foreach (var token in byDayRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var code = token.Length >= 2 ? token[^2..].ToUpperInvariant() : token.ToUpperInvariant();
                var day = code switch
                {
                    "MO" => (DayOfWeek?)DayOfWeek.Monday,
                    "TU" => DayOfWeek.Tuesday,
                    "WE" => DayOfWeek.Wednesday,
                    "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday,
                    "SA" => DayOfWeek.Saturday,
                    "SU" => DayOfWeek.Sunday,
                    _ => null
                };
                if (day.HasValue && !weekdays.Contains(day.Value))
                    weekdays.Add(day.Value);
            }
        }

        switch (freq.ToUpperInvariant())
        {
            case "DAILY":
                if (interval != 1) return false;
                repeat = "Daily";
                return true;

            case "WEEKLY":
                if (interval == 2 && weekdays.Count <= 1)
                {
                    repeat = "Every 2 weeks";
                    return true;
                }
                if (interval == 1)
                {
                    if (weekdays.Count > 1)
                    {
                        repeat = "Weekdays";
                    }
                    else
                    {
                        repeat = "Weekly";
                        if (weekdays.Count == 1 && weekdays[0] != localStart.DayOfWeek)
                            repeat = "Weekdays";
                    }
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private static string GetBaseSeriesUid(string uid)
    {
        var idx = uid.IndexOf("_R20", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? uid[..idx] : uid;
    }

    private static bool IsSameMeeting(Meeting existing, Meeting incoming)
    {
        if (string.Equals(existing.SourceUid, incoming.SourceUid, StringComparison.OrdinalIgnoreCase))
            return true;

        if (incoming.SourceUid.EndsWith("|ONCE", StringComparison.OrdinalIgnoreCase))
        {
            var rawUid = incoming.SourceUid[..^5];
            if (string.Equals(existing.SourceUid, rawUid + "|", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.SourceUid, rawUid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(existing.Title.Trim(), incoming.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
               existing.Start == incoming.Start &&
               string.Equals(existing.Repeat, incoming.Repeat, StringComparison.OrdinalIgnoreCase);
    }

    private void ImportFile(string path)
    {
        try
        {
            var candidates = new List<Meeting>();
            var pendingExceptions = new List<(string RawUid, string BaseUid, DateTime OriginalLocalStart, Meeting? ModifiedMeeting, bool IsCancelled)>();

            int nonZoom = 0;
            int outsideHours = 0;
            int cancelledSkipped = 0;
            int unsupported = 0;

            foreach (var content in ReadCalendarFiles(path))
            {
                foreach (var ev in ParseEvents(content))
                {
                    var zoomUrl = ExtractZoomUrl(ev);
                    if (string.IsNullOrWhiteSpace(zoomUrl))
                    {
                        nonZoom++;
                        continue;
                    }

                    var uid = ev.Props.GetValueOrDefault("UID", "").Trim();
                    if (string.IsNullOrWhiteSpace(uid))
                    {
                        unsupported++;
                        continue;
                    }

                    if (!ev.Props.TryGetValue("DTSTART", out var rawStart) ||
                        !TryIcsDateTime(rawStart, ev.Params.GetValueOrDefault("DTSTART", ""), out var start, out var isAllDay) ||
                        isAllDay)
                    {
                        unsupported++;
                        continue;
                    }

                    var duration = 30;
                    if (ev.Props.TryGetValue("DTEND", out var rawEnd) &&
                        TryIcsDateTime(rawEnd, ev.Params.GetValueOrDefault("DTEND", ""), out var end, out _))
                    {
                        duration = Math.Clamp((int)Math.Round((end - start).TotalMinutes), 1, 1440);
                    }

                    var status = ev.Props.GetValueOrDefault("STATUS", "CONFIRMED").Trim();
                    bool isCancelled = status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                    var title = UnescapeIcsText(ev.Props.GetValueOrDefault("SUMMARY", "Zoom meeting"));
                    var baseUid = GetBaseSeriesUid(uid);

                    if (ev.Props.TryGetValue("RECURRENCE-ID", out var rawRecId))
                    {
                        if (!TryIcsDateTime(rawRecId, ev.Params.GetValueOrDefault("RECURRENCE-ID", ""), out var recOrigStart, out _))
                        {
                            unsupported++;
                            continue;
                        }

                        if (isCancelled)
                        {
                            cancelledSkipped++;
                            pendingExceptions.Add((uid, baseUid, recOrigStart, null, true));
                            continue;
                        }

                        if (!IsWithinWorkingHours(start))
                        {
                            outsideHours++;
                            pendingExceptions.Add((uid, baseUid, recOrigStart, null, false));
                            continue;
                        }

                        var exSourceUid = $"{uid}|EX|{recOrigStart:yyyyMMddTHHmm}";
                        var modMeeting = new Meeting
                        {
                            SourceUid = exSourceUid,
                            Title = title,
                            Start = start,
                            DurationMinutes = duration,
                            Repeat = "Once",
                            JoinUrl = zoomUrl,
                            MinutesBefore = 5
                        };
                        pendingExceptions.Add((uid, baseUid, recOrigStart, modMeeting, false));
                        continue;
                    }

                    if (isCancelled)
                    {
                        cancelledSkipped++;
                        continue;
                    }

                    if (!IsWithinWorkingHours(start))
                    {
                        outsideHours++;
                        continue;
                    }

                    var repeat = "Once";
                    var weekdays = new List<DayOfWeek>();
                    DateTime? repeatUntil = null;

                    if (ev.Props.TryGetValue("RRULE", out var rrule))
                    {
                        if (!TryParseRRule(rrule, start, out repeat, out weekdays, out repeatUntil))
                        {
                            unsupported++;
                            continue;
                        }
                    }

                    var skippedDates = new List<string>();
                    foreach (var (rawEx, exParams) in ev.ExDates)
                    {
                        foreach (var token in rawEx.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (TryIcsDateTime(token, exParams, out var exLocal, out _))
                            {
                                var key = exLocal.ToString("yyyy-MM-dd");
                                if (!skippedDates.Contains(key)) skippedDates.Add(key);
                            }
                        }
                    }

                    var masterSourceUid = repeat == "Once"
                        ? $"{uid}|ONCE"
                        : $"{uid}|SERIES|{start:yyyyMMddTHHmm}";

                    candidates.Add(new Meeting
                    {
                        SourceUid = masterSourceUid,
                        Title = title,
                        Start = start,
                        DurationMinutes = duration,
                        Repeat = repeat,
                        Weekdays = weekdays,
                        RepeatUntil = repeatUntil,
                        SkippedDates = skippedDates,
                        JoinUrl = zoomUrl,
                        MinutesBefore = 5
                    });
                }
            }

            foreach (var ex in pendingExceptions)
            {
                var origDateStr = ex.OriginalLocalStart.ToString("yyyy-MM-dd");
                var matchingSeries = candidates.Where(c =>
                    c.Repeat != "Once" &&
                    (c.SourceUid.StartsWith(ex.RawUid + "|", StringComparison.OrdinalIgnoreCase) ||
                     GetBaseSeriesUid(c.SourceUid).Equals(ex.BaseUid, StringComparison.OrdinalIgnoreCase)));

                foreach (var series in matchingSeries)
                {
                    if (!series.SkippedDates.Contains(origDateStr))
                        series.SkippedDates.Add(origDateStr);
                }

                foreach (var existingSeries in meetings.Where(m =>
                    m.Repeat != "Once" &&
                    (m.SourceUid.StartsWith(ex.RawUid + "|", StringComparison.OrdinalIgnoreCase) ||
                     GetBaseSeriesUid(m.SourceUid).Equals(ex.BaseUid, StringComparison.OrdinalIgnoreCase))))
                {
                    if (!existingSeries.SkippedDates.Contains(origDateStr))
                        existingSeries.SkippedDates.Add(origDateStr);
                }

                if (ex.ModifiedMeeting != null)
                {
                    candidates.Add(ex.ModifiedMeeting);
                }
            }

            var unique = candidates
                .GroupBy(m => m.SourceUid, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var additions = unique.Count(m => !meetings.Any(x => IsSameMeeting(x, m)));
            var updates = unique.Count - additions;

            var message =
                $"Zoom meetings ({settings.WorkdayStart}–{settings.WorkdayEnd}): {unique.Count}\n" +
                $"  • New: {additions}\n" +
                $"  • Existing to update: {updates}\n\n" +
                $"Skipped breakdown:\n" +
                $"  • Outside working hours ({settings.WorkdayStart}–{settings.WorkdayEnd}): {outsideHours}\n" +
                $"  • Non-Zoom events ignored: {nonZoom}\n" +
                $"  • Cancelled occurrences: {cancelledSkipped}\n" +
                $"  • Unsupported / complex rules: {unsupported}\n\n" +
                $"All recurring series and exceptions within {settings.WorkdayStart}–{settings.WorkdayEnd} are preserved regardless of creation date.\n\nApply import?";

            if (MessageBox.Show(message, "ICS import preview", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var meeting in unique)
            {
                var matches = meetings.Where(m => IsSameMeeting(m, meeting)).ToList();
                if (matches.Count == 0)
                {
                    meetings.Add(meeting);
                }
                else
                {
                    var primary = matches[0];
                    for (int i = 1; i < matches.Count; i++)
                        meetings.Remove(matches[i]);

                    primary.SourceUid = meeting.SourceUid;
                    primary.Title = meeting.Title;
                    primary.Start = meeting.Start;
                    primary.DurationMinutes = meeting.DurationMinutes;
                    primary.Repeat = meeting.Repeat;
                    primary.Weekdays = meeting.Weekdays;
                    primary.RepeatUntil = meeting.RepeatUntil;
                    primary.JoinUrl = meeting.JoinUrl;
                    foreach (var skip in meeting.SkippedDates)
                        if (!primary.SkippedDates.Contains(skip))
                            primary.SkippedDates.Add(skip);
                }
            }

            Save();
            RefreshUpcoming();
            RefreshStatus();
            MessageBox.Show($"Import complete. {additions} added, {updates} updated. {unsupported} unsupported entries skipped.", "Meeting Buddy");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import calendar: {ex.Message}", "Import failed");
        }
    }

    private void QuickPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            var match = Regex.Match(text, @"https?://[\w.-]*zoom\.us/(?:j|my|w)/[^\s<>]+", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                MessageBox.Show("Copy a Zoom invitation containing a meeting URL, then try again.");
                return;
            }
            CancelEdit_Click(sender, e);
            JoinBox.Text = match.Value.TrimEnd('.', ',', ';');
            var subject = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x =>
                x.Length is > 3 and < 100 &&
                !x.Contains("http") &&
                !x.Contains("Zoom Meeting", StringComparison.OrdinalIgnoreCase));
            TitleBox.Text = subject ?? "Zoom meeting";
            MeetingDate.SelectedDate = DateTime.Today;
            OpenMeetingDialog();
            MessageBox.Show("Zoom link found. Check the title, date and time before saving.", "Invitation preview");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Clipboard unavailable: {ex.Message}");
        }
    }

    private void BackupMeetings()
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(dataPath)!, "Backups");
            Directory.CreateDirectory(dir);
            var name = Path.Combine(dir, "meetings-" + DateTime.Now.ToString("yyyyMMdd") + ".json");
            if (!File.Exists(name) && File.Exists(dataPath)) File.Copy(dataPath, name);
            foreach (var old in Directory.GetFiles(dir, "meetings-*.json").OrderByDescending(x => x).Skip(14))
                File.Delete(old);
        }
        catch { /* A backup failure must not prevent saving the user's meeting. */ }
    }
}