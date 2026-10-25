using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RoomOps.Desktop;

/// <summary>Branded start-up window shown while the backend and WebView2 initialize.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                var animation = new DoubleAnimation(-120, 400, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                ProgressShift.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                ProgressShift.X = 0;
            }
        };
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
