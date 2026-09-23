namespace NetPI.Abstractions;

/// <summary>
/// A panel-initiated request for the Web surface to prefill a composer with
/// text (the Ideas panel's "insert into chat"). The plugin publishes it on the
/// host event bus; the Web surface forwards it to the clients as a
/// <c>chat.prefill</c> event and the shell appends it to the target session's
/// draft (the currently visible session when <see cref="SessionId"/> is null),
/// focusing the composer. A cross-origin panel cannot reach the shell's
/// composer directly — this event is the only sanctioned path (the
/// shell↔panel bridge stays navigation-only).
/// </summary>
public sealed record ChatPrefillEvent(string Text, string? SessionId = null);
