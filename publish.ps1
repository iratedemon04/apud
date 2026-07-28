# Apud release build: self-contained, no .NET install needed on the target machine.
# Output: publish\Apud\Apud.exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet test -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed - publish aborted." }

dotnet publish src\Apud.App -c Release -r win-x64 --self-contained true -o publish\Apud
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host ""
Write-Host "Done: $PSScriptRoot\publish\Apud\Apud.exe"
