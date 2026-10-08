@echo off
REM Gera UM unico .exe que roda em qualquer Windows 10/11 64 bits (nao precisa instalar o .NET)
REM Precisa do .NET 8 SDK so no PC onde voce compila.
dotnet publish -c Release -o publish
echo.
echo Pronto! Envie so este arquivo: publish\AzulGroove.exe
pause
