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
GO
