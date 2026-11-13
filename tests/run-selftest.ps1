# Builds and runs the developer self-test against the first connected phone. Run build.ps1 first.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Get-ChildItem (Join-Path $root "src") -Filter *.cs | Where-Object { $_.Name -ne "Program.cs" } | ForEach-Object { $_.FullName }
$out = Join-Path $root "build\selftest.exe"
& $csc /nologo /target:exe /unsafe /optimize+ /out:$out "/resource:$root\build\mirrordock-server.jar,mirrordock-server.jar" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:Microsoft.CSharp.dll `
    @src (Join-Path $PSScriptRoot "SelfTest.cs")
if ($LASTEXITCODE -ne 0) { throw "self-test build failed" }
& $out (Join-Path $root "dist\adb\adb.exe")
exit $LASTEXITCODE
