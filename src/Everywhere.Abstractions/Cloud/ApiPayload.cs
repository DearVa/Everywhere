using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.Cloud;

/// <summary>
/// Standard structure for API error details.
/// </summary>
public class ApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("upstream")]
    public UpstreamError? Upstream { get; set; }

    public class UpstreamError
    {
        [JsonPropertyName("status")]
        public HttpStatusCode StatusCode { get; set; }

        [JsonPropertyName("body")]
        public JsonDocument? Body { get; set; }
    }
}

/// <summary>
/// Standard HTTP payload structure for API responses.
/// </summary>
public class ApiPayload
{
    /// <summary>
    /// Status code indicating success or error.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Details about the response error.
    /// </summary>
    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }

    /// <summary>
    /// Ensures that the response indicates success.
    /// </summary>
    /// <exception cref="HttpRequestException"></exception>
    public void EnsureSuccess()
    {
        if (!Success) throw new HttpRequestException(ToString());
    }

    public override string ToString() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Generic HTTP payload structure for API responses containing data of type T.
/// </summary>
/// <typeparam name="T"></typeparam>
public class ApiPayload<T> : ApiPayload
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    public ApiPayload() { }

    public ApiPayload(T data)
    {
        Data = data;
    }

    /// <summary>
    /// Ensures that the status code indicates success and that Data is not null.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    public T EnsureData()
    {
        EnsureSuccess();
        return Data ?? throw new HttpRequestException($"{nameof(Data)} is null");
    }
}