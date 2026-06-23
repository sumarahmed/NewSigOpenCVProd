param(
    [string]$InstallRoot = "$env:ProgramData\Tungsten\StaticSignatureVerification",
    [string]$RuntimeRoot = "C:\Temp\SignatureVerification",
    [string]$ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;",
    [string]$DatabaseName = "SignatureVerification",
    [ValidateSet("Hybrid", "Database")]
    [string]$StorageMode = "Database",
    [string]$ApiKey = "",
    [string]$Urls = "http://127.0.0.1:5117",
    [switch]$CreateStartupTask,
    [switch]$InstallMissingDependencies,
    [switch]$InstallGhostscript,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Test-Command([string]$name) {
    return $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

function New-ApiKey {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

function Invoke-WingetInstall([string]$id, [string]$name) {
    if (-not $InstallMissingDependencies) {
        throw "$name is required. Install it first or rerun with -InstallMissingDependencies."
    }

    if (-not (Test-Command "winget")) {
        throw "winget is not available. Install $name manually."
    }

    Write-Host "Installing $name with winget package $id..."
    & winget install --id $id --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $name."
    }
}

function Assert-DependencyState {
    Write-Step "Checking dependencies"

    if (-not (Test-Command "dotnet")) {
        Invoke-WingetInstall "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
    }

    $dotnetInfo = & dotnet --info
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet is installed but failed to run."
    }

    if ($dotnetInfo -notmatch "Microsoft\.NETCore\.App 8\.") {
        Invoke-WingetInstall "Microsoft.DotNet.Runtime.8" ".NET 8 runtime"
    }

    if ($dotnetInfo -notmatch "Microsoft\.AspNetCore\.App 8\.") {
        Invoke-WingetInstall "Microsoft.DotNet.AspNetCore.8" "ASP.NET Core 8 runtime"
    }

    if (-not $SkipPublish) {
        $sdks = & dotnet --list-sdks
        if ($LASTEXITCODE -ne 0 -or $sdks -notmatch "^8\.") {
            Invoke-WingetInstall "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
        }
    }

    if ($ConnectionString -match "\(localdb\)" -and -not (Test-Command "sqllocaldb")) {
        Invoke-WingetInstall "Microsoft.SQLServer.2022.LocalDB" "SQL Server LocalDB"
    }

    $ghostscript = Find-Ghostscript
    if ([string]::IsNullOrWhiteSpace($ghostscript)) {
        if ($InstallGhostscript) {
            Invoke-WingetInstall "ArtifexSoftware.Ghostscript" "Ghostscript"
            $ghostscript = Find-Ghostscript
        }
        else {
            Write-Warning "Ghostscript was not found. PDF rendering requires an installed Ghostscript executable or an approved replacement renderer."
        }
    }
    else {
        Write-Host "Ghostscript found: $ghostscript"
    }
}

function Find-Ghostscript {
    $roots = @(
        (Join-Path $env:ProgramFiles "gs"),
        (Join-Path ${env:ProgramFiles(x86)} "gs")
    )

    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            $match = Get-ChildItem -LiteralPath $root -Recurse -Include gswin64c.exe,gswin32c.exe -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($match) {
                return $match.FullName
            }
        }
    }

    return ""
}

function Protect-SecretFile([string]$path) {
    try {
        $acl = Get-Acl -LiteralPath $path
        $acl.SetAccessRuleProtection($true, $false)
        $admins = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
        $system = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
        $current = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($sid in @($admins, $system, $current)) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, "FullControl", "Allow")
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    catch {
        Write-Warning "Could not lock down secret file ACL: $($_.Exception.Message)"
    }
}

function New-AppSettings([string]$apiPublishFolder, [string]$databaseConnection, [string]$apiKeyConfig) {
    $settings = [ordered]@{
        SignatureVerification = [ordered]@{
            ConnectionString = $databaseConnection
            ApiKeys = $apiKeyConfig
        }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = "Information"
                "Microsoft.AspNetCore" = "Warning"
            }
        }
    }

    $path = Join-Path $apiPublishFolder "appsettings.Production.json"
    $settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
}

function New-Launcher([string]$scriptsFolder, [string]$apiFolder, [string]$opsFolder, [string]$apiKeyConfig, [string]$databaseConnection, [string]$runtimeRoot, [string]$urls) {
    $launcher = Join-Path $scriptsFolder "Start-SignatureVerificationApi.ps1"
    $content = @"
`$ErrorActionPreference = "Stop"
`$env:ASPNETCORE_ENVIRONMENT = "Production"
`$env:SIGNATURE_API_KEYS = "$apiKeyConfig"
`$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "$databaseConnection"
`$env:SIGNATURE_VERIFICATION_ROOT = "$runtimeRoot"
Set-Location "$apiFolder"
& dotnet "$apiFolder\StaticSignatureVerification.Api.dll" --urls "$urls"
"@
    Set-Content -LiteralPath $launcher -Value $content -Encoding UTF8

    $opsLauncher = Join-Path $scriptsFolder "Run-Operations.ps1"
    $opsContent = @"
param([Parameter(ValueFromRemainingArguments=`$true)][string[]]`$Arguments)
`$ErrorActionPreference = "Stop"
`$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "$databaseConnection"
`$env:SIGNATURE_VERIFICATION_ROOT = "$runtimeRoot"
& dotnet "$opsFolder\StaticSignatureVerification.Operations.dll" @Arguments
"@
    Set-Content -LiteralPath $opsLauncher -Value $opsContent -Encoding UTF8

    return $launcher
}

function Register-StartupTask([string]$launcher, [string]$installRoot) {
    Write-Step "Registering startup task"
    $taskName = "StaticSignatureVerificationApi"
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installRoot "startup-task.txt") -Value $taskName -Encoding UTF8
    Write-Host "Startup task registered: $taskName"
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $InstallRoot "app"
$apiPublish = Join-Path $publishRoot "api"
$opsPublish = Join-Path $publishRoot "operations"
$scriptsInstall = Join-Path $InstallRoot "scripts"
$configFolder = Join-Path $InstallRoot "config"
$logsFolder = Join-Path $InstallRoot "logs"
$secretFolder = Join-Path $InstallRoot "secrets"
$databaseConnection = if ($ConnectionString -match "Initial Catalog") { $ConnectionString } else { "$ConnectionString;Initial Catalog=$DatabaseName" }
$apiKeyValue = if ([string]::IsNullOrWhiteSpace($ApiKey)) { New-ApiKey } else { $ApiKey }
$apiKeyConfig = "$apiKeyValue`:Administrator,ReferenceApprover,Reviewer,Auditor,Verifier"

Write-Step "Preparing install folders"
foreach ($folder in @($InstallRoot, $publishRoot, $apiPublish, $opsPublish, $scriptsInstall, $configFolder, $logsFolder, $secretFolder, $RuntimeRoot)) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

Assert-DependencyState

Write-Step "Publishing application"
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repoRoot "StaticSignatureVerification.Api\StaticSignatureVerification.Api.csproj") -c Release -o $apiPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

    & dotnet publish (Join-Path $repoRoot "StaticSignatureVerification.Operations\StaticSignatureVerification.Operations.csproj") -c Release -o $opsPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Operations publish failed." }
}

Write-Step "Creating runtime configuration"
New-AppSettings $apiPublish $databaseConnection $apiKeyConfig
$secretFile = Join-Path $secretFolder "api-key.txt"
Set-Content -LiteralPath $secretFile -Value $apiKeyValue -Encoding UTF8
Protect-SecretFile $secretFile

Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\Initialize-Database.ps1") -Destination $scriptsInstall -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "database") -Destination (Join-Path $InstallRoot "database") -Recurse -Force

Write-Step "Installing database schema"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts\Initialize-Database.ps1") `
    -ConnectionString $ConnectionString `
    -DatabaseName $DatabaseName `
    -MigrationsFolder (Join-Path $repoRoot "database")
if ($LASTEXITCODE -ne 0) { throw "Database initialization failed." }

Write-Step "Running setup wizard"
& dotnet (Join-Path $opsPublish "StaticSignatureVerification.Operations.dll") setup-wizard `
    --root $RuntimeRoot `
    --connectionString $databaseConnection `
    --storageMode $StorageMode
if ($LASTEXITCODE -ne 0) { throw "Operations setup failed." }

$launcher = New-Launcher $scriptsInstall $apiPublish $opsPublish $apiKeyConfig $databaseConnection $RuntimeRoot $Urls

if ($CreateStartupTask) {
    Register-StartupTask $launcher $InstallRoot
}

Write-Step "Installation complete"
Write-Host "Install root: $InstallRoot"
Write-Host "Runtime root: $RuntimeRoot"
Write-Host "API URL: $Urls"
Write-Host "Admin UI: $Urls/admin"
Write-Host "Operations launcher: $(Join-Path $scriptsInstall "Run-Operations.ps1")"
Write-Host "API key file: $secretFile"
Write-Host ""
Write-Host "Start API manually with:"
Write-Host "powershell -ExecutionPolicy Bypass -File `"$launcher`""
