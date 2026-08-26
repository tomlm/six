@ECHO OFF
dotnet tool uninstall -g Gix
dotnet build -c Release
dotnet pack -c Release -o nupkg
dotnet tool install -g --source nupkg Gix
