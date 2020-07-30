# N26 Direct Import for YNAB

## Build instructions
1. Navigate to src/N26DirectImport
2. Replace win-x64 with desired platform runtime identifier, one of win-x64, linux-arm, osx-x64 and run:
```
dotnet tool restore
dotnet publish -r win-x64 -c release -p:PublishSingleFile=true -p:PublishTrimmed=true
```
