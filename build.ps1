Push-Location $PSScriptRoot
try {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:MouseNudge.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MouseNudge.cs
    if ($LASTEXITCODE -eq 0) { .\MouseNudge.exe --self-test | Write-Output }
}
finally {
    Pop-Location
}
exit $LASTEXITCODE
