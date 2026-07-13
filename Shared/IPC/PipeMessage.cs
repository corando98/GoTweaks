using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Shared.Enums;
using Windows.Foundation.Collections;

namespace Shared.IPC
{
    /// <summary>
    /// Message format for Named Pipe communication between widget and helper.
    /// Provides JSON serialization/deserialization compatible with ValueSet format.
    /// </summary>
    public class PipeMessage
    {
        /// <summary>
        /// Unique request ID for correlating requests with responses.
        /// Set by client, echoed by server. 0 means async push (no correlation needed).
        /// </summary>
        public int RequestId { get; set; }

        /// <summary>
        /// Command type (Get, Set, Response)
        /// </summary>
        public Command Command { get; set; }

        /// <summary>
        /// Function/property being accessed
        /// </summary>
        public Function Function { get; set; }

        /// <summary>
        /// Content/value (string representation)
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Additional key-value pairs
        /// </summary>
        public Dictionary<string, object> Extra { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Creates a PipeMessage from a ValueSet
        /// </summary>
        public static PipeMessage FromValueSet(ValueSet valueSet)
        {
            var msg = new PipeMessage();

            if (valueSet.TryGetValue(nameof(Command), out var cmdObj))
            {
                msg.Command = (Command)(int)cmdObj;
            }

            if (valueSet.TryGetValue(nameof(Function), out var funcObj))
            {
                msg.Function = (Function)(int)funcObj;
            }

            if (valueSet.TryGetValue(nameof(Content), out var contentObj))
            {
                msg.Content = contentObj?.ToString();
            }

            // Copy other properties
            foreach (var kvp in valueSet)
            {
                if (kvp.Key != nameof(Command) && kvp.Key != nameof(Function) && kvp.Key != nameof(Content))
                {
                    msg.Extra[kvp.Key] = kvp.Value;
                }
            }

            return msg;
        }

        /// <summary>
        /// Converts to a ValueSet for compatibility with existing code
        /// </summary>
        public ValueSet ToValueSet()
        {
            var valueSet = new ValueSet
            {
                { nameof(Command), (int)Command },
                { nameof(Function), (int)Function }
            };

            if (Content != null)
            {
                valueSet[nameof(Content)] = Content;
            }

            foreach (var kvp in Extra)
            {
                valueSet[kvp.Key] = kvp.Value;
            }

            return valueSet;
        }

        /// <summary>
        /// Serializes to JSON string
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"RequestId\":{RequestId}");
            sb.Append($",\"Command\":{(int)Command}");
            sb.Append($",\"Function\":{(int)Function}");

            if (Content != null)
            {
                sb.Append($",\"Content\":\"{EscapeJson(Content)}\"");
            }

            foreach (var kvp in Extra)
            {
                sb.Append($",\"{EscapeJson(kvp.Key)}\":");
                sb.Append(ValueToJson(kvp.Value));
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Deserializes from JSON string
        /// </summary>
        public static PipeMessage FromJson(string json)
        {
            var msg = new PipeMessage();

            try
            {
                // Simple JSON parsing - extract key-value pairs
                // Format: {"RequestId":123,"Command":1,"Function":5,"Content":"value",...}
                json = json.Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}"))
                    return msg;

                json = json.Substring(1, json.Length - 2); // Remove { }

                // Parse RequestId
                var reqIdMatch = Regex.Match(json, @"""RequestId""\s*:\s*(\d+)");
                if (reqIdMatch.Success)
                {
                    msg.RequestId = int.Parse(reqIdMatch.Groups[1].Value);
                }

                // Parse Command
                var cmdMatch = Regex.Match(json, @"""Command""\s*:\s*(\d+)");
                if (cmdMatch.Success)
                {
                    msg.Command = (Command)int.Parse(cmdMatch.Groups[1].Value);
                }

                // Parse Function
                var funcMatch = Regex.Match(json, @"""Function""\s*:\s*(\d+)");
                if (funcMatch.Success)
                {
                    msg.Function = (Function)int.Parse(funcMatch.Groups[1].Value);
                }

                // Parse Content - can be string, number, or boolean
                var contentMatch = Regex.Match(json, @"""Content""\s*:\s*""([^""\\]*(\\.[^""\\]*)*)""");
                if (contentMatch.Success)
                {
                    msg.Content = UnescapeJson(contentMatch.Groups[1].Value);
                }
                else
                {
                    // Try boolean content (true/false)
                    contentMatch = Regex.Match(json, @"""Content""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                    if (contentMatch.Success)
                    {
                        // Store as "True" or "False" for .NET bool.Parse compatibility
                        msg.Content = contentMatch.Groups[1].Value.ToLower() == "true" ? "True" : "False";
                    }
                    else
                    {
                        // Try numeric content
                        contentMatch = Regex.Match(json, @"""Content""\s*:\s*(-?\d+\.?\d*)");
                        if (contentMatch.Success)
                        {
                            msg.Content = contentMatch.Groups[1].Value;
                        }
                    }
                }

                // Parse other key-value pairs (simplified - handles strings and numbers)
                var otherMatches = Regex.Matches(json, @"""(\w+)""\s*:\s*(""[^""\\]*(\\.[^""\\]*)*""|-?\d+\.?\d*|true|false|null)");
                foreach (Match match in otherMatches)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;

                    if (key == "Command" || key == "Function" || key == "Content")
                        continue;

                    if (value.StartsWith("\""))
                    {
                        msg.Extra[key] = UnescapeJson(value.Substring(1, value.Length - 2));
                    }
                    else if (value == "true")
                    {
                        msg.Extra[key] = true;
                    }
                    else if (value == "false")
                    {
                        msg.Extra[key] = false;
                    }
                    else if (value == "null")
                    {
                        msg.Extra[key] = null;
                    }
                    else if (value.Contains("."))
                    {
                        if (double.TryParse(value, out var d))
                            msg.Extra[key] = d;
                    }
                    else
                    {
                        // Try long first for large values like UpdatedTime (DateTime.Ticks)
                        if (long.TryParse(value, out var l))
                            msg.Extra[key] = l;
                        else if (int.TryParse(value, out var i))
                            msg.Extra[key] = i;
                    }
                }
            }
            catch
            {
                // Return empty message on parse error
            }

            return msg;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            // Single-pass scan, not sequential whole-string Replace() calls. Replacing "\\"
            // with "\" before replacing "\t"/"\n"/"\r" lets a freshly-collapsed backslash pair
            // up with an unrelated adjacent letter from the ORIGINAL string and get misread as
            // an escape one step later — e.g. "C:\\temp" (escaped "C:\temp") collapses "\\" ->
            // "\" first, leaving "C:\temp", and the very next step matches that backslash + "t"
            // as "\t" and corrupts it into a real tab ("C:<TAB>emp"). Decode each escape once.
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        default: sb.Append(c); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string ValueToJson(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{EscapeJson(s)}\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is int || value is long || value is double || value is float)
                return value.ToString();
            return $"\"{EscapeJson(value.ToString())}\"";
        }

        public override string ToString()
        {
            return $"PipeMessage[{Command} {Function}: {Content?.Substring(0, Math.Min(50, Content?.Length ?? 0))}...]";
        }
    }
}
