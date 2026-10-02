IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    CREATE TABLE [Servers] (
        [Id] int NOT NULL IDENTITY,
        [DisplayName] nvarchar(100) NOT NULL,
        [HostNameOrIp] nvarchar(253) NOT NULL,
        [Environment] nvarchar(30) NOT NULL,
        [OperatingSystem] nvarchar(200) NOT NULL,
        [ConnectionType] nvarchar(30) NOT NULL,
        [WinRmPort] int NOT NULL,
        [UseSsl] bit NOT NULL,
        [Username] nvarchar(150) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [LastConnectionStatus] nvarchar(20) NOT NULL,
        [LastCheckedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Servers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    CREATE TABLE [Deployments] (
        [Id] uniqueidentifier NOT NULL,
        [ServerId] int NOT NULL,
        [ApplicationName] nvarchar(80) NOT NULL,
        [Environment] nvarchar(30) NOT NULL,
        [SiteName] nvarchar(80) NOT NULL,
        [AppPoolName] nvarchar(80) NULL,
        [SourcePath] nvarchar(500) NOT NULL,
        [TargetPath] nvarchar(500) NOT NULL,
        [BackupPath] nvarchar(500) NOT NULL,
        [StartedAt] datetimeoffset NOT NULL,
        [CompletedAt] datetimeoffset NULL,
        [Status] nvarchar(20) NOT NULL,
        [ResultMessage] nvarchar(2000) NOT NULL,
        CONSTRAINT [PK_Deployments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Deployments_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Deployments_ServerId] ON [Deployments] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Deployments_StartedAt] ON [Deployments] ([StartedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Servers_HostNameOrIp_WinRmPort] ON [Servers] ([HostNameOrIp], [WinRmPort]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915121137_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915121137_InitialCreate', N'10.0.0');
END;

COMMIT;
GO

