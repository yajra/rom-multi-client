using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ROInstances;

internal static class LauncherTheme
{
    public static LinearGradientBrush Surface(string top = "#1A2B48", string bottom = "#111D35") => new(
        (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);

    public static FrameworkElement Icon(string kind, double size = 32)
    {
        var geometry = kind switch
        {
            "play" => "M5,3 L22,14 L5,25 Z",
            "cpu" => "M7,7 H23 V23 H7 Z M11,11 H19 V19 H11 Z M10,2 V7 M16,2 V7 M22,2 V7 M10,23 V28 M16,23 V28 M22,23 V28 M2,10 H7 M2,16 H7 M2,22 H7 M23,10 H28 M23,16 H28 M23,22 H28",
            "ram" => "M2,7 H30 V24 H2 Z M7,11 V18 M13,11 V18 M19,11 V18 M25,11 V18 M6,24 V28 M12,24 V28 M18,24 V28 M24,24 V28",
            "layers" => "M2,10 L16,2 L30,10 L16,18 Z M2,17 L16,25 L30,17 M2,24 L16,32 L30,24",
            _ => "M6,16 L12,22 L25,8"
        };
        return new Viewbox { Width = size, Height = size, Child = new Path {
            Data = Geometry.Parse(geometry), Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind == "play" ? "#74F8B0" : "#A9C8EC")),
            Fill = kind == "play" ? new SolidColorBrush(Color.FromRgb(116,248,176)) : Brushes.Transparent,
            StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        } };
    }

    public static Style CheckBoxStyle => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="CheckBox">
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <StackPanel Orientation="Horizontal">
                  <Border x:Name="Box" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="21" Height="21" CornerRadius="5" BorderThickness="1.5" BorderBrush="#B6C6DD" Background="#0D172C" VerticalAlignment="Center">
                    <TextBlock x:Name="Mark" Text="&#x2713;" Foreground="#08251C" FontWeight="Bold" FontSize="17" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                  </Border>
                  <ContentPresenter Margin="10,0,0,0" VerticalAlignment="Center"/>
                </StackPanel>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Box" Property="Background" Value="#74F8B0"/>
                    <Setter TargetName="Box" Property="BorderBrush" Value="#74F8B0"/>
                    <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsChecked" Value="{x:Null}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <Setter TargetName="Mark" Property="Text" Value="&#x2212;"/>
                    <Setter TargetName="Mark" Property="Foreground" Value="#74F8B0"/>
                    <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="White"/><Setter TargetName="Box" Property="BorderThickness" Value="2.5"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
