using System.Text.Json.Nodes;

namespace SpiceForgeServer.Protocol;

/// <summary>The error codes of WebSocketApi.md, Errors.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnknownOp = "unknown_op";
    public const string NotFound = "not_found";
    public const string InvalidValue = "invalid_value";
    public const string IoError = "io_error";
    public const string Exists = "exists";
    public const string ModifiedOnDisk = "modified_on_disk";
    public const string BadDocument = "bad_document";
    public const string BuildFailed = "build_failed";
    public const string Unsupported = "unsupported";
    public const string AudioBusy = "audio_busy";
    public const string AudioUnavailable = "audio_unavailable";
    public const string Diverged = "diverged";
    public const string Internal = "internal";
}

/// <summary>A request failure that goes back to the client as an error response, with its code.</summary>
public sealed class ApiException : Exception
{
    public string Code { get; }
    public JsonNode? Details { get; }

    public ApiException(string code, string message, JsonNode? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }
}
