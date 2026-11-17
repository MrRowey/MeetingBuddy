using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using MessageBox = System.Windows.MessageBox;

namespace MeetingBuddy;

public partial class MainWindow
{
    private HttpListener? sdListener;
    private CancellationTokenSource? sdCts;
    private int sdPageIndex;

    private void InitializeStreamDeckBridge()
    {
        StopStreamDeckBridge();
        if (!settings.StreamDeckEnabled)
        {
            UpdateStreamDeckStatusUi("Disabled in preferences");
            return;
        }

        int port = settings.StreamDeckPort is >= 1024 and <= 65535 ? settings.StreamDeckPort : 48291;
        try
        {
            sdCts = new CancellationTokenSource();
            sdListener = new HttpListener();
            sdListener.Prefixes.Add($"http://127.0.0.1:{port}/");
            sdListener.Start();
            UpdateStreamDeckStatusUi($"Listening on 127.0.0.1:{port}");
            _ = Task.Run(() => RunStreamDeckLoopAsync(sdListener, sdCts.Token));
        }
        catch (Exception ex)
        {
            UpdateStreamDeckStatusUi($"Bridge error: {ex.Message}");
        }
    }

    private void StopStreamDeckBridge()
    {
        try { sdCts?.Cancel(); } catch { }
        try { sdListener?.Stop(); sdListener?.Close(); } catch { }
        sdListener = null;
        sdCts = null;
    }

    private void UpdateStreamDeckStatusUi(string text)
    {
        if (StreamDeckStatusText != null)
            Dispatcher.BeginInvoke(new Action(() => StreamDeckStatusText.Text = text));
    }

    private async Task RunStreamDeckLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleStreamDeckRequestAsync(ctx), token);
        }
    }

    private async Task HandleStreamDeckRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        try
        {
            var path = req.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "";
            object payload = path switch
            {
                "/api/status" => await Dispatcher.InvokeAsync(() => BuildStatusPayload(req)),
                "/api/join-next" => await Dispatcher.InvokeAsync(JoinNextFromBridge),
                "/api/join" => await Dispatcher.InvokeAsync(() => JoinByIdFromBridge(req.QueryString["id"])),
                "/api/quick-meeting" => await Dispatcher.InvokeAsync(() => CreateQuickMeetingFromBridge(req.QueryString["title"], req.QueryString["minutes"])),
                "/api/toggle-pause" => await Dispatcher.InvokeAsync(() =>
                {
                    paused = !paused;
                    RefreshStatus();
                    return (object)new { ok = true, paused };
                }),
                "/api/ack-missed" => await Dispatcher.InvokeAsync(() =>
                {
                    missedKeys.Clear();
                    missedEntries.Clear();
                    RefreshStatus();
                    return (object)new { ok = true, missed = 0 };
                }),
                "/api/page" => await Dispatcher.InvokeAsync(() =>
                {
                    var dir = req.QueryString["dir"] ?? "next";
                    sdPageIndex = dir == "prev" ? Math.Max(0, sdPageIndex - 1) : sdPageIndex + 1;
                    return (object)new { ok = true, page = sdPageIndex };
                }),
                _ => new { ok = false, error = "Unknown endpoint" }
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.StatusCode = 200;
            await res.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            var err = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            res.ContentType = "application/json; charset=utf-8";
            res.StatusCode = 500;
            await res.OutputStream.WriteAsync(err);
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private object BuildStatusPayload(HttpListenerRequest req)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var todaysAll = meetings
            .Where(m => OccursOn(m, today))
            .OrderBy(m => m.Start.TimeOfDay)
            .Select(m =>
            {
                var start = today + m.Start.TimeOfDay;
                var end = start.AddMinutes(m.DurationMinutes > 0 ? m.DurationMinutes : 30);
                return new
                {
                    id = m.Id,
                    title = m.Title,
                    start = start.ToString("HH:mm"),
                    end = end.ToString("HH:mm"),
                    hasZoom = m.HasJoinUrl,
                    isLive = now >= start && now <= end,
                    isPast = now > end,
                    color = m.EventColor
                };
            })
            .ToList();

        int pageSize = int.TryParse(req.QueryString["pageSize"], out var ps) && ps > 0 ? Math.Clamp(ps, 1, 32) : 16;
        int maxPage = Math.Max(0, (todaysAll.Count - 1) / pageSize);
        if (sdPageIndex > maxPage) sdPageIndex = maxPage;

        var pageItems = todaysAll.Skip(sdPageIndex * pageSize).Take(pageSize).ToList();

        var next = meetings
            .Select(m => (Meeting: m, When: NextOccurrence(m, now)))
            .Where(x => x.When.HasValue)
            .OrderBy(x => x.When!.Value)
            .FirstOrDefault();

        string countdownText = "None";
        int minutesRemaining = -1;
        bool isLiveNow = false;
        if (next.Meeting != null && next.When.HasValue)
        {
            if (next.When.Value <= now)
            {
                isLiveNow = true;
                minutesRemaining = 0;
                countdownText = "LIVE NOW";
            }
            else
            {
                var diff = next.When.Value - now;
                minutesRemaining = (int)Math.Ceiling(diff.TotalMinutes);
                countdownText = diff.TotalHours >= 24
                    ? $"{next.When.Value:ddd HH:mm}"
                    : diff.TotalHours >= 1
                        ? $"{(int)diff.TotalHours}h {diff.Minutes}m"
                        : $"{minutesRemaining}m";
            }
        }

        return new
        {
            ok = true,
            app = "MeetingBuddy",
            version = "3.3",
            paused,
            missedCount = missedEntries.Count,
            todayCount = todaysAll.Count,
            page = sdPageIndex,
            totalPages = maxPage + 1,
            todaysMeetings = pageItems,
            nextMeeting = next.Meeting == null || !next.When.HasValue ? null : new
            {
                id = next.Meeting.Id,
                title = next.Meeting.Title,
                start = next.When.Value.ToString("HH:mm"),
                countdown = countdownText,
                minutesRemaining,
                isLive = isLiveNow,
                hasZoom = next.Meeting.HasJoinUrl
            }
        };
    }

    private object JoinNextFromBridge()
    {
        var now = DateTime.Now;
        var next = meetings
            .Where(m => m.HasJoinUrl)
            .Select(m => (Meeting: m, When: NextOccurrence(m, now)))
            .Where(x => x.When.HasValue)
            .OrderBy(x => x.When!.Value)
            .FirstOrDefault();

        if (next.Meeting == null)
            return new { ok = false, message = "No upcoming Zoom meeting found." };

        LaunchJoinUrl(next.Meeting.JoinUrl, next.Meeting.Id);
        return new { ok = true, title = next.Meeting.Title };
    }

    private object JoinByIdFromBridge(string? idStr)
    {
        if (!Guid.TryParse(idStr, out var id))
            return new { ok = false, message = "Invalid meeting ID." };

        var meeting = meetings.FirstOrDefault(m => m.Id == id);
        if (meeting == null || !meeting.HasJoinUrl)
            return new { ok = false, message = "Meeting or Zoom link not found." };

        LaunchJoinUrl(meeting.JoinUrl, meeting.Id);
        return new { ok = true, title = meeting.Title };
    }

    private object CreateQuickMeetingFromBridge(string? rawTitle, string? rawMinutes)
    {
        var now = DateTime.Now;
        int duration = int.TryParse(rawMinutes, out var d) && d is > 0 and <= 480 ? d : settings.DefaultDuration;
        var title = string.IsNullOrWhiteSpace(rawTitle) ? $"Quick Meeting ({now:HH:mm})" : rawTitle.Trim();

        var item = new Meeting
        {
            Title = title,
            Start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0),
            DurationMinutes = duration,
            Repeat = "Once",
            MinutesBefore = 0,
            EventColor = "#059669"
        };
        meetings.Add(item);
        Save();
        RefreshUpcoming();
        RefreshStatus();
        return new { ok = true, id = item.Id, title = item.Title };
    }

    private void TestStreamDeckBridge_Click(object sender, RoutedEventArgs e)
    {
        int port = settings.StreamDeckPort is >= 1024 and <= 65535 ? settings.StreamDeckPort : 48291;
        MessageBox.Show(
            $"Stream Deck XL Bridge Status: {StreamDeckStatusText?.Text ?? "Unknown"}\n\n" +
            $"Local Endpoint: http://127.0.0.1:{port}/api/status\n" +
            $"Plugin Folder: Included in StreamDeckPlugin\\com.meetingbuddy.streamdeck.sdPlugin\n\n" +
            $"Copy the .sdPlugin folder to:\n%AppData%\\Elgato\\StreamDeck\\Plugins\\",
            "Stream Deck XL Integration");
    }
}