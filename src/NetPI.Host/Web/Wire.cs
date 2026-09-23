using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Web;

/// <summary>JSON envelopes of the UI protocol and RPC error mapping (see docs/PROTOCOL.md).</summary>
internal static class Wire
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Hello(string clientId, string version) => Build(w =>
    {
        w.WriteString("t", "hello");
        w.WriteString("clientId", clientId);
        w.WriteString("version", version);
    });

    public static readonly byte[] Pong = Build(w => w.WriteString("t", "pong"));

    public static byte[] Result(JsonElement id, object? result) => Build(w =>
    {
        w.WriteString("t", "res");
        WriteId(w, id);
        w.WritePropertyName("r");
        WriteValue(w, result);
    });

    public static byte[] Error(JsonElement id, string code, string message) => Build(w =>
    {
        w.WriteString("t", "res");
        WriteId(w, id);
        w.WriteStartObject("e");
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    /// <summary>Event envelope; null when the payload cannot be serialized (logged).</summary>
    public static byte[]? Event(BusEvent e, ILogger log)
    {
        try
        {
            return Build(w =>
            {
                w.WriteString("t", "ev");
                w.WriteString("type", e.Type);
                if (e.SessionId is null) w.WriteNull("sid");
                else w.WriteString("sid", e.SessionId);
                w.WritePropertyName("d");
                if (e.Data is null)
                {
                    w.WriteStartObject();
                    w.WriteEndObject();
                }
                else WriteValue(w, e.Data);
                w.WriteNumber("seq", e.Seq);
                w.WriteNumber("ts", e.Time.ToUnixTimeMilliseconds());
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Cannot serialize event '{Type}' from {Source}", e.Type, e.Source ?? "?");
            return null;
        }
    }

    /// <summary>Serialize any value with the right options (plugin types use the collectible cache).</summary>
    public static void WriteValue(Utf8JsonWriter w, object? value)
    {
        if (value is null) w.WriteNullValue();
        else JsonSerializer.Serialize(w, value, value.GetType(), NetPiJson.For(value));
    }

    public static byte[] SerializeValue(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions)) WriteValue(w, value);
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] ErrorBody(string code, string message) => Build(w =>
    {
        w.WriteStartObject("error");
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        w.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined) w.WriteNullValue();
        else id.WriteTo(w);
    }

    private static byte[] Build(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Map a handler exception to a protocol error (code, message, HTTP status).</summary>
    public static (string Code, string Message, int Status) MapError(Exception exception, ILogger log, string method)
    {
        var ex = JsonUtil.Unwrap(exception);
        switch (ex)
        {
            case RpcException r:
                return (r.Code, r.Message, StatusFor(r.Code));
            case KeyNotFoundException:
                return ("not_found", ex.Message, 404);
            case ArgumentException:
                return ("bad_request", ex.Message, 400);
            case JsonException:
                return ("bad_request", "Invalid parameters: " + ex.Message, 400);
            case OperationCanceledException:
                return ("cancelled", "The request was cancelled", 409);
            default:
                log.LogError(ex, "RPC {Method} failed", method);
                return ("internal", ex.Message, 500);
        }
    }

    public static int StatusFor(string code) => code switch
    {
        "not_found" => 404,
        "bad_request" => 400,
        "unauthorized" => 401,
        "forbidden" => 403,
        "conflict" or "cancelled" or "busy" => 409,
        "internal" => 500,
        _ => 400,
    };
}
