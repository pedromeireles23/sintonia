param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [string]$Executable = '')
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$executablePath = Join-Path $workspacePath "src/Sintonia.Desktop/bin/$Configuration/net10.0-windows/Sintonia.Desktop.exe"
if ($Executable) { $executablePath = [IO.Path]::GetFullPath($Executable) }
$appProcess = Start-Process -FilePath $executablePath -WorkingDirectory $workspacePath -PassThru -WindowStyle Hidden
try {
    if (-not $appProcess.WaitForInputIdle(10000)) { throw 'O aplicativo não ficou pronto no prazo.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw "O aplicativo encerrou antes de abrir a janela: $($appProcess.ExitCode)" }
        if ($appProcess.MainWindowHandle -ne 0) { break }
        [Threading.Thread]::Sleep(100)
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($appProcess.MainWindowHandle -eq 0 -or $appProcess.MainWindowTitle -ne 'Sintonia — Central de projetos') {
        throw 'A janela esperada não foi encontrada.'
    }
    Write-Output "PASS: executável normal abriu a janela: $($appProcess.MainWindowTitle)"
    if (-not $appProcess.CloseMainWindow() -or -not $appProcess.WaitForExit(5000)) {
        throw 'A janela não encerrou normalmente.'
    }
    if ($appProcess.ExitCode -ne 0) { throw "Falha no encerramento: $($appProcess.ExitCode)" }
}
finally {
    $appProcess.Refresh()
    if (-not $appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
    $appProcess.Dispose()
}
