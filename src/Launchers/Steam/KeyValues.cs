using System;
using System.Collections.Generic;
using System.Text;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class KvNode
    {
        public readonly Dictionary<string, KvNode> Children = new Dictionary<string, KvNode>(StringComparer.OrdinalIgnoreCase);
        public string Value;
        public string Get(string key) { KvNode child; return Children.TryGetValue(key, out child) ? child.Value : null; }
        public KvNode Child(string key) { KvNode child; return Children.TryGetValue(key, out child) ? child : null; }
    }

    public sealed class KeyValues
    {
        private readonly string text;
        private int offset;
        private KeyValues(string text) { this.text = text; }
        public static KvNode Parse(string text)
        {
            if (text == null || text.Length > 4 * 1024 * 1024) throw new FormatException("Invalid KeyValues size");
            return new KeyValues(text).ReadObject(false, 0);
        }
        private KvNode ReadObject(bool nested, int depth)
        {
            if (depth > 64) throw new FormatException("KeyValues nesting limit");
            var node = new KvNode();
            while (true)
            {
                Skip();
                if (offset == text.Length)
                {
                    if (nested) throw new FormatException("Unclosed KeyValues object");
                    return node;
                }
                if (text[offset] == '}')
                {
                    if (!nested) throw new FormatException("Unexpected closing brace");
                    offset++; return node;
                }
                string key = Token();
                Skip();
                KvNode child;
                if (offset < text.Length && text[offset] == '{') { offset++; child = ReadObject(true, depth + 1); }
                else child = new KvNode { Value = Token() };
                if (node.Children.ContainsKey(key)) throw new FormatException("Duplicate KeyValues key: " + key);
                node.Children.Add(key, child);
            }
        }
        private void Skip()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || text[offset] == '\uFEFF') { offset++; continue; }
                if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
        }
        private string Token()
        {
            Skip();
            if (offset == text.Length || text[offset] == '{' || text[offset] == '}') throw new FormatException("Missing KeyValues token");
            if (text[offset] != '"')
            {
                int start = offset;
                while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] != '{' && text[offset] != '}') offset++;
                return text.Substring(start, offset - start);
            }
            offset++;
            var value = new StringBuilder();
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == '"') return value.ToString();
                if (c == '\\' && offset < text.Length)
                {
                    char escaped = text[offset++];
                    if (escaped == '\\' || escaped == '"') value.Append(escaped);
                    else { value.Append('\\'); value.Append(escaped); }
                }
                else value.Append(c);
            }
            throw new FormatException("Unclosed KeyValues string");
        }
    }
}
