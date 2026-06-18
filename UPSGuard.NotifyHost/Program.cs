using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UPSGuard.NotifyHost
{
	static class Program
	{
		private const string Source = "Program";

		private static string PipeName => "UPS_GUARD_PIPE_MACHINE";

		private const string COMMAND_BATT_ON = "BATT_ON";
		private const string COMMAND_BATT_OFF = "BATT_OFF";

        private const int HIBERNATE_DELAY_MS = 45_000;
        private const float BATTERY_THRESHOLD = 0.30f;
		private const int BATTERY_POLL_MS = 10_000;

		private static System.Threading.Timer? _hibernateTimer;
		private static System.Threading.Timer? _batteryPollTimer;

		private static readonly object _lock = new object();

		private static Control? _ui;
		private static Mutex? _singleInstanceMutex;
		private static NotifyIcon? _tray;
		private static NotifyForm? _toast;

		private static volatile bool _exiting;
		private static volatile bool _onBattery;

		private static CancellationTokenSource? _cts;
		private static Task? _serverTask;

		private static NamedPipeServerStream? _currentServer;

		private static int _shutdownOnce;
		private static int _hibernateRequestedOnce;
		private static int _pipeConnectionCounter;
		private static int _toastCounter;

        private const string MSG_BATT_ON =
            "ИБП переведён на питание от аккумулятора.\n" +
            "Если питание не восстановится в течение 45 секунд,\n" +
            "компьютер будет переведён в спящий режим (гибернацию).";

        private const string MSG_BATT_OFF =
			"Питание восстановлено.\n" +
			"Переход в спящий режим отменён.";

        private const string MSG_HIBERNATING =
            "Хост переведен в спящий режим.";

        [STAThread]
        static void Main()
        {
            try
            {
                RunApp();
            }
            catch (Exception ex)
            {
                WriteFatalFallback(ex);
            }
        }

        private static void RunApp()
        {
            AppLogger.Init("UPSGuard.NotifyHost");

            AppLogger.Event(Source, "ProcessStart",
                $"Executable={Application.ExecutablePath} | " +
                $"BaseDir={AppDomain.CurrentDomain.BaseDirectory} | " +
                $"ArgsCount={Environment.GetCommandLineArgs().Length} | " +
                $"SessionId={GetCurrentSessionIdSafe()}");

            bool createdNew = false;

            try
            {
                int sessionId = GetCurrentSessionIdSafe();
                string mutexName = $@"Local\UPS_GUARD_NOTIFYHOST_{sessionId}";

                _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);

                AppLogger.State(Source, "SingleInstanceMutexCreated", createdNew.ToString(),
                    $"MutexName={mutexName}");
            }
            catch (Exception ex)
            {
                AppLogger.Error(Source, "Failed to create single instance mutex", ex);
                return;
            }

            if (!createdNew)
            {
                AppLogger.Warn(Source, "Another instance already running in this session. Current process will exit.");

                try
                {
                    _singleInstanceMutex?.Dispose();
                    _singleInstanceMutex = null;
                }
                catch
                {
                }

                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppLogger.Event(Source, "WinFormsInitialized");

                _cts = new CancellationTokenSource();
                AppLogger.State(Source, "CTS", "Created");

                Application.ApplicationExit += (_, __) =>
                {
                    AppLogger.Event(Source, "ApplicationExitEvent");
                    ShutdownOnce("ApplicationExit");
                };

                _ui = new Control();
                _ui.CreateControl();

                AppLogger.State(Source, "UIControl", "Created",
                    $"IsHandleCreated={_ui.IsHandleCreated}");

                _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
                AppLogger.State(Source, "ServerTask", "Started");

                AppLogger.Event(Source, "ApplicationRun", "Entering message loop");
                Application.Run(new TrayAppContext());
                AppLogger.Event(Source, "ApplicationRunReturned", "Message loop finished");
            }
            catch (Exception ex)
            {
                AppLogger.Error(Source, "Fatal error in RunApp()", ex);
            }
            finally
            {
                AppLogger.Event(Source, "MainFinally");
                ShutdownOnce("MainFinally");
            }
        }

        private static int GetCurrentSessionIdSafe()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.SessionId;
            }
            catch
            {
                return -1;
            }
        }

        private static void WriteFatalFallback(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "UPSGuard", "Fatal");
                Directory.CreateDirectory(dir);

                var path = Path.Combine(
                    dir,
                    $"UPSGuard.NotifyHost_FATAL_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");

                File.AppendAllText(
                    path,
                    DateTime.Now + Environment.NewLine + ex + Environment.NewLine);
            }
            catch
            {
            }
        }

        private sealed class TrayAppContext : ApplicationContext
		{
			private const string TraySource = "TrayAppContext";

			public TrayAppContext()
			{
				AppLogger.Event(TraySource, "CtorStart");

				_tray = new NotifyIcon
				{
					Visible = true,
					Text = "UPSGuard NotifyHost",
					Icon = LoadTrayIcon()
				};

				AppLogger.State(TraySource, "TrayIcon", "Created",
					$"Visible={_tray.Visible} | Text={_tray.Text}");

				var menu = new ContextMenuStrip();
				menu.Items.Add("О программе…", null, (_, __) =>
				{
					AppLogger.Event(TraySource, "MenuClickAbout");
					ShowAbout();
				});

				menu.Items.Add(new ToolStripSeparator());

				menu.Items.Add("Выход", null, (_, __) =>
				{
					AppLogger.Event(TraySource, "MenuClickExit");
					ExitApp();
				});

				_tray.ContextMenuStrip = menu;
				_tray.DoubleClick += (_, __) =>
				{
					AppLogger.Event(TraySource, "TrayDoubleClick");
					ShowAbout();
				};

				try
				{
					_tray.ShowBalloonTip(
						1500,
						"UPSGuard",
						"NotifyHost запущен и ждёт команды от сервиса.",
						ToolTipIcon.Info);

					AppLogger.Event(TraySource, "BalloonShown");
				}
				catch (Exception ex)
				{
					AppLogger.Error(TraySource, "Failed to show startup balloon", ex);
				}

				AppLogger.Event(TraySource, "CtorEnd");
			}

			private static void ExitApp()
			{
				AppLogger.Event(TraySource, "ExitAppRequested");
				ShutdownOnce("TrayExit");
				Application.Exit();
			}
		}

		private static void ShutdownOnce(string reason)
		{
			if (Interlocked.Exchange(ref _shutdownOnce, 1) != 0)
			{
				AppLogger.Debug(Source, $"ShutdownOnce skipped | Reason={reason} | already executed");
				return;
			}

			AppLogger.Event(Source, "ShutdownBegin", $"Reason={reason}");

			try
			{
				_exiting = true;
				AppLogger.State(Source, "Exiting", _exiting.ToString());

				try
				{
					_cts?.Cancel();
					AppLogger.Event(Source, "CTS.Cancel");
				}
				catch (Exception ex)
				{
					AppLogger.Error(Source, "CTS.Cancel failed", ex);
				}

				try
				{
					var s = Interlocked.Exchange(ref _currentServer, null);
					if (s != null)
					{
						AppLogger.Event(Source, "DisposeCurrentPipeServer");
						s.Dispose();
					}
				}
				catch (Exception ex)
				{
					AppLogger.Error(Source, "Dispose current server failed", ex);
				}

				lock (_lock)
				{
					CancelAll_NoLock("Shutdown");
				}

				SafeUiInvoke(() =>
				{
					AppLogger.Event(Source, "ShutdownUiSectionBegin");

					try
					{
						if (_toast != null)
						{
							AppLogger.State(Source, "Toast", "CloseDispose");
							_toast.Close();
							_toast.Dispose();
							_toast = null;
						}
					}
					catch (Exception ex)
					{
						AppLogger.Error(Source, "Toast cleanup failed", ex);
					}

					try
					{
						if (_tray != null)
						{
							_tray.Visible = false;
							_tray.Dispose();
							_tray = null;
							AppLogger.State(Source, "Tray", "Disposed");
						}
					}
					catch (Exception ex)
					{
						AppLogger.Error(Source, "Tray cleanup failed", ex);
					}

					AppLogger.Event(Source, "ShutdownUiSectionEnd");
				});

				try
				{
					_singleInstanceMutex?.ReleaseMutex();
					AppLogger.Event(Source, "MutexReleased");
				}
				catch (Exception ex)
				{
					AppLogger.Error(Source, "Mutex release failed", ex);
				}

				try
				{
					_singleInstanceMutex?.Dispose();
					_singleInstanceMutex = null;
					AppLogger.State(Source, "Mutex", "Disposed");
				}
				catch (Exception ex)
				{
					AppLogger.Error(Source, "Mutex dispose failed", ex);
				}
			}
			catch (Exception ex)
			{
				AppLogger.Error(Source, "ShutdownOnce fatal error", ex);
			}
			finally
			{
				AppLogger.Event(Source, "ShutdownEnd", $"Reason={reason}");
			}
		}

		private static void CancelAll_NoLock(string reason)
		{
			AppLogger.Event(Source, "CancelAll_NoLock", $"Reason={reason}");

			if (_hibernateTimer != null)
			{
				_hibernateTimer.Dispose();
				_hibernateTimer = null;
				AppLogger.State(Source, "HibernateTimer", "Disposed");
			}

			if (_batteryPollTimer != null)
			{
				_batteryPollTimer.Dispose();
				_batteryPollTimer = null;
				AppLogger.State(Source, "BatteryPollTimer", "Disposed");
			}
		}

		private static Icon LoadTrayIcon()
		{
			const string traySource = "LoadTrayIcon";

			try
			{
				var icoPath = Path.Combine(
					AppDomain.CurrentDomain.BaseDirectory,
					"Assets",
					"icon.ico"
				);

				AppLogger.Debug(traySource, $"TryLoadFromFile | Path={icoPath}");

				if (File.Exists(icoPath))
				{
					AppLogger.Info(traySource, $"Tray icon loaded from file | Path={icoPath}");
					return new Icon(icoPath);
				}

				var fallback = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
							   ?? SystemIcons.Application;

				AppLogger.Warn(traySource, "Tray icon file not found. Using fallback icon.");
				return fallback;
			}
			catch (Exception ex)
			{
				AppLogger.Error(traySource, "LoadTrayIcon failed. Using SystemIcons.Application", ex);
				return SystemIcons.Application;
			}
		}

        private static void ShowAbout()
        {
            const string aboutSource = "ShowAbout";

            try
            {
                AppLogger.Event(aboutSource, "AboutFormShow",
                    "Product=UPSGuard | Version=4.2.1");

                SafeUiInvoke(() =>
                {
                    using var form = new AboutForm(
                        productName: "UPSGuard",
                        versionText: "4.2.1",
                        companyLogoFileName: "logo+text_whitemdpi.png");

                    form.ShowDialog();
                });

                AppLogger.Event(aboutSource, "AboutFormClosed");
            }
            catch (Exception ex)
            {
                AppLogger.Error(aboutSource, "ShowAbout failed", ex);
            }
        }

        private static bool IsDebugBuild()
		{
#if DEBUG
            return true;
#else
			return false;
#endif
		}

		private static async Task ServerLoopAsync(CancellationToken ct)
		{
			const string serverSource = "ServerLoop";

			AppLogger.Event(serverSource, "LoopStart", $"PipeName={PipeName}");

			while (!ct.IsCancellationRequested && !_exiting)
			{
				NamedPipeServerStream? server = null;
				int connId = 0;

				try
				{
					server = CreateServer();
					Interlocked.Exchange(ref _currentServer, server);

					connId = Interlocked.Increment(ref _pipeConnectionCounter);

					AppLogger.Event(serverSource, "PipeServerCreated",
						$"ConnId={connId} | PipeName={PipeName}");

					AppLogger.Event(serverSource, "WaitForConnectionBegin",
						$"ConnId={connId}");

					await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

					AppLogger.Event(serverSource, "WaitForConnectionEnd",
						$"ConnId={connId} | IsConnected={server.IsConnected}");

					if (ct.IsCancellationRequested || _exiting)
					{
						AppLogger.Warn(serverSource,
							$"Loop break after connection | ConnId={connId} | " +
							$"CancellationRequested={ct.IsCancellationRequested} | Exiting={_exiting}");
						break;
					}

					using var reader = new StreamReader(server);
					string? cmd = await reader.ReadLineAsync().ConfigureAwait(false);

					AppLogger.Event(serverSource, "CommandReceived",
						$"ConnId={connId} | RawCommand={(cmd ?? "<null>")}");

					if (string.IsNullOrWhiteSpace(cmd))
					{
						AppLogger.Warn(serverSource, $"Empty command received | ConnId={connId}");
					}
					else
					{
						var normalized = cmd.Trim().ToUpperInvariant();
						AppLogger.State(serverSource, "NormalizedCommand", normalized,
							$"ConnId={connId}");

						switch (normalized)
						{
							case COMMAND_BATT_ON:
								AppLogger.Event(serverSource, "DispatchCommand",
									$"ConnId={connId} | Command={COMMAND_BATT_ON}");
								BeginHibernateCountdown();
								break;

							case COMMAND_BATT_OFF:
								AppLogger.Event(serverSource, "DispatchCommand",
									$"ConnId={connId} | Command={COMMAND_BATT_OFF}");
								CancelHibernate();
								break;

							default:
								AppLogger.Warn(serverSource,
									$"Unknown command | ConnId={connId} | Command={normalized}");
								break;
						}
					}
				}
				catch (OperationCanceledException)
				{
					AppLogger.Warn(serverSource, "OperationCanceledException. Loop will stop.");
					break;
				}
				catch (IOException ioex)
				{
					if (_exiting || ct.IsCancellationRequested)
					{
						AppLogger.Warn(serverSource,
							$"IO exception during shutdown | Message={ioex.Message}");
						break;
					}

					AppLogger.Error(serverSource, "IO error in pipe loop. Retry in 2s.", ioex);
					await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					if (_exiting || ct.IsCancellationRequested)
					{
						AppLogger.Warn(serverSource,
							$"Exception during shutdown | Type={ex.GetType().Name} | Message={ex.Message}");
						break;
					}

					AppLogger.Error(serverSource, "Fatal error in server loop. Retry in 2s.", ex);
					await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
				}
				finally
				{
					try
					{
						var cur = Interlocked.Exchange(ref _currentServer, null);

						if (cur != null && !ReferenceEquals(cur, server))
						{
							server?.Dispose();
						}
						else
						{
							server?.Dispose();
						}

						AppLogger.Event(serverSource, "PipeServerDisposed",
							$"ConnId={connId}");
					}
					catch (Exception ex)
					{
						AppLogger.Error(serverSource, "Pipe server dispose failed", ex);
					}
				}
			}

			AppLogger.Event(serverSource, "LoopExit",
				$"CancellationRequested={ct.IsCancellationRequested} | Exiting={_exiting}");
		}

		private static NamedPipeServerStream CreateServer()
		{
			AppLogger.Debug(Source, $"CreateServer | PipeName={PipeName}");

			return new NamedPipeServerStream(
				PipeName,
				PipeDirection.In,
				1,
				PipeTransmissionMode.Message,
				PipeOptions.Asynchronous,
				0,
				0);
		}

		private static void BeginHibernateCountdown()
		{
			const string hSource = "HibernateFlow";

			AppLogger.Event(hSource, "BeginHibernateCountdownCalled",
				$"OnBattery={_onBattery}");

			lock (_lock)
			{
				if (_onBattery)
				{
					AppLogger.Warn(hSource, "BATT_ON ignored because already on battery.");
					return;
				}

				_onBattery = true;
				AppLogger.State(hSource, "OnBattery", _onBattery.ToString());

				Interlocked.Exchange(ref _hibernateRequestedOnce, 0);
				AppLogger.State(hSource, "HibernateRequestedOnce", "ResetTo0");

				_hibernateTimer?.Dispose();
				AppLogger.State(hSource, "HibernateTimer", "DisposedPreviousInstance");

				_hibernateTimer = new System.Threading.Timer(_ =>
				{
					AppLogger.Event(hSource, "HibernateTimerCallbackBegin");

					try
					{
						AppLogger.Warn(hSource,
							$"Hibernate countdown elapsed | DelayMs={HIBERNATE_DELAY_MS}");
						DoHibernateOnce();
					}
					catch (Exception ex)
					{
						AppLogger.Error(hSource, "Hibernate timer callback error", ex);
					}
					finally
					{
						lock (_lock)
						{
							CancelAll_NoLock("HibernateTimerElapsed");
							_onBattery = false;
							AppLogger.State(hSource, "OnBattery", _onBattery.ToString(),
								"Reset after timer callback");
						}
					}

					AppLogger.Event(hSource, "HibernateTimerCallbackEnd");
				}, null, HIBERNATE_DELAY_MS, Timeout.Infinite);

				AppLogger.State(hSource, "HibernateTimer", "Started",
					$"DelayMs={HIBERNATE_DELAY_MS}");

				StartBatteryMonitoring_NoLock();
			}

			ShowToastAsync(MSG_BATT_ON, timeoutMs: 9000, isGood: false);
		}

		private static void CancelHibernate()
		{
			const string hSource = "HibernateFlow";

			AppLogger.Event(hSource, "CancelHibernateCalled",
				$"OnBattery={_onBattery}");

			bool hadAny = false;

			lock (_lock)
			{
				_onBattery = false;
				AppLogger.State(hSource, "OnBattery", _onBattery.ToString());

				if (_hibernateTimer != null)
				{
					_hibernateTimer.Dispose();
					_hibernateTimer = null;
					hadAny = true;
					AppLogger.State(hSource, "HibernateTimer", "Disposed");
				}

				if (_batteryPollTimer != null)
				{
					_batteryPollTimer.Dispose();
					_batteryPollTimer = null;
					hadAny = true;
					AppLogger.State(hSource, "BatteryPollTimer", "Disposed");
				}
			}

			if (hadAny)
			{
				AppLogger.Info(hSource, "Hibernate sequence cancelled because power restored.");
				ShowToastAsync(MSG_BATT_OFF, timeoutMs: 9000, isGood: true);
			}
			else
			{
				AppLogger.Warn(hSource, "CancelHibernate called but no active timers found.");
			}
		}

		private static void StartBatteryMonitoring_NoLock()
		{
			const string batterySource = "BatteryMonitor";

			_batteryPollTimer?.Dispose();
			AppLogger.State(batterySource, "BatteryPollTimer", "DisposedPreviousInstance");

			_batteryPollTimer = new System.Threading.Timer(_ =>
			{
				try
				{
					var pct = GetBatteryPercent();

					if (pct < 0f)
					{
						AppLogger.Warn(batterySource, "Battery percent unavailable.");
						return;
					}

					AppLogger.Info(batterySource,
						$"Battery percent polled | Percent={pct:P2} | Threshold={BATTERY_THRESHOLD:P0}");

					if (pct < BATTERY_THRESHOLD)
					{
						AppLogger.Warn(batterySource,
							$"Battery below threshold | Percent={pct:P2} | Threshold={BATTERY_THRESHOLD:P0}");

						lock (_lock)
						{
							CancelAll_NoLock("BatteryBelowThreshold");
							_onBattery = false;
							AppLogger.State(batterySource, "OnBattery", _onBattery.ToString(),
								"Reset because threshold reached");
						}

						DoHibernateOnce();
					}
				}
				catch (Exception ex)
				{
					AppLogger.Error(batterySource, "Battery monitoring callback error", ex);
				}
			}, null, dueTime: 0, period: BATTERY_POLL_MS);

			AppLogger.State(batterySource, "BatteryPollTimer", "Started",
				$"PeriodMs={BATTERY_POLL_MS} | Threshold={BATTERY_THRESHOLD:P0}");
		}

		private static float GetBatteryPercent()
		{
			const string batterySource = "GetBatteryPercent";

			try
			{
				float pct = SystemInformation.PowerStatus.BatteryLifePercent;
				AppLogger.Debug(batterySource, $"BatteryLifePercent={pct}");
				return pct;
			}
			catch (Exception ex)
			{
				AppLogger.Error(batterySource, "Failed to query battery percent", ex);
				return -1f;
			}
		}

		private static void ShowToastAsync(string message, int timeoutMs, bool isGood)
		{
			AppLogger.Event(Source, "ShowToastAsyncRequested",
				$"IsGood={isGood} | TimeoutMs={timeoutMs} | MessageLength={message?.Length ?? 0}");

			SafeUiInvoke(() => ShowToast(message, timeoutMs, isGood));
		}

		private static void SafeUiInvoke(Action a)
		{
			const string uiSource = "SafeUiInvoke";

			try
			{
				if (_ui != null && _ui.IsHandleCreated)
				{
					AppLogger.Debug(uiSource,
						$"InvokePath | InvokeRequired={_ui.InvokeRequired}");

					if (_ui.InvokeRequired)
						_ui.BeginInvoke(a);
					else
						a();
				}
				else
				{
					AppLogger.Warn(uiSource,
						"_ui is null or handle not created. Executing action directly.");
					a();
				}
			}
			catch (Exception ex)
			{
				AppLogger.Error(uiSource, "Invoke failed. Fallback to direct call.", ex);

				try
				{
					a();
				}
				catch (Exception ex2)
				{
					AppLogger.Error(uiSource, "Direct fallback action failed", ex2);
				}
			}
		}

		private static void ShowToast(string message, int timeoutMs, bool isGood)
		{
			const string toastSource = "Toast";

			try
			{
				int toastId = Interlocked.Increment(ref _toastCounter);

				AppLogger.Event(toastSource, "ShowToastBegin",
					$"ToastId={toastId} | IsGood={isGood} | TimeoutMs={timeoutMs}");

				if (_toast == null || _toast.IsDisposed)
				{
					AppLogger.State(toastSource, "NotifyForm", "CreateNew");
					_toast = new NotifyForm(isGood);

					_toast.FormClosed += (_, __) =>
					{
						AppLogger.Event(toastSource, "NotifyFormClosed");
						try
						{
							_toast?.Dispose();
						}
						catch (Exception ex)
						{
							AppLogger.Error(toastSource, "NotifyForm dispose in FormClosed failed", ex);
						}
						_toast = null;
					};
				}
				else
				{
					AppLogger.State(toastSource, "NotifyForm", "ReuseExisting");
				}

				_toast.ShowToast(message, isGood, timeoutMs);

				AppLogger.Event(toastSource, "ShowToastEnd",
					$"ToastId={toastId} | Message={message.Replace(Environment.NewLine, " ")}");
			}
			catch (Exception ex)
			{
				AppLogger.Error(toastSource, "ShowToast failed", ex);
			}
		}

		private static void DoHibernateOnce()
		{
			const string hSource = "HibernateFlow";

			int previous = Interlocked.Exchange(ref _hibernateRequestedOnce, 1);
			AppLogger.State(hSource, "HibernateRequestedOnceBefore", previous.ToString());

			if (previous != 0)
			{
				AppLogger.Warn(hSource, "DoHibernateOnce skipped because hibernate already requested.");
				return;
			}

			AppLogger.Event(hSource, "DoHibernateOnceProceed");

			ShowToastAsync(MSG_HIBERNATING, timeoutMs: 8000, isGood: false);
			AppLogger.Event(hSource, "HibernateNotificationShown");

			Thread.Sleep(2000);

			DoHibernate();
		}

		private static void DoHibernate()
		{
			const string hSource = "DoHibernate";

			AppLogger.Warn(hSource, "Hibernate requested: running powercfg /h on");

			try
			{
				var p1 = Process.Start("powercfg", "/h on");
				AppLogger.Info(hSource,
					$"powercfg started | Started={(p1 != null)} | PID={(p1?.Id.ToString() ?? "null")}");
			}
			catch (Exception ex)
			{
				AppLogger.Error(hSource, "powercfg /h on failed", ex);
			}

			AppLogger.Warn(hSource, "Running shutdown /h");

			try
			{
				var p2 = Process.Start("shutdown", "/h");
				AppLogger.Info(hSource,
					$"shutdown started | Started={(p2 != null)} | PID={(p2?.Id.ToString() ?? "null")}");
			}
			catch (Exception ex)
			{
				AppLogger.Error(hSource, "shutdown /h failed", ex);
			}
		}
	}
}