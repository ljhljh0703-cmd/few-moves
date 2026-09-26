using System;
using System.Text;

namespace Nectorial.SlideEscape.Record
{
    public static class RecordCapsuleCodec
    {
        private const string Prefix = "fm1";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool TryEncode(RecordCapsule capsule, out string encoded, out string error)
        {
            encoded = null;
            error = null;
            if (!TryValidateFields(capsule, out error)) return false;

            string[] values = new[]
            {
                capsule.ModeId,
                capsule.DefinitionId,
                capsule.RulesVersion,
                capsule.ContentVersion,
                capsule.DefinitionFingerprint,
                capsule.InputSequence
            };
            int decodedBytes = 0;
            var builder = new StringBuilder(Prefix);
            for (int index = 0; index < values.Length; index++)
            {
                byte[] bytes = Utf8.GetBytes(values[index]);
                decodedBytes = checked(decodedBytes + bytes.Length);
                if (decodedBytes > RecordCapsuleRules.MaximumDecodedBytes) { error = "capsule_decoded_too_large"; return false; }
                builder.Append('.').Append(ToBase64Url(bytes));
                if (builder.Length > RecordCapsuleRules.MaximumEncodedCharacters) { error = "capsule_encoded_too_large"; return false; }
            }
            encoded = builder.ToString();
            return true;
        }

        public static bool TryDecode(string encoded, out RecordCapsule capsule, out string error)
        {
            capsule = null;
            error = null;
            if (string.IsNullOrEmpty(encoded)) { error = "capsule_missing"; return false; }
            if (encoded.Length > RecordCapsuleRules.MaximumEncodedCharacters) { error = "capsule_encoded_too_large"; return false; }
            string[] parts = encoded.Split('.');
            if (parts.Length != 7 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)) { error = "capsule_format_invalid"; return false; }

            string[] values = new string[6];
            int decodedBytes = 0;
            for (int index = 0; index < values.Length; index++)
            {
                byte[] bytes;
                if (!TryFromCanonicalBase64Url(parts[index + 1], out bytes)) { error = "capsule_base64_invalid:" + index.ToString(); return false; }
                decodedBytes = checked(decodedBytes + bytes.Length);
                if (decodedBytes > RecordCapsuleRules.MaximumDecodedBytes) { error = "capsule_decoded_too_large"; return false; }
                try { values[index] = Utf8.GetString(bytes); }
                catch (DecoderFallbackException) { error = "capsule_utf8_invalid:" + index.ToString(); return false; }
            }
            capsule = new RecordCapsule
            {
                SchemaVersion = RecordCapsuleRules.SchemaVersion,
                ModeId = values[0],
                DefinitionId = values[1],
                RulesVersion = values[2],
                ContentVersion = values[3],
                DefinitionFingerprint = values[4],
                InputSequence = values[5]
            };
            if (!TryValidateFields(capsule, out error)) { capsule = null; return false; }
            return true;
        }

        private static bool TryValidateFields(RecordCapsule capsule, out string error)
        {
            error = null;
            if (capsule == null) { error = "capsule_missing"; return false; }
            if (capsule.SchemaVersion != RecordCapsuleRules.SchemaVersion) { error = "capsule_schema_invalid"; return false; }
            if (!Bounded(capsule.ModeId, 64) || !Bounded(capsule.DefinitionId, 128) || !Bounded(capsule.RulesVersion, 128) || !Bounded(capsule.ContentVersion, 128) || !Bounded(capsule.DefinitionFingerprint, 128))
            {
                error = "capsule_identity_invalid";
                return false;
            }
            if (capsule.InputSequence == null || capsule.InputSequence.Length > RecordCapsuleRules.MaximumInputCharacters)
            {
                error = "capsule_input_length_invalid";
                return false;
            }
            for (int index = 0; index < capsule.InputSequence.Length; index++)
            {
                char value = capsule.InputSequence[index];
                if (value != 'U' && value != 'D' && value != 'L' && value != 'R')
                {
                    error = "capsule_input_invalid:" + index.ToString();
                    return false;
                }
            }
            return true;
        }

        private static bool Bounded(string value, int maximumLength)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= maximumLength;
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool TryFromCanonicalBase64Url(string value, out byte[] bytes)
        {
            bytes = null;
            if (value == null) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char item = value[index];
                bool allowed = (item >= 'A' && item <= 'Z') || (item >= 'a' && item <= 'z') || (item >= '0' && item <= '9') || item == '-' || item == '_';
                if (!allowed) return false;
            }
            if (value.Length % 4 == 1) return false;
            string padded = value.Replace('-', '+').Replace('_', '/');
            int padding = (4 - (padded.Length % 4)) % 4;
            if (padding > 0) padded = padded + new string('=', padding);
            try { bytes = Convert.FromBase64String(padded); }
            catch (FormatException) { return false; }
            return string.Equals(value, ToBase64Url(bytes), StringComparison.Ordinal);
        }
    }
}
