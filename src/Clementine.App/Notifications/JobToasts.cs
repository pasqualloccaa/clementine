using System.Diagnostics;
using System.Globalization;
using System.IO;
using Clementine.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Clementine.App.Notifications;

/// <summary>Un toast per batch (tag = id del batch): progresso con "Annulla" mentre lavora, poi esito con "Mostra in cartella".</summary>
static class JobToasts
{
    public static void Init(Action<int> cancel, Action<string> share) => ToastNotificationManagerCompat.OnActivated += e =>
    {
        var args = ToastArguments.Parse(e.Argument);
        if (args.TryGetValue("select", out var path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        if (args.TryGetValue("cancel", out var id) && int.TryParse(id, CultureInfo.InvariantCulture, out var batchId)) cancel(batchId);
        if (args.TryGetValue("share", out var shareId)) share(shareId);
    };

    /// <summary>Secondo passo di "Condividi": il clic (sulla notifica o sul pulsante) porta Clementine in primo piano.</summary>
    public static void ShareReady(string id, string what) =>
        new ToastContentBuilder()
            .AddText("Pronto per condividere")
            .AddText($"{what}: clicca qui per aprire Condividi")
            .AddArgument("share", id)
            .AddButton(new ToastButton().SetContent("Condividi").AddArgument("share", id))
            .Show();

    // Un toast cliccato ad app chiusa la rilancerebbe con un'isola nuova: meglio non lasciarne in giro.
    public static void Cleanup() => ToastNotificationManagerCompat.History.Clear();

    public static void Started(Batch batch) =>
        new ToastContentBuilder()
            .AddText(Title(batch.Op))
            .AddVisualChild(new AdaptiveProgressBar
            {
                Value = new BindableProgressBarValue("value"),
                Status = new BindableString("status"),
            })
            .AddButton(new ToastButton().SetContent("Annulla").AddArgument("cancel", Tag(batch)))
            .Show(t =>
            {
                t.Tag = Tag(batch);
                t.Data = Data(batch, 0);
            });

    public static void Progress(Batch batch, double value) =>
        ToastNotificationManagerCompat.CreateToastNotifier().Update(Data(batch, value), Tag(batch));

    public static void Completed(BatchResult result)
    {
        var builder = new ToastContentBuilder();
        if (result.Cancelled)
            builder.AddText($"{Title(result.Batch.Op)}: annullato")
                .AddText(result.Outputs.Count > 0 ? $"Già completati: {string.Join(", ", result.Outputs.Select(Path.GetFileName))}" : "Nessun file creato");
        else if (result.Errors.Count == 0)
            builder.AddText($"{Title(result.Batch.Op)}: fatto")
                .AddText(string.Join("\n", result.Outputs.Select(Path.GetFileName)));
        else
            builder.AddText($"{result.Errors.Count} di {result.Batch.Inputs.Count} non riuscit{(result.Errors.Count == 1 ? "o" : "i")}")
                .AddText(string.Join("\n", result.Errors));

        if (result.Outputs.Count > 0) AddShowInFolder(builder, result.Outputs[0]);
        builder.Show(t => t.Tag = Tag(result.Batch));
    }

    /// <summary>Esito di un'azione dell'anello Alt.</summary>
    public static void Info(string title, string text, string? select = null)
    {
        var builder = new ToastContentBuilder().AddText(title).AddText(text);
        if (select is not null) AddShowInFolder(builder, select);
        builder.Show();
    }

    static void AddShowInFolder(ToastContentBuilder builder, string path) =>
        builder.AddArgument("select", path)
            .AddButton(new ToastButton().SetContent("Mostra in cartella").AddArgument("select", path));

    static string Title(Operation op) => op.Id.StartsWith("convert:") ? $"Conversione in {op.Label}" : op.Label;

    static string Tag(Batch batch) => batch.Id.ToString(CultureInfo.InvariantCulture);

    static Windows.UI.Notifications.NotificationData Data(Batch batch, double value) => new(new Dictionary<string, string>
    {
        ["value"] = value.ToString(CultureInfo.InvariantCulture),
        ["status"] = batch.Inputs.Count == 1 || batch.Op.Combines
            ? Path.GetFileName(batch.Inputs[0])
            : $"{Math.Min((int)(value * batch.Inputs.Count) + 1, batch.Inputs.Count)} di {batch.Inputs.Count}",
    });
}
