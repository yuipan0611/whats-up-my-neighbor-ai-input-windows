using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace NekoChat.Windows;

public sealed class OutlinedCaption : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(OutlinedCaption), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
            (o, e) => AutomationProperties.SetName(o, (string)e.NewValue)));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft JhengHei, Segoe UI"), FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal),
            31.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var geometry = text.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || ActualWidth <= 8) return;
        double scale = Math.Min(1, (ActualWidth - 8) / (bounds.Width + 4));
        drawing.PushTransform(new TranslateTransform((ActualWidth - bounds.Width * scale) / 2 - bounds.Left * scale,
            (ActualHeight - bounds.Height * scale) / 2 - bounds.Top * scale));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 3.5) { LineJoin = PenLineJoin.Round }, geometry);
        drawing.Pop(); drawing.Pop();
    }
}
