using System.Web;
using MessagePack;

namespace Everywhere.Cloud;

/// <summary>
/// Standard HTTP payload structure for API responses.
/// </summary>
public class HttpPayload
{
    /// <summary>
    /// Status code indicating success or type of error.
    /// 1 indicates success; other values indicate various errors.
    /// </summary>
    [Key("code")]
    public int Code { get; set; } = 1;

    /// <summary>
    /// Message describing the status or error.
    /// </summary>
    [Key("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Timestamp in UTC ticks when the payload is created.
    /// </summary>
    [Key("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>
    /// Ensures that the status code indicates success.
    /// Throws an HttpRequestException if the code is not 1.
    /// </summary>
    /// <exception cref="HttpRequestException"></exception>
    public void EnsureSuccessStatusCode()
    {
        if (Code != 1) throw new HttpRequestException($"({Code}) {Message}");
    }

    public override string ToString() =>
        $$"""
          {
            {{nameof(Code)}}: {{Code}},
            {{nameof(Message)}}: "{{HttpUtility.JavaScriptStringEncode(Message)}}",
            {{nameof(Timestamp)}}: "{{Timestamp}}"
          }
          """;

    public static HttpPayload Success { get; } = new();

    public static HttpPayload FromErrorMessage(string error, int code = 0) => new()
    {
        Code    = code,
        Message = error,
    };

    public static HttpPayload FromException(Exception e, int code = 0) => FromErrorMessage(e.Message, code);
}

/// <summary>
/// Generic HTTP payload structure for API responses containing data of type T.
/// </summary>
/// <typeparam name="T"></typeparam>
public class HttpPayload<T> : HttpPayload
{
    [Key("data")]
    public T? Data { get; set; }

    public HttpPayload() { }

    public HttpPayload(T data)
    {
        Data = data;
    }

    public override string ToString() =>
        $$"""
          {
            {{nameof(Data)}}: {{HttpUtility.JavaScriptStringEncode(Data?.ToString())}},
            {{nameof(Code)}}: {{Code}},
            {{nameof(Message)}}: "{{HttpUtility.JavaScriptStringEncode(Message)}}",
            {{nameof(Timestamp)}}: "{{Timestamp}}"
          }
          """;

    /// <summary>
    /// Ensures that the status code indicates success and that Data is not null.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    public T EnsureData()
    {
        EnsureSuccessStatusCode();
        return Data ?? throw new HttpRequestException(Message ?? $"{nameof(Data)} is null");
    }

    public new static HttpPayload<T> FromErrorMessage(string error, int code = 0) => new()
    {
        Code    = code,
        Message = error,
    };

    public new static HttpPayload<T> FromException(Exception e, int code = 0) => FromErrorMessage(e.Message, code);
}