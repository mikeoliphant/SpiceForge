using System.Text.Json.Nodes;
using AudioIOSharp;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>
/// AudioIOSharp's types in SpiceForge's API (WebSocketApi.md, Audio) and settings file: JSON both ways, the
/// <c>audio.state</c> event, and the library's errors as API errors.
/// </summary>
public static class AudioJson
{
    public static JsonObject ToJson(this AudioConfig config)
    {
        var json = new JsonObject { ["driver"] = config.Driver, ["device"] = config.Device };
        json["inputs"] = config.Inputs == null ? null : Json.Strings(config.Inputs);
        json["outputs"] = config.Outputs == null ? null : Json.Strings(config.Outputs);
        json["sampleRate"] = config.SampleRate;
        json["bufferSize"] = config.BufferSize;
        return json;
    }

    /// <summary>A configuration from its JSON (settings, or a request); missing fields are the device's defaults.</summary>
    public static AudioConfig Config(JsonObject? json)
    {
        if (json == null)
            return new AudioConfig();
        var args = new Args(json);
        return new AudioConfig
        {
            Driver = args.OptionalString("driver") ?? ClockDriver.Name,
            Device = args.OptionalString("device"),
            Inputs = args.Has("inputs") ? args.Strings("inputs") : null,
            Outputs = args.Has("outputs") ? args.Strings("outputs") : null,
            SampleRate = args.Has("sampleRate") ? args.Int("sampleRate", 0) : null,
            BufferSize = args.Has("bufferSize") ? args.Int("bufferSize", 0) : null,
        };
    }

    public static JsonObject ToJson(this AudioDeviceInfo info) => new()
    {
        ["name"] = info.Name,
        ["inputs"] = Json.Strings(info.Inputs),
        ["outputs"] = Json.Strings(info.Outputs),
        ["sampleRates"] = Json.Array(info.SampleRates.Select(r => (JsonNode?)r)),
        ["bufferSizes"] = Json.Array(info.BufferSizes.Select(b => (JsonNode?)b)),
        ["defaultSampleRate"] = info.DefaultSampleRate,
        ["defaultBufferSize"] = info.DefaultBufferSize,
        ["controlPanel"] = info.ControlPanel,
    };

    /// <summary><c>audio.state</c>.</summary>
    public static JsonObject ToJson(this AudioState state)
    {
        var json = new JsonObject { ["open"] = state.Open, ["driver"] = state.Driver, ["device"] = state.Device };
        if (state.Open && state.Format != null)
        {
            json["sampleRate"] = state.Format.SampleRate;
            json["bufferSize"] = state.BufferSize;
            json["inputs"] = Json.Strings(state.Format.Inputs);
            json["outputs"] = Json.Strings(state.Format.Outputs);
            json["inputLatency"] = state.InputLatency;
            json["outputLatency"] = state.OutputLatency;
            if (state.Connections != null)
                json["connections"] = Json.Array(state.Connections.Select(c => (JsonNode?)new JsonObject
                {
                    ["port"] = c.Port,
                    ["channel"] = c.Channel,
                    ["connected"] = Json.Strings(c.Connected),
                }));
        }
        // Whether a simulation or the test is running on it (a resident stream stays open without).
        json["running"] = state.Running;
        // Running on the clock although a device was configured.
        json["fallback"] = state.Fallback;
        if (state.Error != null)
            json["error"] = state.Error;
        return json;
    }

    /// <summary>The API error an audio error is. Its message says what the device does offer, for a value it doesn't.</summary>
    public static ApiException ToApiException(this AudioException ex) => new(
        ex.Kind switch
        {
            AudioErrorKind.InvalidValue => ErrorCodes.InvalidValue,
            AudioErrorKind.NotFound => ErrorCodes.NotFound,
            AudioErrorKind.Busy => ErrorCodes.AudioBusy,
            AudioErrorKind.Unavailable => ErrorCodes.AudioUnavailable,
            _ => ErrorCodes.Unsupported,
        },
        ex.Message);
}
