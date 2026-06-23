param(
    [string]$ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;",
    [string]$DatabaseName = "SignatureVerification",
    [string]$MigrationsFolder = (Join-Path (Split-Path $PSScriptRoot -Parent) "database"),
    [string]$SchemaPath = ""
)

$ErrorActionPreference = "Stop"

function New-SqlConnection([string]$connectionString) {
    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery([System.Data.SqlClient.SqlConnection]$connection, [string]$sql) {
    if ([string]::IsNullOrWhiteSpace($sql)) {
        return
    }

    $command = $connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()
}

function Split-SqlBatches([string]$sql) {
    return [regex]::Split($sql, "(?im)^\s*GO\s*;?\s*$")
}

if (-not (Test-Path -LiteralPath $MigrationsFolder)) {
    throw "Migrations folder was not found: $MigrationsFolder"
}

$master = New-SqlConnection $ConnectionString
try {
    $dbNameEscaped = $DatabaseName.Replace("]", "]]")
    Invoke-SqlNonQuery $master "IF DB_ID(N'$($DatabaseName.Replace("'", "''"))') IS NULL CREATE DATABASE [$dbNameEscaped];"
}
finally {
    $master.Close()
}

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
$builder["Initial Catalog"] = $DatabaseName
$databaseConnection = New-SqlConnection $builder.ConnectionString
try {
    Invoke-SqlNonQuery $databaseConnection "IF SCHEMA_ID(N'ssv') IS NULL EXEC(N'CREATE SCHEMA ssv');"
    Invoke-SqlNonQuery $databaseConnection @"
IF OBJECT_ID(N'ssv.SchemaMigration', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.SchemaMigration
    (
        MigrationId nvarchar(260) NOT NULL CONSTRAINT PK_SchemaMigration PRIMARY KEY,
        FileName nvarchar(260) NOT NULL,
        AppliedUtc datetimeoffset(7) NOT NULL CONSTRAINT DF_SchemaMigration_AppliedUtc DEFAULT(sysutcdatetime())
    );
END
"@

    $existingCoreCheck = $databaseConnection.CreateCommand()
    $existingCoreCheck.CommandText = "SELECT COUNT(1) FROM sys.tables t INNER JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'ssv' AND t.name = N'VerificationDocument';"
    if ([int]$existingCoreCheck.ExecuteScalar() -gt 0) {
        Invoke-SqlNonQuery $databaseConnection @"
IF NOT EXISTS (SELECT 1 FROM ssv.SchemaMigration WHERE MigrationId = N'001_initial_schema')
BEGIN
    INSERT INTO ssv.SchemaMigration(MigrationId, FileName)
    VALUES(N'001_initial_schema', N'001_initial_schema.sql');
END
"@
    }

    $migrationFiles = if ([string]::IsNullOrWhiteSpace($SchemaPath)) {
        Get-ChildItem -LiteralPath $MigrationsFolder -Filter "*.sql" | Sort-Object Name
    }
    else {
        if (-not (Test-Path -LiteralPath $SchemaPath)) {
            throw "Schema file was not found: $SchemaPath"
        }

        Get-Item -LiteralPath $SchemaPath
    }

    $applied = 0
    foreach ($migrationFile in $migrationFiles) {
        $migrationId = [System.IO.Path]::GetFileNameWithoutExtension($migrationFile.Name)
        $check = $databaseConnection.CreateCommand()
        $check.CommandText = "SELECT COUNT(1) FROM ssv.SchemaMigration WHERE MigrationId = @MigrationId;"
        [void]$check.Parameters.Add("@MigrationId", [System.Data.SqlDbType]::NVarChar, 260)
        $check.Parameters["@MigrationId"].Value = $migrationId
        if ([int]$check.ExecuteScalar() -gt 0) {
            Write-Host "Skipping applied migration: $migrationId"
            continue
        }

        Write-Host "Applying migration: $migrationId"
        $schema = Get-Content -LiteralPath $migrationFile.FullName -Raw
        foreach ($batch in Split-SqlBatches $schema) {
            Invoke-SqlNonQuery $databaseConnection $batch
        }

        $record = $databaseConnection.CreateCommand()
        $record.CommandText = "INSERT INTO ssv.SchemaMigration(MigrationId, FileName) VALUES(@MigrationId, @FileName);"
        [void]$record.Parameters.Add("@MigrationId", [System.Data.SqlDbType]::NVarChar, 260)
        [void]$record.Parameters.Add("@FileName", [System.Data.SqlDbType]::NVarChar, 260)
        $record.Parameters["@MigrationId"].Value = $migrationId
        $record.Parameters["@FileName"].Value = $migrationFile.Name
        [void]$record.ExecuteNonQuery()
        $applied++
    }

    Write-Host "Database migrations complete: $DatabaseName"
    Write-Host "Migrations applied this run: $applied"
}
finally {
    $databaseConnection.Close()
}
