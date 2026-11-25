[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$sdkVersion = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'global.json') | ConvertFrom-Json).sdk.version
$app = 'src/SsdGuardian.App/SsdGuardian.App.csproj'
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot $app))
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$archiveName = "SSD-Guardian-v$version-win-x64.zip"
$tests = @(
    'tests/SsdGuardian.Tests/SsdGuardian.Tests.csproj',
    'tests/SsdGuardian.Windows.Tests/SsdGuardian.Windows.Tests.csproj'
)

function Invoke-Dotnet {
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Dotnet @args 2>&1 | ForEach-Object { $_.ToString() }
    } finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)."
    }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    if ((Invoke-Dotnet --version) -ne $sdkVersion) {
        throw "The pinned .NET SDK $sdkVersion is required."
    }

    $output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
    $testLog = Join-Path $output "test-results-v$version.txt"
    Set-Content -LiteralPath $testLog -Value "SSD Guardian v$version | Release | .NET SDK $sdkVersion" -Encoding UTF8
    foreach ($test in $tests) {
        Invoke-Dotnet restore $test --locked-mode
        Invoke-Dotnet run --project $test --configuration Release --no-restore | ForEach-Object {
            Add-Content -LiteralPath $testLog -Value $_ -Encoding UTF8
            Write-Output $_
        }
    }

    $staging = (New-Item -ItemType Directory -Path ([IO.Path]::Combine($output, [Guid]::NewGuid().ToString('N')))).FullName
    $temporaryZip = Join-Path $staging $archiveName
    $publish = Join-Path $staging 'SSD Guardian'
    try {
        Invoke-Dotnet restore $app --locked-mode
        Invoke-Dotnet publish $app --configuration Release --no-restore --output $publish
        Copy-Item -LiteralPath 'README.md', 'README.ru.md', 'LICENSE' -Destination $publish
        Copy-Item -LiteralPath 'docs' -Destination $publish -Recurse
        $notices = New-Item -ItemType Directory -Path (Join-Path $publish 'licenses')
        Copy-Item -LiteralPath 'licenses/TraceEvent.txt' -Destination $notices.FullName
        $sdkLine = Invoke-Dotnet --list-sdks | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1
        if ($sdkLine -notmatch '\[(.+)\]$') { throw 'Cannot locate SDK distribution notices.' }
        $dotnetDirectory = Split-Path -Parent $Matches[1]
        Copy-Item -LiteralPath (Join-Path $dotnetDirectory 'LICENSE.txt') -Destination (Join-Path $notices.FullName 'dotnet-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $dotnetDirectory 'ThirdPartyNotices.txt') -Destination $notices.FullName

        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            [string[]]$files = [IO.Directory]::GetFiles($publish, '*', [IO.SearchOption]::AllDirectories)
            [Array]::Sort($files, [StringComparer]::Ordinal)
            foreach ($file in $files) {
                $entryName = 'SSD Guardian/' + $file.Substring($publish.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTime
                $inputFile = [IO.File]::OpenRead($file)
                try {
                    $entryStream = $entry.Open()
                    try { $inputFile.CopyTo($entryStream) }
                    finally { $entryStream.Dispose() }
                } finally {
                    $inputFile.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }

        $archive = Join-Path $output $archiveName
        Move-Item -LiteralPath $temporaryZip -Destination $archive -Force
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $archiveName" -Encoding ASCII
        Write-Output $archive
    } finally {
        if ($staging -and (Test-Path -LiteralPath $staging -PathType Container)) {
            $resolvedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
            if ((Split-Path -Parent $resolvedStaging) -eq $output -and (Split-Path -Leaf $resolvedStaging) -match '^[a-f0-9]{32}$') {
                Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
            }
        }
    }
} finally {
    Pop-Location
}
