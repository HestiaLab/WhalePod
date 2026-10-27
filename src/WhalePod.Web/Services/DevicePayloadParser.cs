using System.Text.Json;
using System.Text.Json.Serialization;

using WhalePod.Contracts.Devices;

namespace WhalePod.Web.Services;

public static class DevicePayloadParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryParse(string? terminalOutput, out DeviceCompleteRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(terminalOutput))
        {
            return false;
        }

        var start = terminalOutput.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        var end = -1;
        for (var index = start; index < terminalOutput.Length; index++)
        {
            var character = terminalOutput[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        end = index;
                    }

                    break;
            }

            if (end >= 0)
            {
                break;
            }
        }

        if (end < 0)
        {
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<DeviceCompleteRequest>(
                terminalOutput[start..(end + 1)],
                Options);
            return request is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}