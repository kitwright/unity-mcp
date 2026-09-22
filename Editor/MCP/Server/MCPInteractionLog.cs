// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    internal enum MCPToolCallStatus
    {
        Success,
        Interrupted,
        Error
    }

    internal struct MCPLogEntry
    {
        public DateTime Timestamp;
        public string ToolName;
        public MCPToolCallStatus Status;
        public string ResultSummary;
        public string ImageFilePath;
        public int EstimatedTokens;
    }

    internal class MCPInteractionLog
    {
        private const string ImageDataUriPrefix = "data:image/";
        private const string Base64Marker = ";base64,";

        private const string SessionStateKey = "KitWright.MCP.InteractionLog";
        private static readonly string ImageDir =
            Path.Combine("Library", "KitWrightMcp", "ActivityImages");

        private MCPLogEntry[] _buffer;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        public event Action<MCPLogEntry> OnEntryAdded;

        public MCPInteractionLog(int capacity = 200)
        {
            _buffer = new MCPLogEntry[Mathf.Clamp(capacity, 50, 1000)];
            LoadFromSession();
        }

        public void SetCapacity(int capacity)
        {
            capacity = Mathf.Clamp(capacity, 50, 1000);
            lock (_lock)
            {
                if (_buffer != null && capacity == _buffer.Length)
                    return;

                var existing = GetEntries();
                _buffer = new MCPLogEntry[capacity];
                _head = 0;
                _count = 0;
                for (int i = existing.Count - 1; i >= 0; i--)
                {
                    _buffer[_head] = existing[i];
                    _head = (_head + 1) % _buffer.Length;
                    if (_count < _buffer.Length) _count++;
                }
            }
        }

        [Serializable]
        private struct SerializedEntry
        {
            public long TimestampTicks;
            public string ToolName;
            public int Status;
            public string ResultSummary;
            public string ImageFilePath;
            public int EstimatedTokens;
        }

        [Serializable]
        private class SerializedLog
        {
            public List<SerializedEntry> Entries = new List<SerializedEntry>();
        }

        public void Add(string toolName, MCPToolCallStatus status, string resultSummary)
        {
            var isImage = Base64Of(resultSummary) != null;
            var imageFilePath = isImage
                ? SaveImageToDisk(resultSummary)
                : TryExtractScreenshotPath(resultSummary);

            var display = ExtractMessage(resultSummary);
            var entry = new MCPLogEntry
            {
                Timestamp = DateTime.Now,
                ToolName = toolName,
                Status = status,
                ResultSummary = isImage || imageFilePath != null
                    ? "Screenshot captured successfully."
                    : display.Length > 200
                    ? display.Substring(0, 197) + "..."
                    : display,
                ImageFilePath = imageFilePath,
                EstimatedTokens = isImage
                    ? EstimateImageTokens(resultSummary)
                    : ((resultSummary?.Length ?? 0) + 3) / 4
            };

            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }

            SaveToSession();
            OnEntryAdded?.Invoke(entry);
        }

        private static string ExtractMessage(string result)
        {
            if (string.IsNullOrEmpty(result))
                return "";
            var trimmed = result.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return result;
            try
            {
                var root = Newtonsoft.Json.Linq.JToken.Parse(result);
                var msg = root.SelectToken("message")?.ToString();
                if (string.IsNullOrEmpty(msg))
                {
                    var code = root.SelectToken("error")?.ToString();
                    var detail = root.SelectToken("data.message")?.ToString();
                    if (!string.IsNullOrEmpty(code))
                        msg = string.IsNullOrEmpty(detail) ? code : $"{code}: {detail}";
                }
                return string.IsNullOrEmpty(msg) ? result : msg;
            }
            catch
            {
                return result;
            }
        }

        // Vision cost ~= w*h/750, from PNG IHDR dims -- base64 length overcounts ~50x.
        private static int EstimateImageTokens(string dataUri)
        {
            try
            {
                var base64 = Base64Of(dataUri);
                if (base64 == null)
                    return 1000;

                var header = Convert.FromBase64String(base64.Substring(0, Math.Min(base64.Length, 64)));
                if (header.Length < 24) return 1000;
                int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                if (w <= 0 || h <= 0 || w > 20000 || h > 20000) return 1000;
                return Mathf.Clamp(Mathf.RoundToInt(w * (long)h / 750f), 1, 1600);
            }
            catch
            {
                return 1000;
            }
        }

        public List<MCPLogEntry> GetEntries()
        {
            lock (_lock)
            {
                var result = new List<MCPLogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                    result.Add(_buffer[idx]);
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
            }

            SessionState.EraseString(SessionStateKey);

            try
            {
                if (Directory.Exists(ImageDir))
                    Directory.Delete(ImageDir, true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright MCP] Failed to clear activity images: {ex.Message}");
            }
        }

        private static string TryExtractScreenshotPath(string result)
        {
            if (string.IsNullOrEmpty(result) || result.IndexOf(".png", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            try
            {
                // JSON parse (not regex) because Windows paths arrive backslash-escaped in the payload.
                var root = Newtonsoft.Json.Linq.JToken.Parse(result);
                var path = root.SelectToken("data.path")?.ToString() ?? root.SelectToken("path")?.ToString();
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    return null;
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        // Any base64 image, not only PNG. A screenshot taken across a wire arrives JPEG-encoded, and a
        // png-only prefix logged those as their own base64 payload — a megabyte of text per entry, with
        // no thumbnail and a token estimate off by a factor of fifty.
        private static string Base64Of(string dataUri)
        {
            if (dataUri == null || !dataUri.StartsWith(ImageDataUriPrefix, StringComparison.Ordinal))
                return null;

            var marker = dataUri.IndexOf(Base64Marker, StringComparison.Ordinal);
            return marker > 0 ? dataUri.Substring(marker + Base64Marker.Length) : null;
        }

        private static string ExtensionOf(string dataUri)
        {
            var marker = dataUri.IndexOf(Base64Marker, StringComparison.Ordinal);
            var format = dataUri.Substring(ImageDataUriPrefix.Length, marker - ImageDataUriPrefix.Length);

            if (format == "jpeg")
                return "jpg";

            // A subtype like "svg+xml" is not a filename; anything unexpected keeps the old extension.
            foreach (var character in format)
            {
                if (!char.IsLetterOrDigit(character))
                    return "png";
            }

            return format;
        }

        private static string SaveImageToDisk(string dataUri)
        {
            try
            {
                var bytes = Convert.FromBase64String(Base64Of(dataUri));
                Directory.CreateDirectory(ImageDir);
                var path = Path.Combine(ImageDir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{ExtensionOf(dataUri)}");
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright MCP] Failed to save activity image: {ex.Message}");
                return null;
            }
        }

        private void SaveToSession()
        {
            var log = new SerializedLog();
            lock (_lock)
            {
                for (int i = _count - 1; i >= 0; i--)
                {
                    int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                    var e = _buffer[idx];
                    log.Entries.Add(new SerializedEntry
                    {
                        TimestampTicks = e.Timestamp.Ticks,
                        ToolName = e.ToolName,
                        Status = (int)e.Status,
                        ResultSummary = e.ResultSummary,
                        ImageFilePath = e.ImageFilePath,
                        EstimatedTokens = e.EstimatedTokens
                    });
                }
            }

            SessionState.SetString(SessionStateKey, JsonUtility.ToJson(log));
        }

        private void LoadFromSession()
        {
            var json = SessionState.GetString(SessionStateKey, null);
            if (string.IsNullOrEmpty(json))
                return;

            SerializedLog log;
            try
            {
                log = JsonUtility.FromJson<SerializedLog>(json);
            }
            catch
            {
                return;
            }

            if (log?.Entries == null)
                return;

            lock (_lock)
            {
                foreach (var e in log.Entries)
                {
                    _buffer[_head] = new MCPLogEntry
                    {
                        Timestamp = new DateTime(e.TimestampTicks),
                        ToolName = e.ToolName,
                        Status = (MCPToolCallStatus)e.Status,
                        ResultSummary = e.ResultSummary,
                        ImageFilePath = e.ImageFilePath,
                        EstimatedTokens = e.EstimatedTokens
                    };
                    _head = (_head + 1) % _buffer.Length;
                    if (_count < _buffer.Length) _count++;
                }
            }
        }
    }
}
