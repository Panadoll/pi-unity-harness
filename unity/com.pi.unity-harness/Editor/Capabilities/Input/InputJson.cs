using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Pi.UnityHarness.Editor.Capabilities.Shared;

[assembly: InternalsVisibleTo("Pi.UnityHarness.Editor.Tests")]

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    internal sealed class InputJson
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private bool _needsComma;
        private bool _closed;

        private InputJson()
        {
            _sb.Append('{');
        }

        internal static InputJson Object()
        {
            return new InputJson();
        }

        internal InputJson String(string name, string value)
        {
            FieldPrefix(name);
            _sb.Append('"').Append(Escape(value)).Append('"');
            return this;
        }

        internal InputJson Number(string name, int value)
        {
            FieldPrefix(name);
            _sb.Append(value);
            return this;
        }

        internal InputJson Number(string name, long value)
        {
            FieldPrefix(name);
            _sb.Append(value);
            return this;
        }

        internal InputJson Number(string name, float value)
        {
            FieldPrefix(name);
            _sb.Append(Float(value));
            return this;
        }

        internal InputJson Bool(string name, bool value)
        {
            FieldPrefix(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        internal InputJson Raw(string name, string rawJson)
        {
            FieldPrefix(name);
            _sb.Append(string.IsNullOrEmpty(rawJson) ? "null" : rawJson);
            return this;
        }

        internal InputJson StringArray(string name, string[] values)
        {
            FieldPrefix(name);
            AppendStringArray(_sb, values);
            return this;
        }

        public override string ToString()
        {
            if (!_closed)
            {
                _sb.Append('}');
                _closed = true;
            }
            return _sb.ToString();
        }

        internal static string StringArray(string[] values)
        {
            var sb = new StringBuilder();
            AppendStringArray(sb, values);
            return sb.ToString();
        }

        internal static string Escape(string value)
        {
            return PiAbilityJson.Escape(value);
        }

        internal static string Float(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static void AppendStringArray(StringBuilder sb, string[] values)
        {
            sb.Append('[');
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Escape(values[i])).Append('"');
                }
            }
            sb.Append(']');
        }

        private void FieldPrefix(string name)
        {
            if (_closed)
                throw new System.InvalidOperationException("Cannot add fields after JSON object is closed.");
            if (string.IsNullOrEmpty(name))
                throw new System.ArgumentException("JSON field name is required.", nameof(name));
            if (_needsComma)
                _sb.Append(',');
            _sb.Append('"').Append(Escape(name)).Append("\":");
            _needsComma = true;
        }
    }
}
