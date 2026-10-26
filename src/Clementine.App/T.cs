using System.Windows.Markup;
using Clementine.Core;

namespace Clementine.App;

/// <summary>Testo nelle due lingue dentro lo XAML: Content="{app:T 'Annulla', 'Cancel'}" (vedi Lang).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string italian, string english) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(italian, english);
}
