using System.Text.Json;

namespace NetPI.Activity;

/// <summary>
/// astra-2 §12.3: validation of the panel→shell navigation bridge envelopes.
///
/// The Work panel (a cross-origin iframe on this plugin's Kestrel port) posts a
/// versioned envelope to the shell when the user activates an agent row:
///
///   { "type": "netpi.panel.openSession", "version": 1, "payload": { "sessionId": "…" } }
///
/// The shell (web/netpi-web) accepts it ONLY when the message's source is the
/// currently mounted panel iframe's contentWindow AND the origin equals that
/// registered panel's entry-URL origin — an origin/type string alone is never
/// enough (App.svelte + RightPanel.svelte implement the frame checks; this
/// class owns the envelope SHAPE so the shell and the server agree on one
/// canonical parse). The shell keeps the legacy Activity envelope
/// (<c>netpi.activity.openSession</c>, no version) temporarily with the same
/// checks; it is deprecated (docs/web-panels.md).
///
/// Row activation is navigation ONLY: it selects an existing chat tab or opens
/// the session by id. It never spawns, resumes, cancels, or changes lanes.
/// </summary>
public static class ActivityBridge
{
    /// <summary>The current generic panel navigation envelope type.</summary>
    public const string OpenSessionType = "netpi.panel.openSession";

    /// <summary>
    /// The legacy Activity envelope type — accepted temporarily (deprecated;
    /// removed in a later round once no old Activity page build is in use).
    /// </summary>
    public const string LegacyOpenSessionType = "netpi.activity.openSession";

    /// <summary>Envelope versions the shell and the page speak today.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Session ids are bounded; longer strings are a shape violation.</summary>
    public const int MaxSessionIdLength = 128;

    /// <summary>
    /// Parse and validate one navigation envelope. Returns the bounded
    /// <c>sessionId</c> when the shape is acceptable, or null when the envelope
    /// must be rejected. This checks SHAPE only — source/origin/frame checks
    /// belong to the shell (they need the DOM); the server-side consumer (if
    /// any) must apply them too, never skip them.
    /// </summary>
    public static string? Validate(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return null;

        var type = GetString(message, "type");
        if (type is null)
            return null;

        if (type == LegacyOpenSessionType)
        {
            // Legacy envelope: same shape minus the version field (deprecation
            // documented in web-panels.md).
            return GetSessionId(message);
        }

        if (type != OpenSessionType)
            return null;

        if (!TryGetInt(message, "version", out var version) || version != CurrentVersion)
            return null;

        return GetSessionId(message);
    }

    /// <summary>True when <paramref name="sessionId"/> is a bounded, well-formed session id.</summary>
    public static bool IsValidSessionId(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaxSessionIdLength)
            return false;
        foreach (var c in sessionId)
            if (char.IsWhiteSpace(c) || c < 0x20 || c == 0x7F)
                return false;
        return true;
    }

    private static string? GetSessionId(JsonElement message)
    {
        if (!message.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        var sid = GetString(payload, "sessionId");
        return sid is not null && IsValidSessionId(sid) ? sid : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return v.GetString();
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return false;
        if (!v.TryGetInt32(out value)) return false;
        return true;
    }
}
