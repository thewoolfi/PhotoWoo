param([string]$OutputDirectory = 'dist\PhotoWoo-0.6.1')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\PhotoWoo\PhotoWoo.csproj'
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $PSScriptRoot $OutputDirectory }
$buildOutput = Join-Path $PSScriptRoot 'artifacts\native-build\'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:OutputPath=$buildOutput" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Сборка PhotoWoo завершилась с ошибкой.' }
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages\magick.net-q16-x64\14.16.0\Notice.txt') -Destination (Join-Path $licenses 'Magick.NET-Notice.txt')
Copy-Item -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages\magick.net.core\14.16.0\Copyright.txt') -Destination (Join-Path $licenses 'Magick.NET-Copyright.txt')
$runtimeConfig = Get-Content -LiteralPath (Join-Path $output 'PhotoWoo.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $packageName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $packagePath = Join-Path $env:USERPROFILE ".nuget\packages\$packageName\$($framework.version)"
    foreach ($licenseName in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT', 'ThirdPartyNotices.txt')) {
        $licensePath = Join-Path $packagePath $licenseName
        if (Test-Path -LiteralPath $licensePath) { Copy-Item -LiteralPath $licensePath -Destination (Join-Path $licenses "$packageName-$licenseName") }
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md')
Write-Output "Готово: $output\PhotoWoo.exe"
