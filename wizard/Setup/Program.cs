using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SetupTests")]

namespace CrayonSetup
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            using (var mutex = new Mutex(true, @"Local\CrayonSetup", out bool first))
            {
                if (!first) { MessageBox.Show("Crayon Setup is already open.", "Crayon"); return 1; }
                try
                {
                    var app = new Application();
                    var ui = new Wizard();
                    if (args.Length == 2 && args[0] == "--preview")
                    {
                        // Render the real WPF window without installing or opening another app.
                        ui.Window.Show(); ui.Window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)ui.Window.ActualWidth, (int)ui.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(ui.Window);
                        using (var output = File.Create(args[1])) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
                        ui.Window.Close(); return 0;
                    }
                    app.Run(ui.Window); return 0;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Crayon Setup", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
        }
    }
    internal sealed class Wizard
    {
        public Window Window { get; }
        SystemHost host;
        string rhino;
        bool busy, done;
        T Find<T>(string name) where T : class => Window.FindName(name) as T;
        public Wizard()
        {
            using (var stream = SystemHost.Resource("Window.xaml")) Window = (Window)XamlReader.Load(stream);
            string release = SystemHost.ResourceText("package-info.txt").Split('\n')[0].Trim();
            string versionLabel = "v" + (release.EndsWith(".0", StringComparison.Ordinal) && !release.Contains("-")
                ? release.Substring(0, release.Length - 2) : release);
            Window.Title = "Crayon Setup " + versionLabel;
            Find<TextBlock>("SetupVersion").Text = "SETUP  " + versionLabel;
            using (var stream = SystemHost.Resource("logo.png"))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                Find<Image>("Logo").Source = image; Window.Icon = image;
            }
            using (var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                if (theme?.GetValue("AppsUseLightTheme") is int light && light == 0)
                {
                    SetBrush("Canvas", "#1F2228"); SetBrush("Card", "#292D35"); SetBrush("Ink", "#F3F4F6"); SetBrush("Muted", "#ADB2BD"); SetBrush("Line", "#3D424D");
                }
            Find<Button>("Primary").Click += (s,e) => InstallOrFinish();
            Find<Button>("Secondary").Click += (s,e) => Window.Close();
            Find<Button>("Locate").Click += (s,e) =>
            {
                var dialog = new OpenFileDialog { Title = "Select Rhino 8", Filter = "Rhino|Rhino.exe" };
                if (dialog.ShowDialog(Window) == true)
                {
                    if (SystemHost.ValidRhino(dialog.FileName)) { rhino = dialog.FileName; Ready(); }
                    else ShowError("Choose Rhino.exe from a Rhino 8 installation. Rhino 7 and Rhino 9 are not supported by this installer.");
                }
            };
            Window.Closing += (s,e) => { if (busy) { e.Cancel = true; return; } host?.Dispose(); };
            rhino = SystemHost.FindRhino(); Ready();
        }
        void SetBrush(string name, string color) => Window.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        void Ready()
        {
            bool found = rhino != null;
            Find<TextBlock>("RhinoStatus").Text = found ? "Rhino 8 found" : "Rhino 8 is required";
            Find<TextBlock>("RhinoStatus").ToolTip = rhino;
            Find<Button>("Primary").IsEnabled = found;
            Find<Button>("Locate").Visibility = found ? Visibility.Collapsed : Visibility.Visible;
            if (!found) ShowError("Install and activate Rhino 8 first, then return to setup. If it is already installed, select it below.");
            else Find<TextBlock>("Error").Visibility = Visibility.Collapsed;
        }
        void ShowError(string text) { Find<TextBlock>("Error").Text = text; Find<TextBlock>("Error").Visibility = Visibility.Visible; }
        async void InstallOrFinish()
        {
            if (busy) return;
            if (done)
            {
                if (Find<CheckBox>("LaunchRhino").IsChecked == true)
                {
                    try { Process.Start(new ProcessStartInfo(rhino) { UseShellExecute = true }); }
                    catch (Exception ex) { ShowError("Crayon is installed. Open Rhino from your Start menu. " + ex.Message); return; }
                }
                Window.Close(); return;
            }
            busy = true;
            Find<Button>("Primary").IsEnabled = Find<Button>("Secondary").IsEnabled = false;
            Find<TextBlock>("Error").Visibility = Visibility.Collapsed;
            Find<TextBlock>("Heading").Text = "Making room for ideas.";
            Find<TextBlock>("Description").Text = "We're setting up Crayon for you.";
            Find<Border>("Summary").Visibility = Visibility.Collapsed;
            Find<TextBlock>("Note").Visibility = Visibility.Collapsed;
            Find<StackPanel>("ProgressArea").Visibility = Visibility.Visible;
            try
            {
                if (host == null) host = new SystemHost(rhino);
                await new Installer(host).Install(new SetupOptions(), (value, text) =>
                {
                    Find<ProgressBar>("Progress").Value = value;
                    Find<TextBlock>("ProgressText").Text = text;
                }, CancellationToken.None);
                done = true;
                Find<TextBlock>("Heading").Text = "You're ready to create.";
                Find<TextBlock>("Description").Text = "Crayon will be waiting in Rhino's sidebar. Open Rhino and start sketching.";
                Find<StackPanel>("ProgressArea").Visibility = Visibility.Collapsed;
                Find<CheckBox>("LaunchRhino").Visibility = Visibility.Visible;
                Find<Button>("Primary").Content = "Finish";
                Find<Button>("Secondary").Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Find<TextBlock>("Heading").Text = "Let's finish setting up.";
                Find<TextBlock>("Description").Text = "Completed components are kept. Retry will pick up from here.";
                Find<TextBlock>("ProgressText").Text = "Setup needs your attention";
                Find<Button>("Primary").Content = "Retry";
                ShowError(ex is OperationCanceledException ? "The download or installer took too long. Check your connection and retry." : ex.Message);
            }
            finally { busy = false; Find<Button>("Primary").IsEnabled = Find<Button>("Secondary").IsEnabled = true; }
        }
    }
}
