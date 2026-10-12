@ECHO OFF
dotnet tool uninstall -g Six
dotnet build -c Release
dotnet pack -c Release -o nupkg
dotnet tool install -g --source nupkg Six
