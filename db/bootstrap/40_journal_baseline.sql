/*
 * 迁移基线登记
 *
 * 10_schema.sql 已包含下列迁移的最终结构，因此在此预先登记到 DbUp 的日志表，
 * 使应用启动时不会重复执行它们。应用会自动创建 ERP_SCHEMA_JOURNAL；
 * 这里显式建表以便在启动前完成登记。
 */

IF OBJECT_ID(N'dbo.ERP_SCHEMA_JOURNAL', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ERP_SCHEMA_JOURNAL (
        [schemaversionid] INT IDENTITY(1,1) NOT NULL,
        [scriptname]      NVARCHAR(255)     NOT NULL,
        [applied]         DATETIME          NOT NULL,
        CONSTRAINT [PK_ERP_SCHEMA_JOURNAL_id] PRIMARY KEY CLUSTERED ([schemaversionid])
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.001_attachments.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.001_attachments.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.002_workbench_definition_snapshot.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.002_workbench_definition_snapshot.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.003_workbench_idempotency.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.003_workbench_idempotency.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.004_audit_event.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.004_audit_event.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.005_quote_module_consolidation.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.005_quote_module_consolidation.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.006_assistant_chat.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.006_assistant_chat.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.007_employee_card_batch_special_page.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.007_employee_card_batch_special_page.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.008_workbench_url_prefix.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.008_workbench_url_prefix.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.009_choose_returnval_dead_mapping_cleanup.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.009_choose_returnval_dead_mapping_cleanup.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.010_pilot_workflow_1906.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.010_pilot_workflow_1906.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.011_2305_group_admin_menu.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.011_2305_group_admin_menu.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.012_2305_group_admin_special_page.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.012_2305_group_admin_special_page.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.013_flow_admin_routes.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.013_flow_admin_routes.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.014_legacy_deny_sentinel_cleanup.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.014_legacy_deny_sentinel_cleanup.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.015_2306_user_admin_special_page.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.015_2306_user_admin_special_page.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.016_table_data_modules_offline.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.016_table_data_modules_offline.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.017_fields_chooser.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.017_fields_chooser.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.018_fields_chooser_backfill.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.018_fields_chooser_backfill.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.019_2205_report_conditions_special_page.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.019_2205_report_conditions_special_page.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.020_field_datasource_rename.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.020_field_datasource_rename.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.021_fields_chooser_rerun.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.021_fields_chooser_rerun.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.022_report_rights_override.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.022_report_rights_override.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.023_report_center_menu_converge.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.023_report_center_menu_converge.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.024_report_condition_template.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.024_report_condition_template.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.025_report_conditions_page_offline.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.025_report_conditions_page_offline.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.026_report_schedule_inbox.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.026_report_schedule_inbox.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.027_report_metric.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.027_report_metric.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.028_chooser_debt_a_d.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.028_chooser_debt_a_d.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.029_report_layout_merge.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.029_report_layout_merge.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.030_tsql_tables_drop.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.030_tsql_tables_drop.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.031_report_form_layout.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.031_report_form_layout.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.032_form_cell_group_backfill.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.032_form_cell_group_backfill.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.033_form_cell_group_retitems.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.033_form_cell_group_retitems.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.034_form_cell_group_manual.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.034_form_cell_group_manual.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.035_form_cell_group_physical_name.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.035_form_cell_group_physical_name.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.036_report_form_layout_version.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.036_report_form_layout_version.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.037_chooser_b_query_relation.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.037_chooser_b_query_relation.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.038_chooser_b_rerun.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.038_chooser_b_rerun.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.039_chooser_dead_config_cleanup.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.039_chooser_dead_config_cleanup.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.040_chooser_intent_reconfigure.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.040_chooser_intent_reconfigure.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.041_drop_chooser_migration_log.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.041_drop_chooser_migration_log.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.042_drop_ghost_backup_tables.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.042_drop_ghost_backup_tables.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.043_assistant_memory.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.043_assistant_memory.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.044_kb_collection.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.044_kb_collection.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.045_assistant_usage_ledger.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.045_assistant_usage_ledger.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.046_report_metric_confirm_status.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.046_report_metric_confirm_status.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.047_audit_event_v2.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.047_audit_event_v2.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.048_report_metric_m10c_confirmations.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.048_report_metric_m10c_confirmations.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.049_report_metric_ghost_column_revert.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.049_report_metric_ghost_column_revert.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.050_field_relation.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.050_field_relation.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.051_report_metric_domain_confirmations.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.051_report_metric_domain_confirmations.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.052_module_business_action.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.052_module_business_action.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.053_business_action_op_source_table.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.053_business_action_op_source_table.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.054_business_action_op_source_terms.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.054_business_action_op_source_terms.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.055_module_validation_rule.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.055_module_validation_rule.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.056_seed_module_business_action_1607.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.056_seed_module_business_action_1607.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.057_field_relation_effect_extension.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.057_field_relation_effect_extension.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.058_validation_target_table.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.058_validation_target_table.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.059_module_effect_engine_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.059_module_effect_engine_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.060_drop_legacy_1607_approve_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.060_drop_legacy_1607_approve_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.061_drop_legacy_1406_approve_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.061_drop_legacy_1406_approve_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.062_drop_legacy_1505_approve_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.062_drop_legacy_1505_approve_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.063_drop_legacy_1407_return_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.063_drop_legacy_1407_return_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.064_drop_legacy_workflow_framework_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.064_drop_legacy_workflow_framework_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.065_drop_legacy_pwfb_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.065_drop_legacy_pwfb_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.066_drop_legacy_cus_approve_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.066_drop_legacy_cus_approve_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.067_drop_legacy_workorder_approve_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.067_drop_legacy_workorder_approve_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.068_drop_legacy_mould_approve_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.068_drop_legacy_mould_approve_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.069_drop_legacy_inventory_approve_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.069_drop_legacy_inventory_approve_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.070_drop_legacy_apply_prepay_hr_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.070_drop_legacy_apply_prepay_hr_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.071_clear_dangling_update_sproc_refs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.071_clear_dangling_update_sproc_refs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.072_hide_unwired_redeploy_module.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.072_hide_unwired_redeploy_module.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.073_drop_legacy_quote_settlement_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.073_drop_legacy_quote_settlement_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.074_offline_redeploy_engine_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.074_offline_redeploy_engine_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.075_clear_unreachable_aftersave_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.075_clear_unreachable_aftersave_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.076_seed_duplicate_check_rules.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.076_seed_duplicate_check_rules.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.077_prepay_requires_details.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.077_prepay_requires_details.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.078_seed_master_detail_unique_rules.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.078_seed_master_detail_unique_rules.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.079_seed_rule_applicability.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.079_seed_rule_applicability.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.080_disable_purchase_reference_check.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.080_disable_purchase_reference_check.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.081_disable_wage_lz_duplicate_check.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.081_disable_wage_lz_duplicate_check.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.082_rebuild_purchase_reference_check.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.082_rebuild_purchase_reference_check.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.083_detail_required_rollout.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.083_detail_required_rollout.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.084_normalize_empty_op_fields.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.084_normalize_empty_op_fields.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.085_seed_period_overlap_rules.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.085_seed_period_overlap_rules.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.086_clear_dead_autoapprove_without_confirm_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.086_clear_dead_autoapprove_without_confirm_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.087_lifecycle_notnull_first_batch.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.087_lifecycle_notnull_first_batch.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.088_enable_ci_row_ownership.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.088_enable_ci_row_ownership.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.089_drop_company_ci_redundant.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.089_drop_company_ci_redundant.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.090_seed_reference_exists_rules.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.090_seed_reference_exists_rules.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.091_fix_reference_exists_newline_escape.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.091_fix_reference_exists_newline_escape.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.092_null_multi_check_rule_message.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.092_null_multi_check_rule_message.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.093_fix_in_depot_reference_target.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.093_fix_in_depot_reference_target.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.094_seed_reference_exists_rules_b3b.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.094_seed_reference_exists_rules_b3b.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.095_drop_dept_sysdn_company_id.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.095_drop_dept_sysdn_company_id.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.096_default_company_sentinel.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.096_default_company_sentinel.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.097_bill_no_sequence.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.097_bill_no_sequence.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.098_clear_shell_aftersave_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.098_clear_shell_aftersave_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.099_seed_qty_save_1607.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.099_seed_qty_save_1607.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.100_seed_qty_save_moc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.100_seed_qty_save_moc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.101_seed_qty_save_cop_back.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.101_seed_qty_save_cop_back.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.102_seed_qty_save_cop_fitin.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.102_seed_qty_save_cop_fitin.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.103_seed_qty_save_moc_product_in.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.103_seed_qty_save_moc_product_in.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.104_seed_qty_save_cop_send.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.104_seed_qty_save_cop_send.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.105_retire_batch_workflow_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.105_retire_batch_workflow_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.106_retire_purchase_workflow_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.106_retire_purchase_workflow_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.107_retire_metadata_workflow_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.107_retire_metadata_workflow_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.108_retire_aftersave_hook_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.108_retire_aftersave_hook_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.109_retire_dead_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.109_retire_dead_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.110_retire_noop_mould_rule_families.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.110_retire_noop_mould_rule_families.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.111_seed_line_require_batch_no.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.111_seed_line_require_batch_no.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.112_retire_inv_occur_rule_families.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.112_retire_inv_occur_rule_families.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.113_seed_line_require_batch_no_batch2.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.113_seed_line_require_batch_no_batch2.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.114_retire_inv_moc_cop_rule_families.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.114_retire_inv_moc_cop_rule_families.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.115_seed_and_retire_cop_return_mou_get2.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.115_seed_and_retire_cop_return_mou_get2.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.116_seed_and_retire_pur_receive_sam_out.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.116_seed_and_retire_pur_receive_sam_out.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.117_seed_and_retire_moc_work.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.117_seed_and_retire_moc_work.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.118_downshift_field_copy.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.118_downshift_field_copy.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.119_downshift_set_state.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.119_downshift_set_state.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.120_reorder_2913_state_before_move.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.120_reorder_2913_state_before_move.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.121_downshift_link_stamp.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.121_downshift_link_stamp.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.122_catalog_mou_batch_qty_save.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.122_catalog_mou_batch_qty_save.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.123_downshift_link_stamp_detail.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.123_downshift_link_stamp_detail.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.124_catalog_qc_analysis_gated_qty.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.124_catalog_qc_analysis_gated_qty.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.125_catalog_mou_batchin_gated_qty.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.125_catalog_mou_batchin_gated_qty.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.126_catalog_moc_work_out_gated_qty.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.126_catalog_moc_work_out_gated_qty.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.127_catalog_moc_product_out.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.127_catalog_moc_product_out.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.128_catalog_sfc_process_fixed_time.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.128_catalog_sfc_process_fixed_time.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.129_catalog_cop_callback.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.129_catalog_cop_callback.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.130_catalog_moc_produce_change.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.130_catalog_moc_produce_change.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.131_catalog_pur_purchase_change.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.131_catalog_pur_purchase_change.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.132_catalog_cop_order_change.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.132_catalog_cop_order_change.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.133_seed_and_retire_moc_get_mou_get_moc_plan.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.133_seed_and_retire_moc_get_mou_get_moc_plan.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.134_catalog_row_assertions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.134_catalog_row_assertions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.135_catalog_cus_declaration.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.135_catalog_cus_declaration.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.136_catalog_cop_fitout.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.136_catalog_cop_fitout.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.137_catalog_sfc_daily.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.137_catalog_sfc_daily.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.138_drop_unused_report_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.138_drop_unused_report_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.139_drop_p_change_m_idx.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.139_drop_p_change_m_idx.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.140_drop_p_update_pro_mrp_all.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.140_drop_p_update_pro_mrp_all.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.141_drop_compare_baseline_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.141_drop_compare_baseline_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.142_drop_orphan_check_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.142_drop_orphan_check_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.143_drop_system_aux_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.143_drop_system_aux_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.144_drop_hr_report_sprocs.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.144_drop_hr_report_sprocs.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.145_drop_inventory_report_sproc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.145_drop_inventory_report_sproc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.146_catalog_pur_cancel.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.146_catalog_pur_cancel.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.147_catalog_moc_produce.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.147_catalog_moc_produce.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.148_catalog_mou_pro.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.148_catalog_mou_pro.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.149_catalog_sysdg.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.149_catalog_sysdg.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.150_revert_sysdg_takeover.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.150_revert_sysdg_takeover.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.151_catalog_employee_card.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.151_catalog_employee_card.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.152_catalog_wage_item_fields.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.152_catalog_wage_item_fields.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.153_catalog_cus_manual.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.153_catalog_cus_manual.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.154_catalog_hr_wage_lz.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.154_catalog_hr_wage_lz.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.155_catalog_moc_bom_stru.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.155_catalog_moc_bom_stru.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.156_catalog_sfc_plan.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.156_catalog_sfc_plan.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.157_catalog_cus_account.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.157_catalog_cus_account.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.158_catalog_pur_apply.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.158_catalog_pur_apply.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.159_catalog_bom_stru.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.159_catalog_bom_stru.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.160_catalog_cop_account.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.160_catalog_cop_account.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.161_catalog_cop_prepay.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.161_catalog_cop_prepay.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.162_catalog_purchase_due.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.162_catalog_purchase_due.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.163_catalog_pur_prepay.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.163_catalog_pur_prepay.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.164_catalog_pur_pay.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.164_catalog_pur_pay.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.165_catalog_cop_receipt.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.165_catalog_cop_receipt.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.166_catalog_cop_order.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.166_catalog_cop_order.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.167_catalog_cop_send.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.167_catalog_cop_send.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.168_catalog_pur_purchase.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.168_catalog_pur_purchase.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.169_catalog_hr_worktime.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.169_catalog_hr_worktime.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.170_catalog_hr_apply.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.170_catalog_hr_apply.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.171_merge_rollup_handlers.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.171_merge_rollup_handlers.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.172_drop_legacy_hook_columns.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.172_drop_legacy_hook_columns.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.173_widen_inv_depot_log_batch_no.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.173_widen_inv_depot_log_batch_no.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.174_create_depot_location.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.174_create_depot_location.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.175_create_depot_stock_policy.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.175_create_depot_stock_policy.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.176_seed_depot_location_sentinel.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.176_seed_depot_location_sentinel.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.177_widen_inv_pro_depot_four_key.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.177_widen_inv_pro_depot_four_key.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.178_widen_inv_depot_log_location.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.178_widen_inv_depot_log_location.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.179_create_depot_product_location.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.179_create_depot_product_location.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.180_add_location_columns_to_detail_tables.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.180_add_location_columns_to_detail_tables.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.181_add_check_stock_location_root.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.181_add_check_stock_location_root.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.182_reserve_month_close_dimensions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.182_reserve_month_close_dimensions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.183_fix_orphan_backup_defaults.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.183_fix_orphan_backup_defaults.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.184_stock_policy_month_close.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.184_stock_policy_month_close.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.185_depot_location_module_metadata.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.185_depot_location_module_metadata.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.186_depot_module_rights.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.186_depot_module_rights.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.187_fix_depot_module_menu_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.187_fix_depot_module_menu_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.188_fix_depot_module_flags.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.188_fix_depot_module_flags.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.189_detail_location_field_metadata.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.189_detail_location_field_metadata.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.190_drop_sysdl_group_idx.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.190_drop_sysdl_group_idx.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.191_cleanup_empty_group_links.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.191_cleanup_empty_group_links.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.192_cop_send_stock_location_dimension.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.192_cop_send_stock_location_dimension.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.193_depot_location_crud_and_guards.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.193_depot_location_crud_and_guards.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.194_depot_location_path_default.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.194_depot_location_path_default.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.195_depot_location_disable_auto_approve.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.195_depot_location_disable_auto_approve.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.196_stocktake_scope_generate.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.196_stocktake_scope_generate.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.197_stocktake_location_fields_visible.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.197_stocktake_location_fields_visible.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.198_location_columns_default_visible.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.198_location_columns_default_visible.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.199_module_discriminator_flags.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.199_module_discriminator_flags.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.200_depot_stock_policy_disable_auto_approve.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.200_depot_stock_policy_disable_auto_approve.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.201_retire_orphan_report_modules.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.201_retire_orphan_report_modules.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.202_sample_out_qty_diagnostic_rows.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.202_sample_out_qty_diagnostic_rows.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.203_depot_location_lifecycle_field_metadata.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.203_depot_location_lifecycle_field_metadata.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.204_grouped_qty_diagnostic_agg.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.204_grouped_qty_diagnostic_agg.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.205_report_format_selection.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.205_report_format_selection.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.206_stock_policy_admin_page.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.206_stock_policy_admin_page.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.207_system_parameter_verticalization.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.207_system_parameter_verticalization.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.208_system_param_db_objects.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.208_system_param_db_objects.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.209_system_param_group_order.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.209_system_param_group_order.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.210_retire_remark_parameter.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.210_retire_remark_parameter.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.211_fix_placeholder_field_labels.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.211_fix_placeholder_field_labels.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.212_fix_placeholder_field_labels_batch2.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.212_fix_placeholder_field_labels_batch2.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.213_form_chooser_source_memo.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.213_form_chooser_source_memo.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.214_required_detail_key_columns.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.214_required_detail_key_columns.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.215_wage_module_discriminator_flags.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.215_wage_module_discriminator_flags.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.216_wage_module_effect_engine.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.216_wage_module_effect_engine.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.217_document_action_buttons.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.217_document_action_buttons.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.218_stocktake_recalc_account_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.218_stocktake_recalc_account_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.219_stocktake_generate_adjustment_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.219_stocktake_generate_adjustment_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.220_stock_policy_relocate_sentinel_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.220_stock_policy_relocate_sentinel_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.221_retire_relocate_sentinel_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.221_retire_relocate_sentinel_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.222_stocktake_restock_scope_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.222_stocktake_restock_scope_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.223_purchase_reprice_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.223_purchase_reprice_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.224_material_issue_allocate_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.224_material_issue_allocate_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.225_produce_calc_materials_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.225_produce_calc_materials_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.226_produce_gen_sub_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.226_produce_gen_sub_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.227_module_config_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.227_module_config_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.228_engine_owned_columns_readonly.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.228_engine_owned_columns_readonly.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.229_stocktake_detail_df_verify_location_batch.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.229_stocktake_detail_df_verify_location_batch.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.230_month_close_detail_dimension_key.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.230_month_close_detail_dimension_key.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.231_month_close_snapshot_button.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.231_month_close_snapshot_button.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.232_form_layout_tables.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.232_form_layout_tables.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.233_form_layout_fix_orphan_companions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.233_form_layout_fix_orphan_companions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.234_inventory_freeze_reserve.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.234_inventory_freeze_reserve.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.235_month_close_derived_fields_readonly.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.235_month_close_derived_fields_readonly.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.236_month_snapshot_recon_view.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.236_month_snapshot_recon_view.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.237_depot_product_location_module.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.237_depot_product_location_module.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.238_depot_product_location_default_fields.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.238_depot_product_location_default_fields.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.239_depot_product_location_rights.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.239_depot_product_location_rights.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.240_master_field_write_entry.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.240_master_field_write_entry.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.241_retire_field_level_form_layout.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.241_retire_field_level_form_layout.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.242_month_close_half_stock_scope.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.242_month_close_half_stock_scope.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.243_restore_field_level_form_layout_columns.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.243_restore_field_level_form_layout_columns.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.244_drop_field_level_form_layout_columns.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.244_drop_field_level_form_layout_columns.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.245_month_close_snapshot_button_grant.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.245_month_close_snapshot_button_grant.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.246_inventory_freeze_buttons.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.246_inventory_freeze_buttons.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.247_inventory_reserve_release.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.247_inventory_reserve_release.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.248_button_grant_lifecycle_columns.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.248_button_grant_lifecycle_columns.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.249_form_layout_span_and_backfill.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.249_form_layout_span_and_backfill.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.250_retire_form_adjust_tag.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.250_retire_form_adjust_tag.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.251_rename_module_id_to_m_idx.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.251_rename_module_id_to_m_idx.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.252_normalize_chooser_return_items_case.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.252_normalize_chooser_return_items_case.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.253_restore_inv_occur_in_m_last_update_by_nullable.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.253_restore_inv_occur_in_m_last_update_by_nullable.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.254_drop_layout_rows_of_invisible_fields.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.254_drop_layout_rows_of_invisible_fields.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.255_readonly_detail_reference_choosers.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.255_readonly_detail_reference_choosers.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.256_detach_orphan_layout_companions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.256_detach_orphan_layout_companions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.257_retire_orphan_invoice_and_backup_tables.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.257_retire_orphan_invoice_and_backup_tables.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.258_clear_unresolvable_virtual_expressions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.258_clear_unresolvable_virtual_expressions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.259_batch_expiry_effect_date.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.259_batch_expiry_effect_date.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.260_depot_stock_policy_expiry_mode.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.260_depot_stock_policy_expiry_mode.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.261_batch_expiry_report_and_alert_days.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.261_batch_expiry_report_and_alert_days.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.262_batch_chooser_for_outbound_lines.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.262_batch_chooser_for_outbound_lines.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.263_retire_sysdl_g_desc.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.263_retire_sysdl_g_desc.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.264_report_batch_expiry_host_module.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.264_report_batch_expiry_host_module.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.265_clear_unpublishable_module_dirty.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.265_clear_unpublishable_module_dirty.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.266_clear_dead_virtual_expressions.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.266_clear_dead_virtual_expressions.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.267_retire_datasource_sql.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.267_retire_datasource_sql.sql', GETDATE());
IF NOT EXISTS (SELECT 1 FROM dbo.ERP_SCHEMA_JOURNAL WHERE [scriptname] = N'EOS.API.Data.Migrations.268_endcase_hook_and_release_kind.sql')
    INSERT dbo.ERP_SCHEMA_JOURNAL ([scriptname], [applied]) VALUES (N'EOS.API.Data.Migrations.268_endcase_hook_and_release_kind.sql', GETDATE());
