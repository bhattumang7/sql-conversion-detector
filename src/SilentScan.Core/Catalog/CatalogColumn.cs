using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Catalog;

public sealed record CatalogColumn(
    string Name,
    SqlType? Type,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsPersisted,
    bool IsAnsiPadded = true,
    decimal? IdentitySeed = null,
    decimal? IdentityIncrement = null,
    decimal? IdentityCurrentValue = null,
    ColumnEncryptionType EncryptionType = ColumnEncryptionType.None,
    ColumnEncryptionEnclaveSupport EnclaveSupport = ColumnEncryptionEnclaveSupport.Unknown,
    bool IsMasked = false,
    string? MaskingFunctionName = null,
    bool IsGeneratedAlwaysPeriod = false,
    bool IsSparse = false,
    bool IsComputedNonDeterministic = false,
    bool IsComputedImprecise = false);
