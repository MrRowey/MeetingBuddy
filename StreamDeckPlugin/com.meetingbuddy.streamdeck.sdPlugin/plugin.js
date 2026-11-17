let websocket = null;
let pluginUUID = null;
const contexts = new Map();
const API_BASE = "http://127.0.0.1:48291";

function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent, inInfo) {
    pluginUUID = inPluginUUID;
    websocket = new WebSocket("ws://127.0.0.1:" + inPort);
    websocket.onopen = () => {
        websocket.send(JSON.stringify({ event: inRegisterEvent, uuid: inPluginUUID }));
        setInterval(pollMeetingBuddy, 2000);
    };
    websocket.onmessage = (evt) => {
        const msg = JSON.parse(evt.data);
        if (msg.event === "willAppear") {
            contexts.set(msg.context, { action: msg.action, settings: msg.payload.settings || {} });
            pollMeetingBuddy();
        } else if (msg.event === "willDisappear") {
            contexts.delete(msg.context);
        } else if (msg.event === "didReceiveSettings") {
            const existing = contexts.get(msg.context);
            if (existing) existing.settings = msg.payload.settings || {};
        } else if (msg.event === "keyDown") {
            handleKeyDown(msg.action, msg.context, msg.payload.settings || {});
        }
    };
}

async function handleKeyDown(action, context, settings) {
    try {
        if (action === "com.meetingbuddy.streamdeck.countdown" || action === "com.meetingbuddy.streamdeck.joinnext") {
            await fetch(API_BASE + "/api/join-next", { method: "POST" });
        } else if (action === "com.meetingbuddy.streamdeck.todayslot") {
            if (settings.meetingId) {
                await fetch(API_BASE + "/api/join?id=" + encodeURIComponent(settings.meetingId), { method: "POST" });
            }
        } else if (action === "com.meetingbuddy.streamdeck.quickmeeting") {
            await fetch(API_BASE + "/api/quick-meeting", { method: "POST" });
        } else if (action === "com.meetingbuddy.streamdeck.togglepause") {
            await fetch(API_BASE + "/api/toggle-pause", { method: "POST" });
        } else if (action === "com.meetingbuddy.streamdeck.missed") {
            await fetch(API_BASE + "/api/ack-missed", { method: "POST" });
        }
        setTimeout(pollMeetingBuddy, 300);
    } catch (e) {
        console.error(e);
    }
}

function makeKeySvg(bg, line1, line2, line3) {
    const esc = (s) => String(s || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144">
      <rect width="144" height="144" rx="18" fill="${bg}"/>
      <text x="72" y="42" font-family="Segoe UI, sans-serif" font-size="16" font-weight="bold" fill="#93C5FD" text-anchor="middle">${esc(line1)}</text>
      <text x="72" y="80" font-family="Segoe UI, sans-serif" font-size="22" font-weight="bold" fill="#FFFFFF" text-anchor="middle">${esc(line2)}</text>
      <text x="72" y="114" font-family="Segoe UI, sans-serif" font-size="14" fill="#E2E8F0" text-anchor="middle">${esc(line3)}</text>
    </svg>`;
    return "data:image/svg+xml;charset=utf8," + encodeURIComponent(svg);
}

function setKeyImage(context, dataUrl) {
    if (!websocket || websocket.readyState !== 1) return;
    websocket.send(JSON.stringify({ event: "setImage", context, payload: { image: dataUrl, target: 0 } }));
}

async function pollMeetingBuddy() {
    if (contexts.size === 0) return;
    try {
        const res = await fetch(API_BASE + "/api/status?pageSize=24");
        const data = await res.json();
        let slotIndex = 0;

        for (const [ctx, info] of contexts.entries()) {
            if (info.action === "com.meetingbuddy.streamdeck.countdown") {
                const nm = data.nextMeeting;
                const bg = nm && nm.isLive ? "#DC2626" : "#1E3A8A";
                const title = nm ? nm.title.slice(0, 14) : "No Meetings";
                const cd = nm ? nm.countdown : "Clear";
                const sub = nm ? nm.start : "All done";
                setKeyImage(ctx, makeKeySvg(bg, title, cd, sub));
            } else if (info.action === "com.meetingbuddy.streamdeck.joinnext") {
                const nm = data.nextMeeting;
                const bg = nm && nm.hasZoom ? "#2563EB" : "#334155";
                setKeyImage(ctx, makeKeySvg(bg, "JOIN ZOOM", nm ? nm.start : "--:--", nm ? nm.title.slice(0, 14) : "No link"));
            } else if (info.action === "com.meetingbuddy.streamdeck.todayslot") {
                const m = (data.todaysMeetings || [])[slotIndex++];
                if (m) {
                    info.settings.meetingId = m.id;
                    const bg = m.isLive ? "#DC2626" : (m.isPast ? "#1E293B" : "#0F172A");
                    setKeyImage(ctx, makeKeySvg(bg, m.start + "-" + m.end, m.title.slice(0, 12), m.hasZoom ? "Zoom Ready" : "No Link"));
                } else {
                    info.settings.meetingId = null;
                    setKeyImage(ctx, makeKeySvg("#0F172A", "SLOT", "Empty", ""));
                }
            } else if (info.action === "com.meetingbuddy.streamdeck.quickmeeting") {
                setKeyImage(ctx, makeKeySvg("#059669", "QUICK", "+ Meeting", "Start Now"));
            } else if (info.action === "com.meetingbuddy.streamdeck.togglepause") {
                const bg = data.paused ? "#B45309" : "#15803D";
                setKeyImage(ctx, makeKeySvg(bg, "ALERTS", data.paused ? "PAUSED" : "ACTIVE", "Tap to toggle"));
            } else if (info.action === "com.meetingbuddy.streamdeck.missed") {
                const bg = data.missedCount > 0 ? "#B91C1C" : "#1E293B";
                setKeyImage(ctx, makeKeySvg(bg, "MISSED", String(data.missedCount), data.missedCount > 0 ? "Tap to clear" : "All clear"));
            }
        }
    } catch {
        for (const [ctx] of contexts.entries()) {
            setKeyImage(ctx, makeKeySvg("#334155", "MEETING BUDDY", "Offline", "Start App"));
        }
    }
}
