BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021133557_AddInfrastructureSources'
)
BEGIN
    CREATE TABLE [InfrastructureSources] (
        [Id] int NOT NULL IDENTITY,
        [DisplayName] nvarchar(100) NOT NULL,
        [Provider] nvarchar(20) NOT NULL,
        [Destination] nvarchar(400) NOT NULL,
        [CredentialReference] nvarchar(64) NOT NULL,
        [Enabled] bit NOT NULL,
        [ConnectionStatus] nvarchar(20) NOT NULL,
        [ConnectionMessage] nvarchar(200) NOT NULL,
        [LastCheckedAt] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_InfrastructureSources] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021133557_AddInfrastructureSources'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InfrastructureSources_Provider_Destination] ON [InfrastructureSources] ([Provider], [Destination]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021133557_AddInfrastructureSources'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021133557_AddInfrastructureSources', N'10.0.0');
END;

COMMIT;
GO
