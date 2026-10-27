using System;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Record
{
    public static class RecordCapsuleRules
    {
        public const int SchemaVersion = 1;
        public const int MaximumInputCharacters = 256;
        public const int MaximumEncodedCharacters = 4096;
        public const int MaximumDecodedBytes = 2048;
        public const string RaidModeId = "raid-v1";
        public const string CoopModeId = "coop-v1";
    }

    [Serializable]
    public sealed class RecordCapsule
    {
        public int SchemaVersion;
        public string ModeId;
        public string DefinitionId;
        public string RulesVersion;
        public string ContentVersion;
        public string DefinitionFingerprint;
        public string InputSequence;
    }

    [Serializable]
    public sealed class RecordVerification
    {
        public bool Valid;
        public string ErrorCode;
        public string ModeId;
        public string DefinitionId;
        public string RulesVersion;
        public string ContentVersion;
        public string DefinitionFingerprint;
        public string StatusCode;
        public int InputCount;
        public int EffectiveActionCount;
        public int LogicalActionCount;
        public int Hits;
        public int ActiveActorCode;
        public GridPoint CirclePosition;
        public GridPoint DiamondPosition;
    }
}
