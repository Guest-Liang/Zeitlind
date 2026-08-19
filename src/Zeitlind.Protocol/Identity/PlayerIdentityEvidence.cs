namespace Zeitlind.Protocol.Identity;

public enum PlayerIdentityConfidence
{
    None,
    Weak,
    Confirmed,
}

public readonly record struct PlayerIdentityEvidence(ulong Uid, PlayerIdentityConfidence Confidence, string Detail);
