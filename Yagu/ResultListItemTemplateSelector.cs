using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Models;

namespace Yagu;

public sealed partial class ResultListItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupHeaderTemplate { get; set; }
    public DataTemplate? FileGroupTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => item switch
    {
        ResultGroupHeaderRow => GroupHeaderTemplate,
        FileGroup => FileGroupTemplate,
        _ => base.SelectTemplateCore(item, container),
    };
}