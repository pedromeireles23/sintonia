using System.Text;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.WriteLine("Sintonia · diagnóstico limitado de instalações\nNenhuma inferência será iniciada. Não confirma login, quota ou integração real.\n");
try
{
    var probe = new ProviderInstallationProbe();
    var reports = await Task.WhenAll(Enum.GetValues<ProviderKind>().Select(provider =>
        probe.InspectAsync(provider, Environment.CurrentDirectory, cancellation.Token)));
    foreach (var report in reports)
    {
        var status = report.Status switch { InstallationStatus.Detected => "Detectado", InstallationStatus.Missing => "Ausente", InstallationStatus.Unsupported => "Instalação não suportada", _ => "Falha no diagnóstico" };
        Console.WriteLine($"{report.Provider}: {status} · {report.Version ?? "versão indisponível"}");
        if (report.InstallationKind is { } kind) Console.WriteLine($"  Instalação: {kind}");
        if (report.AdvertisedOptions.Count > 0) Console.WriteLine($"  Opções anunciadas na ajuda: {string.Join(", ", report.AdvertisedOptions)}");
        Console.WriteLine($"  {report.Diagnostic}\n");
    }
    return reports.All(r => r.Status == InstallationStatus.Detected) ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Diagnóstico cancelado ou prazo excedido."); return 2; }
