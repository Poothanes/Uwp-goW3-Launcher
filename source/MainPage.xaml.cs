using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace GoW3Installer
{
    public sealed partial class MainPage : Page
    {
        CancellationTokenSource activeCancel;
        WifiReceiver activeWifi;
        GoW3Settings settings;
        bool initializingUi = true;
        readonly StringBuilder logBuffer = new StringBuilder();

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            settings = GoW3Settings.Load();
            ApplySettingsToUi();
            initializingUi = false;
            AppendLog("UWP GoW3 Launcher iniciado.");
            AppendLog("Carpeta de destino en Xbox: " + GameStorage.ImportedGamePath);
            await RefreshStatusAsync();
        }

        void ApplySettingsToUi()
        {
            SelectComboByContent(ResolutionCombo, settings.Resolution);
            SelectComboByContent(FpsCombo, settings.FrameRatePatch);
            SelectComboByTag(GlyphCombo, settings.GlyphStyle);
            SelectComboByTag(DmemCombo, settings.DirectMemoryMb.ToString());
            SkipIntroCheck.IsChecked = settings.SkipIntroLogos;
            NoMotionBlurCheck.IsChecked = settings.DisableMotionBlur;
            NoDofCheck.IsChecked = settings.DisableDepthOfField;
        }

        void ReadSettingsFromUi()
        {
            if (initializingUi || settings == null) return;
            if (ResolutionCombo.SelectedItem is ComboBoxItem resItem)
                settings.Resolution = resItem.Content?.ToString() ?? "1920x1080 (Native)";
            if (FpsCombo.SelectedItem is ComboBoxItem fpsItem)
            {
                settings.FrameRatePatch = fpsItem.Content?.ToString() ?? "60 FPS++";
                settings.TargetFps = settings.FrameRatePatch.Contains("120") ? 120 : 60;
            }
            if (GlyphCombo.SelectedItem is ComboBoxItem glyphItem && glyphItem.Tag != null)
                settings.GlyphStyle = glyphItem.Tag.ToString();
            if (DmemCombo.SelectedItem is ComboBoxItem dmemItem && dmemItem.Tag != null && int.TryParse(dmemItem.Tag.ToString(), out int dmem))
                settings.DirectMemoryMb = dmem;

            settings.SkipIntroLogos = SkipIntroCheck.IsChecked == true;
            settings.DisableMotionBlur = NoMotionBlurCheck.IsChecked == true;
            settings.DisableDepthOfField = NoDofCheck.IsChecked == true;
            try
            {
                if (Directory.Exists(GameStorage.ImportedGamePath))
                    settings.Save();
            }
            catch { }
        }

        void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            ReadSettingsFromUi();
        }

        static void SelectComboByContent(ComboBox combo, string text)
        {
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        async Task RefreshStatusAsync()
        {
            bool imported = GamePreparation.IsImported();
            bool prepared = GamePreparation.IsPrepared();
            long freeBytes = await GetFreeBytesAsync();

            StorageInfoText.Text = $"Ruta Xbox: {GameStorage.ImportedGamePath}  |  Espacio libre: {FormatBytes(freeBytes)}";

            if (prepared)
            {
                var sfo = GamePreparation.ReadImportedSfo();
                TitleIdBadge.Text = $"{sfo.TitleId}  v{sfo.AppVer}";
                StateBadgeText.Text = "Listo para Jugar (Preparado)";
                OperationTitleText.Text = $"{sfo.Title} ({sfo.TitleId}) preparado y listo.";
            }
            else if (imported)
            {
                var sfo = GamePreparation.ReadImportedSfo();
                TitleIdBadge.Text = $"{sfo.TitleId}  v{sfo.AppVer}";
                StateBadgeText.Text = "Importado (Falta Preparar)";
                OperationTitleText.Text = "Archivos importados. Pulsa 'Preparar y Vincular Juego'.";
            }
            else
            {
                TitleIdBadge.Text = "CUSA01715 / CUSA01623";
                StateBadgeText.Text = "Pendiente de importar";
                OperationTitleText.Text = "Transfiere tu carpeta CUSA01715 por Wi-Fi o USB para comenzar.";
            }

            bool busy = activeCancel != null || activeWifi != null;
            PlayButton.IsEnabled = !busy && (prepared || imported);
            PrepareButton.IsEnabled = !busy && imported;
            VerifyButton.IsEnabled = !busy && imported;
            WifiButton.IsEnabled = !busy;
            ImportFolderButton.IsEnabled = !busy;
            ImportZipButton.IsEnabled = !busy;
            DeleteButton.IsEnabled = !busy && Directory.Exists(GameStorage.ImportedGamePath);
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        async void PlayGame(object sender, RoutedEventArgs e)
        {
            ReadSettingsFromUi();
            if (!GamePreparation.IsImported())
            {
                AppendLog("Primero transfiere la carpeta CUSA01715 por Wi-Fi o USB.");
                return;
            }
            if (!GamePreparation.IsPrepared())
            {
                AppendLog("El juego está importado pero aún no preparado. Ejecutando preparación en Xbox...");
                bool ok = await RunPrepareInternalAsync();
                if (!ok) return;
            }
            try
            {
                AppendLog("Iniciando God of War III Remastered en Xbox...");
                bool launched = await CombinedRuntime.LaunchAsync(settings);
                if (!launched)
                    AppendLog("Advertencia: Xbox rechazó la activación por protocolo uwp-gow3-combined-game:play.");
            }
            catch (Exception ex)
            {
                AppendLog("Error al iniciar el juego: " + ex.Message);
            }
        }

        async void PrepareGame(object sender, RoutedEventArgs e)
        {
            ReadSettingsFromUi();
            await RunPrepareInternalAsync();
        }

        async Task<bool> RunPrepareInternalAsync()
        {
            if (activeCancel != null) return false;
            activeCancel = new CancellationTokenSource();
            var token = activeCancel.Token;
            await RefreshStatusAsync();
            ProgressBar.IsIndeterminate = true;
            OperationTitleText.Text = "Preparando God of War III Remastered en Xbox...";
            try
            {
                await Task.Run(() =>
                {
                    GamePreparation.Prepare(settings, msg =>
                    {
                        var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            OperationTitleText.Text = msg;
                            AppendLog(msg);
                        });
                    }, token);
                }, token);
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                AppendLog("¡Preparación completada con éxito! boot-linked.bin, patches.bin, content.bin y GoW3StartupModule.dll generados.");
                return true;
            }
            catch (Exception ex)
            {
                ProgressBar.IsIndeterminate = false;
                AppendLog("Error en preparación: " + ex.Message);
                return false;
            }
            finally
            {
                activeCancel.Dispose();
                activeCancel = null;
                await RefreshStatusAsync();
            }
        }

        async void StartWifiTransfer(object sender, RoutedEventArgs e)
        {
            if (activeWifi != null) return;
            try
            {
                if (Directory.Exists(GameStorage.ImportedGamePath) && Directory.GetFileSystemEntries(GameStorage.ImportedGamePath).Length > 0)
                {
                    AppendLog("Nota: Eliminando importación incompleta previa antes de iniciar Wi-Fi...");
                    Directory.Delete(GameStorage.ImportedGamePath, true);
                }
                activeWifi = new WifiReceiver(
                    GameStorage.ImportedGamePath,
                    GetFreeBytesAsync,
                    (done, total, file) =>
                    {
                        var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            double pct = total > 0 ? (done * 100.0 / total) : 0;
                            ProgressBar.IsIndeterminate = false;
                            ProgressBar.Value = pct;
                            ProgressPercentText.Text = $"{pct:0.0}%";
                            OperationTitleText.Text = $"Recibiendo {file} ({FormatBytes(done)} / {FormatBytes(total)})";
                        });
                    });

                WifiUrlText.Text = activeWifi.Url;
                WifiCodeText.Text = activeWifi.Code;
                await RenderQrCodeAsync(activeWifi.Url);
                WifiPanel.Visibility = Visibility.Visible;
                AppendLog($"Servidor Wi-Fi iniciado en {activeWifi.Url} (Código: {activeWifi.Code})");
                await RefreshStatusAsync();

                await activeWifi.Completion;
                AppendLog("¡Transferencia Wi-Fi completada y verificada! Iniciando preparación automática...");
                WifiPanel.Visibility = Visibility.Collapsed;
                activeWifi.Dispose();
                activeWifi = null;
                await RefreshStatusAsync();
                await RunPrepareInternalAsync();
            }
            catch (TaskCanceledException)
            {
                AppendLog("Transferencia Wi-Fi cancelada.");
            }
            catch (Exception ex)
            {
                AppendLog("Error en transferencia Wi-Fi: " + ex.Message);
            }
            finally
            {
                WifiPanel.Visibility = Visibility.Collapsed;
                if (activeWifi != null)
                {
                    activeWifi.Dispose();
                    activeWifi = null;
                }
                await RefreshStatusAsync();
            }
        }

        async Task RenderQrCodeAsync(string url)
        {
            try
            {
                using (var gen = new QRCodeGenerator())
                using (var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
                using (var png = new PngByteQRCode(data))
                {
                    byte[] bytes = png.GetGraphic(6);
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(bytes))
                    {
                        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    }
                    WifiQrImage.Source = bmp;
                }
            }
            catch { }
        }

        async void ImportFolder(object sender, RoutedEventArgs e)
        {
            if (activeCancel != null) return;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            activeCancel = new CancellationTokenSource();
            var token = activeCancel.Token;
            await RefreshStatusAsync();
            try
            {
                AppendLog("Escaneando carpeta seleccionada: " + folder.Path);
                var manifest = await FolderImportEngine.ScanPs4FolderAsync(folder, msg =>
                {
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OperationTitleText.Text = msg);
                });
                long free = await GetFreeBytesAsync();
                var src = new UsbFolderSource(folder);
                await FolderImportEngine.ImportAsync(src, GameStorage.ImportedGamePath, manifest, free, (done, total, file) =>
                {
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        double pct = total > 0 ? (done * 100.0 / total) : 0;
                        ProgressBar.Value = pct;
                        ProgressPercentText.Text = $"{pct:0.0}%";
                        OperationTitleText.Text = $"Copiando {file} ({FormatBytes(done)} / {FormatBytes(total)})";
                    });
                }, token);
                AppendLog("Carpeta USB importada y verificada correctamente.");
            }
            catch (Exception ex)
            {
                AppendLog("Error al importar carpeta: " + ex.Message);
            }
            finally
            {
                activeCancel.Dispose();
                activeCancel = null;
                await RefreshStatusAsync();
            }
        }

        async void ImportZip(object sender, RoutedEventArgs e)
        {
            if (activeCancel != null) return;
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".zip");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            activeCancel = new CancellationTokenSource();
            var token = activeCancel.Token;
            await RefreshStatusAsync();
            try
            {
                AppendLog("Importando archivo ZIP: " + file.Name);
                long free = await GetFreeBytesAsync();
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    await Task.Run(() => ImportEngine.ImportZip(stream, GameStorage.ImportedGamePath, free, (done, total, name) =>
                    {
                        var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            double pct = total > 0 ? (done * 100.0 / total) : 0;
                            ProgressBar.Value = pct;
                            ProgressPercentText.Text = $"{pct:0.0}%";
                            OperationTitleText.Text = $"Extrayendo {name} ({FormatBytes(done)} / {FormatBytes(total)})";
                        });
                    }, token), token);
                }
                AppendLog("Archivo ZIP importado y verificado correctamente.");
            }
            catch (Exception ex)
            {
                AppendLog("Error al importar ZIP: " + ex.Message);
            }
            finally
            {
                activeCancel.Dispose();
                activeCancel = null;
                await RefreshStatusAsync();
            }
        }

        async void VerifyGame(object sender, RoutedEventArgs e)
        {
            if (activeCancel != null) return;
            activeCancel = new CancellationTokenSource();
            var token = activeCancel.Token;
            await RefreshStatusAsync();
            try
            {
                AppendLog("Verificando integridad SHA-256 de los archivos importados...");
                await Task.Run(() => GamePreparation.VerifyImportedFiles((done, total, status) =>
                {
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        double pct = total > 0 ? (done * 100.0 / total) : 0;
                        ProgressBar.Value = pct;
                        ProgressPercentText.Text = $"{pct:0.0}%";
                        OperationTitleText.Text = status;
                    });
                }, token), token);
                AppendLog("¡Todos los archivos importados coinciden con su hash SHA-256!");
            }
            catch (Exception ex)
            {
                AppendLog("Error en verificación: " + ex.Message);
            }
            finally
            {
                activeCancel.Dispose();
                activeCancel = null;
                await RefreshStatusAsync();
            }
        }

        void CancelActiveOperation(object sender, RoutedEventArgs e)
        {
            activeCancel?.Cancel();
            if (activeWifi != null)
            {
                activeWifi.Dispose();
                activeWifi = null;
                WifiPanel.Visibility = Visibility.Collapsed;
            }
            AppendLog("Cancelación solicitada por el usuario.");
        }

        async void DeleteImportedFiles(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(GameStorage.ImportedGamePath))
                {
                    AppendLog("Eliminando archivos importados en " + GameStorage.ImportedGamePath + "...");
                    await Task.Run(() => Directory.Delete(GameStorage.ImportedGamePath, true));
                    ProgressBar.Value = 0;
                    ProgressPercentText.Text = "";
                    AppendLog("Archivos eliminados.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Error al eliminar archivos: " + ex.Message);
            }
            await RefreshStatusAsync();
        }

        async Task<long> GetFreeBytesAsync()
        {
            try
            {
                Directory.CreateDirectory(GameStorage.Root);
                var folder = await StorageFolder.GetFolderFromPathAsync(GameStorage.Root);
                var props = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                if (props.TryGetValue("System.FreeSpace", out object val) && val is ulong free)
                    return (long)Math.Min(free, (ulong)long.MaxValue);
            }
            catch { }
            return 60L * 1024 * 1024 * 1024;
        }

        void AppendLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            logBuffer.AppendLine(line);
            LogText.Text = logBuffer.ToString();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        }

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
