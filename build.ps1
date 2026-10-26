# Builds LGBrillo.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$opts = @('/nologo', '/codepage:65001', '/target:winexe', '/optimize+', '/r:System.Web.Extensions.dll')

& $csc @opts '/out:LGBrillo.exe' LGBrillo.cs
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Start-Process .\LGBrillo.exe -ArgumentList '--make-icon', 'brillo.ico' -Wait
& $csc @opts '/out:LGBrillo.exe' '/win32icon:brillo.ico' LGBrillo.cs
exit $LASTEXITCODE

