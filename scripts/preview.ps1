Get-Process OwOWinDeployer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet build src/OwOWinDeployer.App/OwOWinDeployer.App.csproj -c Debug
if ($LASTEXITCODE -eq 0) {
    Start-Process "src\OwOWinDeployer.App\bin\Debug\net10.0-windows10.0.19041.0\OwOWinDeployer.exe"
}
