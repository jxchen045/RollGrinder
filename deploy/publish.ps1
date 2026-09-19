<#
.SYNOPSIS
    发布上位机到一个干净的目录（框架依赖式，机床工控机需装 .NET 8 Desktop Runtime）。

.PARAMETER OutputDirectory
    发布产物目录，默认 artifacts\publish。

.NOTES
    发布产物只包含程序与 config\*.sample.json 模板；
    现场的 config\*.json 与 data\ 由 upgrade.ps1 负责保留。
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot '..\src\RollGrinder.App\RollGrinder.App.csproj'

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output $OutputDirectory

Write-Host "发布完成：$OutputDirectory"
