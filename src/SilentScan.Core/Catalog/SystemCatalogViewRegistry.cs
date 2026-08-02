namespace SilentScan.Core.Catalog;

/// <summary>
/// A curated set of built-in SQL Server system catalog views/compatibility views, every column
/// name/type verified directly against <c>sys.dm_exec_describe_first_result_set</c> on the
/// Docker oracle rather than taken from documentation or memory (CLAUDE.md precision discipline:
/// never guess). Before this existed, any predicate against these - overwhelmingly common in
/// DBA/admin scripts (this project's own First Responder Kit and Ola Hallengren corpus entries
/// are built almost entirely out of them, and DNN Platform's incremental upgrade scripts probe
/// dbo.sysobjects/sys.objects constantly to check "does this object already exist") - resolved as
/// an unrecognized table reference, both discarding real findings (a predicate like
/// <c>sys.dm_...</c> comparing an int catalog column against an nvarchar variable is exactly the
/// bug class this tool exists to find) and inflating the skip ledger's dominant cause across
/// every corpus repo (an audit finding).
///
/// Collation is deliberately left unresolved (null) for every string-family column here - it was
/// not verified against the oracle, and CLAUDE.md's rule is never to guess one. A predicate
/// comparing one of these columns still resolves through the normal pipeline; it just can't reach
/// a collation-dependent verdict, exactly like any other real column with an unpinned collation.
///
/// This is a curated allowlist covering the small set of catalog views actually driving the skip
/// counts in this project's own pinned corpus (sys.objects/sysobjects, sys.indexes/sysindexes,
/// sys.columns/syscolumns, sys.tables, sys.databases, sys.schemas) - not an attempt at a complete
/// system-view catalog. A reference to a view not in this table still resolves as "no known DDL"
/// exactly as before, recorded in the skip ledger rather than guessed.
/// </summary>
public static class SystemCatalogViewRegistry
{
    private static readonly SqlType NVarChar128 = new(SqlTypeCategory.NVarChar, Length: 128);
    private static readonly SqlType Int32 = new(SqlTypeCategory.Int);
    private static readonly SqlType SmallInt16 = new(SqlTypeCategory.SmallInt);
    private static readonly SqlType TinyInt8 = new(SqlTypeCategory.TinyInt);
    private static readonly SqlType Bit1 = new(SqlTypeCategory.Bit);
    private static readonly SqlType BigInt64 = new(SqlTypeCategory.BigInt);
    private static readonly SqlType DateTime8 = new(SqlTypeCategory.DateTime);
    private static readonly SqlType UniqueIdentifier16 = new(SqlTypeCategory.UniqueIdentifier);
    private static readonly SqlType Char2 = new(SqlTypeCategory.Char, Length: 2);
    private static readonly SqlType NVarChar60 = new(SqlTypeCategory.NVarChar, Length: 60);

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysObjectsColumns =
    [
        ("name", NVarChar128), ("id", Int32), ("xtype", Char2), ("uid", SmallInt16), ("info", SmallInt16),
        ("status", Int32), ("base_schema_ver", Int32), ("replinfo", Int32), ("parent_obj", Int32),
        ("crdate", DateTime8), ("ftcatid", SmallInt16), ("schema_ver", Int32), ("stats_schema_ver", Int32),
        ("type", Char2), ("userstat", SmallInt16), ("sysstat", SmallInt16), ("indexdel", SmallInt16),
        ("refdate", DateTime8), ("version", Int32), ("deltrig", Int32), ("instrig", Int32),
        ("updtrig", Int32), ("seltrig", Int32), ("category", Int32), ("cache", SmallInt16),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysDotObjectsColumns =
    [
        ("name", NVarChar128), ("object_id", Int32), ("principal_id", Int32), ("schema_id", Int32),
        ("parent_object_id", Int32), ("type", Char2), ("type_desc", NVarChar60), ("create_date", DateTime8),
        ("modify_date", DateTime8), ("is_ms_shipped", Bit1), ("is_published", Bit1), ("is_schema_published", Bit1),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysIndexesColumns =
    [
        ("id", Int32), ("status", Int32), ("first", new SqlType(SqlTypeCategory.Binary, Length: 6)),
        ("indid", SmallInt16), ("root", new SqlType(SqlTypeCategory.Binary, Length: 6)), ("minlen", SmallInt16),
        ("keycnt", SmallInt16), ("groupid", SmallInt16), ("dpages", Int32), ("reserved", Int32), ("used", Int32),
        ("rowcnt", BigInt64), ("rowmodctr", Int32), ("reserved3", TinyInt8), ("reserved4", TinyInt8),
        ("xmaxlen", SmallInt16), ("maxirow", SmallInt16), ("OrigFillFactor", TinyInt8), ("StatVersion", TinyInt8),
        ("reserved2", Int32), ("FirstIAM", new SqlType(SqlTypeCategory.Binary, Length: 6)), ("impid", SmallInt16),
        ("lockflags", SmallInt16), ("pgmodctr", Int32), ("keys", new SqlType(SqlTypeCategory.VarBinary, Length: 1088)),
        ("name", NVarChar128), ("statblob", new SqlType(SqlTypeCategory.Image)), ("maxlen", Int32), ("rows", Int32),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysDotIndexesColumns =
    [
        ("object_id", Int32), ("name", NVarChar128), ("index_id", Int32), ("type", TinyInt8),
        ("type_desc", NVarChar60), ("is_unique", Bit1), ("data_space_id", Int32), ("ignore_dup_key", Bit1),
        ("is_primary_key", Bit1), ("is_unique_constraint", Bit1), ("fill_factor", TinyInt8), ("is_padded", Bit1),
        ("is_disabled", Bit1), ("is_hypothetical", Bit1), ("is_ignored_in_optimization", Bit1),
        ("allow_row_locks", Bit1), ("allow_page_locks", Bit1), ("has_filter", Bit1),
        ("filter_definition", new SqlType(SqlTypeCategory.NVarChar, IsMax: true)), ("compression_delay", Int32),
        ("suppress_dup_key_messages", Bit1), ("auto_created", Bit1), ("optimize_for_sequential_key", Bit1),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysColumnsColumns =
    [
        ("name", NVarChar128), ("id", Int32), ("xtype", TinyInt8), ("typestat", TinyInt8),
        ("xusertype", SmallInt16), ("length", SmallInt16), ("xprec", TinyInt8), ("xscale", TinyInt8),
        ("colid", SmallInt16), ("xoffset", SmallInt16), ("bitpos", TinyInt8), ("reserved", TinyInt8),
        ("colstat", SmallInt16), ("cdefault", Int32), ("domain", Int32), ("number", SmallInt16),
        ("colorder", SmallInt16), ("autoval", new SqlType(SqlTypeCategory.VarBinary, Length: 8000)),
        ("offset", SmallInt16), ("collationid", Int32), ("language", Int32), ("status", TinyInt8),
        ("type", TinyInt8), ("usertype", SmallInt16), ("printfmt", new SqlType(SqlTypeCategory.VarChar, Length: 255)),
        ("prec", SmallInt16), ("scale", Int32), ("iscomputed", Int32), ("isoutparam", Int32),
        ("isnullable", Int32), ("collation", NVarChar128), ("tdscollation", new SqlType(SqlTypeCategory.Binary, Length: 5)),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysDotColumnsColumns =
    [
        ("object_id", Int32), ("name", NVarChar128), ("column_id", Int32), ("system_type_id", TinyInt8),
        ("user_type_id", Int32), ("max_length", SmallInt16), ("precision", TinyInt8), ("scale", TinyInt8),
        ("collation_name", NVarChar128), ("is_nullable", Bit1), ("is_ansi_padded", Bit1), ("is_rowguidcol", Bit1),
        ("is_identity", Bit1), ("is_computed", Bit1), ("is_filestream", Bit1), ("is_replicated", Bit1),
        ("is_non_sql_subscribed", Bit1), ("is_merge_published", Bit1), ("is_dts_replicated", Bit1),
        ("is_xml_document", Bit1), ("xml_collection_id", Int32), ("default_object_id", Int32),
        ("rule_object_id", Int32), ("is_sparse", Bit1), ("is_column_set", Bit1),
        ("generated_always_type", TinyInt8), ("generated_always_type_desc", NVarChar60),
        ("encryption_type", Int32), ("encryption_type_desc", new SqlType(SqlTypeCategory.NVarChar, Length: 64)),
        ("encryption_algorithm_name", NVarChar128), ("column_encryption_key_id", Int32),
        ("column_encryption_key_database_name", NVarChar128), ("is_hidden", Bit1), ("is_masked", Bit1),
        ("graph_type", Int32), ("graph_type_desc", NVarChar60), ("is_data_deletion_filter_column", Bit1),
        ("ledger_view_column_type", Int32), ("ledger_view_column_type_desc", NVarChar60),
        ("is_dropped_ledger_column", Bit1),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysTablesColumns =
    [
        .. SysDotObjectsColumns,
        ("lob_data_space_id", Int32), ("filestream_data_space_id", Int32), ("max_column_id_used", Int32),
        ("lock_on_bulk_load", Bit1), ("uses_ansi_nulls", Bit1), ("has_replication_filter", Bit1),
        ("is_sync_tran_subscribed", Bit1), ("has_unchecked_assembly_data", Bit1), ("text_in_row_limit", Int32),
        ("large_value_types_out_of_row", Bit1), ("is_tracked_by_cdc", Bit1), ("lock_escalation", TinyInt8),
        ("lock_escalation_desc", NVarChar60), ("is_filetable", Bit1), ("is_memory_optimized", Bit1),
        ("durability", TinyInt8), ("durability_desc", NVarChar60), ("temporal_type", TinyInt8),
        ("temporal_type_desc", NVarChar60), ("history_table_id", Int32),
        ("is_remote_data_archive_enabled", Bit1), ("is_external", Bit1), ("history_retention_period", Int32),
        ("history_retention_period_unit", Int32),
        ("history_retention_period_unit_desc", new SqlType(SqlTypeCategory.NVarChar, Length: 10)),
        ("is_node", Bit1), ("is_edge", Bit1), ("data_retention_period", Int32),
        ("data_retention_period_unit", Int32),
        ("data_retention_period_unit_desc", new SqlType(SqlTypeCategory.NVarChar, Length: 10)),
        ("ledger_type", TinyInt8), ("ledger_type_desc", NVarChar60), ("ledger_view_id", Int32),
        ("is_dropped_ledger_table", Bit1),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysDatabasesColumns =
    [
        ("name", NVarChar128), ("database_id", Int32), ("source_database_id", Int32),
        ("owner_sid", new SqlType(SqlTypeCategory.VarBinary, Length: 85)), ("create_date", DateTime8),
        ("compatibility_level", TinyInt8), ("collation_name", NVarChar128), ("user_access", TinyInt8),
        ("user_access_desc", NVarChar60), ("is_read_only", Bit1), ("is_auto_close_on", Bit1),
        ("is_auto_shrink_on", Bit1), ("state", TinyInt8), ("state_desc", NVarChar60), ("is_in_standby", Bit1),
        ("is_cleanly_shutdown", Bit1), ("is_supplemental_logging_enabled", Bit1),
        ("snapshot_isolation_state", TinyInt8), ("snapshot_isolation_state_desc", NVarChar60),
        ("is_read_committed_snapshot_on", Bit1), ("recovery_model", TinyInt8), ("recovery_model_desc", NVarChar60),
        ("page_verify_option", TinyInt8), ("page_verify_option_desc", NVarChar60),
        ("is_auto_create_stats_on", Bit1), ("is_auto_create_stats_incremental_on", Bit1),
        ("is_auto_update_stats_on", Bit1), ("is_auto_update_stats_async_on", Bit1),
        ("is_ansi_null_default_on", Bit1), ("is_ansi_nulls_on", Bit1), ("is_ansi_padding_on", Bit1),
        ("is_ansi_warnings_on", Bit1), ("is_arithabort_on", Bit1), ("is_concat_null_yields_null_on", Bit1),
        ("is_numeric_roundabort_on", Bit1), ("is_quoted_identifier_on", Bit1),
        ("is_recursive_triggers_on", Bit1), ("is_cursor_close_on_commit_on", Bit1),
        ("is_local_cursor_default", Bit1), ("is_fulltext_enabled", Bit1), ("is_trustworthy_on", Bit1),
        ("is_db_chaining_on", Bit1), ("is_parameterization_forced", Bit1),
        ("is_master_key_encrypted_by_server", Bit1), ("is_query_store_on", Bit1), ("is_published", Bit1),
        ("is_subscribed", Bit1), ("is_merge_published", Bit1), ("is_distributor", Bit1),
        ("is_sync_with_backup", Bit1), ("service_broker_guid", UniqueIdentifier16), ("is_broker_enabled", Bit1),
        ("log_reuse_wait", TinyInt8), ("log_reuse_wait_desc", NVarChar60), ("is_date_correlation_on", Bit1),
        ("is_cdc_enabled", Bit1), ("is_encrypted", Bit1), ("is_honor_broker_priority_on", Bit1),
        ("replica_id", UniqueIdentifier16), ("group_database_id", UniqueIdentifier16),
        ("resource_pool_id", Int32), ("default_language_lcid", SmallInt16), ("default_language_name", NVarChar128),
        ("default_fulltext_language_lcid", Int32), ("default_fulltext_language_name", NVarChar128),
        ("is_nested_triggers_on", Bit1), ("is_transform_noise_words_on", Bit1), ("two_digit_year_cutoff", SmallInt16),
        ("containment", TinyInt8), ("containment_desc", NVarChar60), ("target_recovery_time_in_seconds", Int32),
        ("delayed_durability", Int32), ("delayed_durability_desc", NVarChar60),
        ("is_memory_optimized_elevate_to_snapshot_on", Bit1), ("is_federation_member", Bit1),
        ("is_remote_data_archive_enabled", Bit1), ("is_mixed_page_allocation_on", Bit1),
        ("is_temporal_history_retention_enabled", Bit1), ("catalog_collation_type", Int32),
        ("catalog_collation_type_desc", NVarChar60), ("physical_database_name", NVarChar128),
        ("is_result_set_caching_on", Bit1), ("is_accelerated_database_recovery_on", Bit1),
        ("is_tempdb_spill_to_remote_store", Bit1), ("is_stale_page_detection_on", Bit1),
        ("is_memory_optimized_enabled", Bit1), ("is_data_retention_enabled", Bit1), ("is_ledger_on", Bit1),
        ("is_change_feed_enabled", Bit1),
    ];

    private static readonly IReadOnlyList<(string Name, SqlType Type)> SysSchemasColumns =
    [
        ("name", NVarChar128), ("schema_id", Int32), ("principal_id", Int32),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Name, SqlType Type)>> ByQualifiedName =
        new Dictionary<string, IReadOnlyList<(string Name, SqlType Type)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.sysobjects"] = SysObjectsColumns,
            ["sys.sysobjects"] = SysObjectsColumns,
            ["sys.objects"] = SysDotObjectsColumns,
            ["dbo.sysindexes"] = SysIndexesColumns,
            ["sys.sysindexes"] = SysIndexesColumns,
            ["sys.indexes"] = SysDotIndexesColumns,
            ["dbo.syscolumns"] = SysColumnsColumns,
            ["sys.syscolumns"] = SysColumnsColumns,
            ["sys.columns"] = SysDotColumnsColumns,
            ["sys.tables"] = SysTablesColumns,
            ["sys.databases"] = SysDatabasesColumns,
            ["sys.schemas"] = SysSchemasColumns,
        };

    /// <summary>The column shape for <paramref name="qualifiedName"/> if it's one of the curated system catalog views, or null (never guessed).</summary>
    public static IReadOnlyList<(string Name, SqlType Type)>? TryResolve(string qualifiedName) =>
        ByQualifiedName.GetValueOrDefault(qualifiedName);
}
