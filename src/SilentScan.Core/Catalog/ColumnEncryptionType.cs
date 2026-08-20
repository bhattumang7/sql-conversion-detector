namespace SilentScan.Core.Catalog;

/// <summary>
/// Mirrors <c>sys.columns.encryption_type</c> (<c>NULL</c>/<c>1</c>/<c>2</c>) - Always Encrypted's
/// per-column encryption scheme. <see cref="None"/> is the overwhelming common case (not Always
/// Encrypted at all); <see cref="Deterministic"/> and <see cref="Randomized"/> are catalog facts,
/// independent of whether the connecting client is itself Always-Encrypted-enabled.
/// </summary>
public enum ColumnEncryptionType
{
    None = 0,
    Deterministic = 1,
    Randomized = 2,
}
