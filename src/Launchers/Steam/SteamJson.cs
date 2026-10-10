using System;
using System.Text;
using System.Web.Script.Serialization;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamJson
    {
        public static string Serialize(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 32 }.Serialize(value);
        }

        public static T Deserialize<T>(string text)
        {
            if (text == null || text.Length > 1048576) throw new FormatException("Steam UI message too large");
            try { return new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 32 }.Deserialize<T>(text); }
            catch (ArgumentException exception) { throw new FormatException("Invalid Steam UI JSON", exception); }
            catch (InvalidOperationException exception) { throw new FormatException("Invalid Steam UI JSON", exception); }
        }

        public static string Encode(object value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(value)));
        }

        public static T Decode<T>(string value)
        {
            if (value == null || value.Length > 65536) throw new FormatException("Steam reader message too large");
            try { return Deserialize<T>(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value))); }
            catch (DecoderFallbackException exception) { throw new FormatException("Invalid Steam reader encoding", exception); }
        }
    }
}
