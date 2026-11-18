@echo off
rem Builds Snip.exe with the C# compiler that comes with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:Snip.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Snip.cs
