using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.ReactiveUI;
using Carina.PixelViewer.Threading;
using Carina.PixelViewer.ViewModels;
using NLog;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Carina.PixelViewer
{
	/// <summary>
	/// Application.
	/// </summary>
	class App : Application, IThreadDependent
	{
		// Static fields.
		static readonly ILogger Logger = LogManager.GetCurrentClassLogger();


		// Fields.
		bool isRestartMainWindowRequested;
		MainWindow? mainWindow;
		ResourceInclude? stringResources;
		ResourceInclude? stringResourcesLinux;
		StyleInclude? stylesDark;
		StyleInclude? stylesLight;
		volatile SynchronizationContext? syncContext;
		Workspace? workspace;


		// Avalonia configuration, don't remove; also used by visual designer.
		static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.UseReactiveUI()
			.LogToTrace();


		/// <summary>
		/// Get current <see cref="CultureInfo"/>.
		/// </summary>
		public CultureInfo CultureInfo { get; private set; } = CultureInfo.CurrentCulture;


		/// <summary>
		/// Get <see cref="App"/> instance for current process.
		/// </summary>
		public static new App Current
		{
			get => (App)Application.Current;
		}


		/// <summary>
		/// Path of directory of application.
		/// </summary>
		public string Directory { get; } = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? throw new Exception("Unable to get application directory.");


		/// <summary>
		/// Get string for current locale.
		/// </summary>
		/// <param name="key">Key of string.</param>
		/// <param name="defaultValue">Default value.</param>
		/// <returns>String or default value.</returns>
		public string? GetString(string key, string? defaultValue = null)
		{
			if (this.Resources.TryGetResource($"String.{key}", out var value) && value is string str)
				return str;
			return defaultValue;
		}


		/// <summary>
		/// Get non-null string for current locale.
		/// </summary>
		/// <param name="key">Key of string.</param>
		/// <param name="defaultValue">Default value.</param>
		/// <returns>String or default value.</returns>
		public string GetStringNonNull(string key, string defaultValue = "") => this.GetString(key) ?? defaultValue;


		// Initialize.
		public override void Initialize()
		{
			// setup global exception handler
			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			{
				var exceptionObj = e.ExceptionObject;
				if (exceptionObj is Exception exception)
					Logger.Fatal(exception, "***** Unhandled application exception *****");
				else
					Logger.Fatal($"***** Unhandled application exception ***** {exceptionObj}");
			};

			// load XAML
			AvaloniaXamlLoader.Load(this);

			// attach to settings
			this.Settings.PropertyChanged += (_, e) => this.OnSettingsChanged(e.PropertyName);

			// load strings
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				this.Resources.MergedDictionaries.Add(new ResourceInclude()
				{
					Source = new Uri($"avares://PixelViewer/Strings/Default-Linux.xaml")
				});
			}
			this.UpdateStringResources();
		}


		// Application entry point.
		[STAThread]
		public static void Main(string[] args)
		{
			Logger.Info("Start");

			// start application
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

			// dispose workspace
			App.Current.workspace?.Dispose();
		}


		// Called when framework initialization completed.
		public override void OnFrameworkInitializationCompleted()
		{
			this.syncContext = SynchronizationContext.Current;
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
				this.workspace = new Workspace().Also((it) =>
				{
					// create first session
					it.ActivateSession(it.CreateSession());
				});
				this.SynchronizationContext.Post(this.ShowMainWindow);
			}
			base.OnFrameworkInitializationCompleted();
		}


		// Called when main window closed.
		void OnMainWindowClosed()
		{
			Logger.Warn("Main window closed");

			// detach from main window
			this.mainWindow = this.mainWindow?.Let((it) =>
			{
				it.DataContext = null;
				return (MainWindow?)null;
			});

			// save settings
			this.Settings.Save();

			// restart main window
			if(this.isRestartMainWindowRequested)
			{
				Logger.Warn("Restart main window");
				this.isRestartMainWindowRequested = false;
				this.SynchronizationContext.Post(this.ShowMainWindow);
				return;
			}

			// shut down application
			if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
			{
				Logger.Warn("Shut down");
				desktopLifetime.Shutdown();
			}
		}


		// Called when settings changed.
		void OnSettingsChanged(string propertyName)
		{
			switch (propertyName)
			{
				case nameof(Settings.AutoSelectLanguage):
					this.UpdateStringResources();
					break;
			}
		}


		/// <summary>
		/// Restart main window.
		/// </summary>
		public void RestartMainWindow()
		{
			this.VerifyAccess();
			if (this.isRestartMainWindowRequested)
				return;
			if (this.mainWindow != null)
			{
				Logger.Warn("Request restarting main window");
				this.isRestartMainWindowRequested = true;
				this.mainWindow.Close();
			}
			else
			{
				Logger.Warn("No main window to restart, show directly");
				this.ShowMainWindow();
			}
		}


		/// <summary>
		/// Get application settings.
		/// </summary>
		public Settings Settings { get; } = Settings.Default;


		// Create and show main window.
		void ShowMainWindow()
		{
			// check state
			if (this.mainWindow != null)
			{
				Logger.Error("Already shown main window");
				return;
			}

			// update styles
			this.UpdateStyles();

			// show main window
			this.mainWindow = new MainWindow().Also((it) =>
			{
				it.DataContext = this.workspace;
				it.Closed += (_, e) => this.OnMainWindowClosed();
			});
			Logger.Warn("Show main window");
			this.mainWindow.Show();
		}


		/// <summary>
		/// Synchronization context.
		/// </summary>
		public SynchronizationContext SynchronizationContext { get => this.syncContext ?? throw new InvalidOperationException("Application is not ready yet."); }


		// Update string resource according to settings.
		void UpdateStringResources()
		{
			if (this.Settings.AutoSelectLanguage)
			{
				// base resources
				var localeName = this.CultureInfo.Name;
				if (this.stringResources == null)
				{
					try
					{
						this.stringResources = new ResourceInclude()
						{
							Source = new Uri($"avares://PixelViewer/Strings/{localeName}.xaml")
						};
						_ = this.stringResources.Loaded; // trigger error if resource not found
						Logger.Info($"Load strings for {localeName}.");
					}
					catch
					{
						this.stringResources = null;
						Logger.Warn($"No strings for {localeName}.");
						return;
					}
					this.Resources.MergedDictionaries.Add(this.stringResources);
				}
				else if (!this.Resources.MergedDictionaries.Contains(this.stringResources))
					this.Resources.MergedDictionaries.Add(this.stringResources);

				// resources for specific OS
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				{
					if (this.stringResourcesLinux == null)
					{
						try
						{
							this.stringResourcesLinux = new ResourceInclude()
							{
								Source = new Uri($"avares://PixelViewer/Strings/{localeName}-Linux.xaml")
							};
							_ = this.stringResourcesLinux.Loaded; // trigger error if resource not found
							Logger.Info($"Load strings (Linux) for {localeName}.");
						}
						catch
						{
							this.stringResourcesLinux = null;
							Logger.Warn($"No strings (Linux) for {localeName}.");
							return;
						}
						this.Resources.MergedDictionaries.Add(this.stringResourcesLinux);
					}
					else if (!this.Resources.MergedDictionaries.Contains(this.stringResourcesLinux))
						this.Resources.MergedDictionaries.Add(this.stringResourcesLinux);
				}
			}
			else
			{
				if (this.stringResources != null)
					this.Resources.MergedDictionaries.Remove(this.stringResources);
				if (this.stringResourcesLinux != null)
					this.Resources.MergedDictionaries.Remove(this.stringResourcesLinux);
			}
		}


		// Update styles according to settings.
		void UpdateStyles()
		{
			// select style
			var addingStyle = this.Settings.DarkMode switch
			{
				true => this.stylesDark ?? new StyleInclude(new Uri("avares://PixelViewer/")).Also((it) =>
				{
					it.Source = new Uri("avares://PixelViewer/Styles/Dark.xaml");
					this.stylesDark = it;
				}),
				_ => this.stylesLight ?? new StyleInclude(new Uri("avares://PixelViewer/")).Also((it) =>
				{
					it.Source = new Uri("avares://PixelViewer/Styles/Light.xaml");
					this.stylesLight = it;
				}),
			};
			var removingStyle = this.Settings.DarkMode switch
			{
				true => this.stylesLight,
				_ => this.stylesDark,
			};

			// update style
			if (removingStyle != null)
				this.Styles.Remove(removingStyle);
			if (!this.Styles.Contains(addingStyle))
				this.Styles.Add(addingStyle);
		}
	}
}
