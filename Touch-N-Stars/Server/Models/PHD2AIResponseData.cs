using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TouchNStars.Server.Models;

// EmbedIO/Swan treats JToken as IEnumerable and otherwise emits nested arrays.
// Convert at the HTTP boundary while preserving numbers, booleans and nulls.
public static class PHD2AIResponseData
{
    public static object ToPlain(JToken token) => token switch
    {
        null => null,
        JObject obj => obj.Properties().ToDictionary(p => p.Name, p => ToPlain(p.Value)),
        JArray array => array.Select(ToPlain).ToArray(),
        JValue value => value.Value,
        _ => throw new ArgumentException("Unsupported AI response value"),
    };
}
