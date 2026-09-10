# Domain Site Deployment Manager

WPF-приложение для Windows на .NET 8.

## Требования

- Windows x64
- .NET 8 SDK

## Проверка сборки

dotnet restore .\TextFileProcessor.csproj
dotnet build .\TextFileProcessor.csproj -c Release -r win-x64

## Публикация

dotnet publish .\TextFileProcessor.csproj -c Release -r win-x64 --self-contained true

Результат находится в каталоге:

bin\Release\net8.0-windows\win-x64\publish
