using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Http;

/// <summary>
/// Strict JSON parsing for request bodies.
/// </summary>
/// <remarks>
/// Comments, duplicate properties, trailing content, excessive nesting and
/// non finite numbers are all rejected before a request reaches the game.
/// </remarks>
public static class StrictJson
{
    public const int MaxDepth = 16;

    // JObject discards comments between properties even with CommentHandling.Load.
    // Reject them at the reader boundary so nested/property comments cannot slip through.
    private sealed class RequestJsonReader : JsonTextReader
    {
        public RequestJsonReader(TextReader reader)
            : base(reader)
        {
        }

        public override bool Read()
        {
            var read = base.Read();
            if (TokenType == JsonToken.Comment)
            {
                throw new JsonReaderException("Comments are not allowed.");
            }

            return read;
        }
    }

    /// <summary>
    /// Parses a request body into a single JSON object.
    /// </summary>
    public static JObject ParseObject(string body)
    {
        try
        {
            using var sr = new StringReader(body);
            using var reader = new RequestJsonReader(sr)
            {
                MaxDepth = MaxDepth,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
            };

            var token = JToken.ReadFrom(reader, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Load,
            });

            if (!(token is JObject result) || reader.Read() || result.DescendantsAndSelf().Any(t => t.Type == JTokenType.Comment))
            {
                throw new JsonReaderException();
            }

            return result;
        }
        catch (JsonException)
        {
            throw new ApiException(
                400,
                "INVALID_JSON",
                "Expected one JSON object without duplicate keys or comments (maximum depth " + MaxDepth + ").");
        }
    }

    /// <summary>
    /// Returns true when the token tree contains a value that is not a finite number.
    /// </summary>
    public static bool ContainsNonFiniteNumber(JToken token) => Walk(token);

    private static bool Walk(JToken token)
    {
        if (token.Type == JTokenType.Float)
        {
            var value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return true;
            }
        }

        if (token is JContainer container)
        {
            foreach (var child in container.Children())
            {
                if (Walk(child))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
