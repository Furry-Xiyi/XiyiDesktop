using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace XiyiDesktop.Widgets.UI;

public sealed partial class WidgetPickerPageControl : UserControl
{
    public static readonly DependencyProperty EntryObjectProperty = DependencyProperty.Register(
        nameof(EntryObject), typeof(object), typeof(WidgetPickerPageControl), new PropertyMetadata(null, EntryObject_Changed));

    public WidgetPickerPageControl()
    {
        InitializeComponent();
    }

    public object? EntryObject
    {
        get => GetValue(EntryObjectProperty);
        set => SetValue(EntryObjectProperty, value);
    }

    internal WidgetPickerEntry? Entry { get; private set; }

    private static void EntryObject_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WidgetPickerPageControl page && args.NewValue is WidgetPickerEntry entry)
            page.Initialize(entry);
    }

    internal void Initialize(WidgetPickerEntry entry)
    {
        if (Entry is not null)
            Entry.PropertyChanged -= Entry_PropertyChanged;

        Entry = entry;
        Entry.PropertyChanged += Entry_PropertyChanged;
        SetPreview(entry.PreviewSource);
    }

    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WidgetPickerEntry.PreviewSource))
            SetPreview(Entry?.PreviewSource);
    }

    private void SetPreview(ImageSource? source)
    {
        WidgetPreviewImage.Source = source;
        WidgetPreviewFallbackText.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
