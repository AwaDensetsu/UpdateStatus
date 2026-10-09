using System;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Shared S3 payload for selected and background observation. Export only
    // reviewed measurements/metadata, never opaque native padding.
    public static class SteamUpdatePayload
    {
        public static string Encode(byte[] update, bool enabled, uint? speed = null, uint networkAppId = 0)
        {
            if (update == null) return "-";
            if (update.Length != 248) throw new FormatException("Unexpected Steam update struct size");
            var clean = new byte[248];
            clean[0] = enabled ? (byte)1 : (byte)2;
            if (speed.HasValue)
            {
                Array.Copy(BitConverter.GetBytes(1u),0,clean,12,4);
                Array.Copy(BitConverter.GetBytes(speed.Value),0,clean,16,4);
                Array.Copy(BitConverter.GetBytes(networkAppId),0,clean,20,4);
            }
            foreach (int offset in new[] { 4,8,32,36,60 }) Array.Copy(update,offset,clean,offset,4);
            Array.Copy(update,80,clean,80,168);
            return BitConverter.ToString(clean).Replace("-","");
        }
    }
}
