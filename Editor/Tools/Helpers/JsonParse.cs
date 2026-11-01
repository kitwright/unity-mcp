// Copyright (C) KitWright. All rights reserved.

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KitWright.Editor.Tools.Helpers
{
    // JObject.Parse/JArray.Parse turn ISO-8601-looking strings into dates, and ToString then re-renders them
    // in another format, so a text argument no longer reaches the tool as the client wrote it.
    internal static class JsonParse
    {
        public static JObject Object(string json) => Read(json, JObject.Load);

        public static JArray Array(string json) => Read(json, JArray.Load);

        public static JToken Token(string json) => Read(json, JToken.ReadFrom);

        private static T Read<T>(string json, Func<JsonReader, T> load)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                var result = load(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException(
                            $"Unexpected content after the JSON value. Path '{reader.Path}', line {reader.LineNumber}, position {reader.LinePosition}.");
                }
                return result;
            }
        }
    }
}
