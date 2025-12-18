namespace OrganizerTool;

using System.IO;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	protected override void OnStartup(System.Windows.StartupEventArgs e)
	{
		RegisterGlobalExceptionHandlers();

		try
		{
			base.OnStartup(e);

			var mainWindow = new MainWindow();
			mainWindow.Show();
		}
		catch (Exception ex)
		{
			ReportFatal(ex, "Startup failed");
			Shutdown(-1);
		}
	}

	private void RegisterGlobalExceptionHandlers()
	{
		DispatcherUnhandledException += (_, args) =>
		{
			ReportFatal(args.Exception, "DispatcherUnhandledException");
			args.Handled = true;
		};

		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			if (args.ExceptionObject is Exception ex)
			{
				ReportFatal(ex, "AppDomain.UnhandledException");
			}
			else
			{
				ReportFatal(new Exception(args.ExceptionObject?.ToString() ?? "Unknown exception"), "AppDomain.UnhandledException");
			}
		};

		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			ReportFatal(args.Exception, "TaskScheduler.UnobservedTaskException");
			args.SetObserved();
		};
	}

	private static void ReportFatal(Exception ex, string title)
	{
		try
		{
			var logPath = GetCrashLogPath();
			Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
			File.AppendAllText(
				logPath,
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");

			System.Windows.MessageBox.Show(
				$"{title}\n\n{ex.Message}\n\n詳細は以下に出力しました:\n{logPath}",
				"エラー",
				System.Windows.MessageBoxButton.OK,
				System.Windows.MessageBoxImage.Error);
		}
		catch
		{
			// 何もできない場合は握る
		}
	}

	private static string GetCrashLogPath()
	{
		var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(baseDir, "MinecraftModFolderOrganizer", "crash-log.txt");
	}
}

