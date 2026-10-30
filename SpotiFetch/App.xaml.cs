using System.IO;
using System.Text;
using System.Windows;

namespace LaurusDownloader;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            WriteCrash(eventArgs.Exception);
            MessageBox.Show(
                $"O SpotiFetch encontrou um erro e não pode continuar.\n\n{eventArgs.Exception.Message}\n\nO diagnóstico foi salvo em:\n{CrashLogPath}",
                "Erro ao iniciar o SpotiFetch", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception) WriteCrash(exception);
            else WriteCrash(new Exception(eventArgs.ExceptionObject?.ToString() ?? "Erro desconhecido."));
        };
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpotiFetch", "crash.log");

    private static void WriteCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n{new string('-', 72)}\n",
                Encoding.UTF8);
        }
        catch { }
    }
}
