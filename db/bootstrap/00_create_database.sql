/*
 * 建库入口
 *
 * 数据库名与连接串 Initial Catalog 一致（见 EOS.API/appsettings.Development.example.json）。
 * 中文环境建议使用中文排序规则；实例未安装时可改 SQL_Latin1_General_CP1_CI_AS。
 * 完整初始化步骤见 db/README.md。
 */

USE [master];
GO

IF DB_ID(N'EOS.ERP') IS NULL
BEGIN
    CREATE DATABASE [EOS.ERP] COLLATE Chinese_PRC_CI_AS;
END
GO

USE [EOS.ERP];
GO
