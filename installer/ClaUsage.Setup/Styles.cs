using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaUsage.Setup;

/// <summary>The app's colors (UI/Theme.cs) and control looks, so the setup reads as part of ClaUsage.</summary>
public static class Styles
{
    public static bool Dark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static ResourceDictionary Create(bool dark)
    {
        var resources = (ResourceDictionary)XamlReader.Parse(Xaml);
        void Set(string name, string light, string darkHex) =>
            resources[name] = Brush(dark ? darkHex : light);
        Set("BgBrush", "#F7F7F9", "#1C1C1E");
        Set("CardBrush", "#FFFFFF", "#2A2A2D");
        Set("BorderBrush", "#E2E2E6", "#3A3A3E");
        Set("TextBrush", "#1C1C1E", "#F2F2F4");
        Set("MutedBrush", "#6E6E76", "#9A9AA2");
        Set("TrackBrush", "#E6E6EA", "#3C3C42");
        Set("AccentBrush", "#2F6FEB", "#5EA2FF");
        Set("AccentHoverBrush", "#2560D4", "#7AB3FF");
        Set("OnAccentBrush", "#FFFFFF", "#0B1A33");
        Set("ButtonBrush", "#ECECF0", "#34343A");
        Set("ButtonHoverBrush", "#E2E2E8", "#3E3E45");
        Set("CriticalBrush", "#E0382F", "#FF5B57");
        Set("OkBrush", "#1FA463", "#34C77B");
        return resources;
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private const string Xaml = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

      <Style x:Key="Button" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
        <Setter Property="Background" Value="{DynamicResource ButtonBrush}" />
        <Setter Property="Padding" Value="18,7" />
        <Setter Property="MinWidth" Value="96" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="Box" Background="{TemplateBinding Background}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Box" Property="Background" Value="{DynamicResource ButtonHoverBrush}" />
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True">
                  <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                  <Setter TargetName="Box" Property="BorderThickness" Value="1" />
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="Box" Property="Opacity" Value="0.5" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource Button}">
        <Setter Property="Foreground" Value="{DynamicResource OnAccentBrush}" />
        <Setter Property="Background" Value="{DynamicResource AccentBrush}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="Box" Background="{TemplateBinding Background}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentHoverBrush}" />
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="Box" Property="Opacity" Value="0.5" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style x:Key="CloseButton" TargetType="Button">
        <Setter Property="Width" Value="32" />
        <Setter Property="Height" Value="28" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="Box" Background="Transparent" CornerRadius="6">
                <Path Data="M0,0 L10,10 M10,0 L0,10" Stroke="{DynamicResource MutedBrush}" StrokeThickness="1.3"
                      HorizontalAlignment="Center" VerticalAlignment="Center" />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Box" Property="Background" Value="{DynamicResource ButtonHoverBrush}" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style x:Key="Check" TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Margin" Value="0,0,0,10" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="CheckBox">
              <StackPanel Orientation="Horizontal" Background="Transparent">
                <Border x:Name="Box" Width="18" Height="18" CornerRadius="5" BorderThickness="1.5"
                        BorderBrush="{DynamicResource MutedBrush}" Background="Transparent" VerticalAlignment="Center">
                  <Path x:Name="Mark" Data="M3.5,9 L7.5,12.5 L14,5.5" Stroke="{DynamicResource OnAccentBrush}" StrokeThickness="2"
                        StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"
                        Margin="-1.5" />
                </Border>
                <ContentPresenter Margin="10,0,0,0" VerticalAlignment="Center" />
              </StackPanel>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentBrush}" />
                  <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                  <Setter TargetName="Mark" Property="Visibility" Value="Visible" />
                </Trigger>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter Property="Opacity" Value="0.5" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style x:Key="Progress" TargetType="ProgressBar">
        <Setter Property="Height" Value="6" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ProgressBar">
              <Grid>
                <Border x:Name="PART_Track" CornerRadius="3" Background="{DynamicResource TrackBrush}" />
                <Border x:Name="PART_Indicator" CornerRadius="3" Background="{DynamicResource AccentBrush}" HorizontalAlignment="Left" />
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
    </ResourceDictionary>
    """;
}
