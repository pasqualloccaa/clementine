using System.Diagnostics;
using System.Globalization;
using System.IO;
using Clementine.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Clementine.App.Notifications;

/// <summary>Un toast per batch (tag = id del batch): progresso mentre lavora, poi esito con "Mostra in cartella".</summary>
static class JobToasts
{
    public static void Init() => ToastNotificationManagerCompat.OnActivated += e =>
    {
        if (ToastArguments.Parse(e.Argument).TryGetValue("select", out var path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    };

    // Un toast cliccato ad app chiusa la rilancerebbe con un'isola nuova: meglio non lasciarne in giro.
    public static void Cleanup() => ToastNotificationManagerCompat.History.Clear();

    public static void Started(Batch batch) =>
        new ToastContentBuilder()
            .AddText($"Conversione in {batch.Op.Label}")
            .AddVisualChild(new AdaptiveProgressBar
            {
                Value = new BindableProgressBarValue("value"),
                Status = new BindableString("status"),
            })
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
        if (result.Errors.Count == 0)
            builder.AddText($"Convertit{(result.Outputs.Count == 1 ? "o" : "i")} in {result.Batch.Op.Label}")
                .AddText(string.Join("\n", result.Outputs.Select(Path.GetFileName)));
        else
            builder.AddText($"{result.Errors.Count} di {result.Batch.Inputs.Count} non convertit{(result.Errors.Count == 1 ? "o" : "i")}")
                .AddText(string.Join("\n", result.Errors));

        if (result.Outputs.Count > 0)
            builder.AddArgument("select", result.Outputs[0])
                .AddButton(new ToastButton().SetContent("Mostra in cartella").AddArgument("select", result.Outputs[0]));

        builder.Show(t => t.Tag = Tag(result.Batch));
    }

    static string Tag(Batch batch) => batch.Id.ToString(CultureInfo.InvariantCulture);

    static Windows.UI.Notifications.NotificationData Data(Batch batch, double value) => new(new Dictionary<string, string>
    {
        ["value"] = value.ToString(CultureInfo.InvariantCulture),
        ["status"] = batch.Inputs.Count == 1
            ? Path.GetFileName(batch.Inputs[0])
            : $"{Math.Min((int)(value * batch.Inputs.Count) + 1, batch.Inputs.Count)} di {batch.Inputs.Count}",
    });
}
