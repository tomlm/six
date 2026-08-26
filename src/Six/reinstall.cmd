@ECHO OFF
dotnet tool uninstall -g SixelViewer
dotnet build -c Release
dotnet pack -c Release -o nupkg
dotnet tool install -g SixelViewer
