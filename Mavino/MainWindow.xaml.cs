using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Mavino.Models;
using Mavino.Services;

namespace Mavino
{
    public partial class MainWindow : Window
    {
        private readonly AudioService _audio = new();
        private readonly DispatcherTimer _timer = new();
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _volumeSaveTimer = new();
        private List<LrcLine> _lyrics = new();
        private List<LrcLine> _syncedLyrics = new();
        private List<LrcLine> _staticLyrics = new();
        private int _currentLyricIndex = -1;
        private bool _isDraggingProgress;
        private AppSettings _settings = new();
        private readonly ObservableCollection<LyricViewModel> _lyricVMs = new();
        private readonly Random _rand = new();
        private double[] _barHeights = new double[32];
        private double[] _barTargets = new double[32];
        private string? _currentLyricsTrackPath;
        private bool _liveMode = true; // Живой by default
        private List<LyricsResult> _allSources = new();
        private int _activeSourceIndex = 0;

        // ===== ПЛАВНОСТЬ ПРОКРУТКИ ТЕКСТА =====
        // Чем больше число — тем медленнее и плавнее (рекомендуется 800–2000)
        private const double LyricsScrollDurationMs = 1200;
        private const double LyricsScrollLerp = 0.08;

        private double _scrollTarget = 0;
        private bool _scrollAnimating = false;
        private readonly DispatcherTimer _scrollTimer = new();
        private bool _showPlaylist = false;
        private Forms.NotifyIcon? _trayIcon;
        private bool _reallyClose = false;
        private readonly ObservableCollection<PlaylistItemVm> _playlistItems = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
            KeyDown += MainWindow_KeyDown;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsService.Load();

            var vol = Math.Clamp(_settings.Volume, 0.0, 1.0);
            VolumeSlider.Value = vol;
            _audio.Volume = vol;
            UpdateVolumeText();

            _audio.Shuffle = _settings.Shuffle;
            _audio.RepeatMode = _settings.RepeatMode;
            UpdateShuffleUI();
            UpdateRepeatUI();
            UpdateModeButtons();


            _audio.TrackChanged += Audio_TrackChanged;
            _audio.PlaybackStopped += Audio_PlaybackStopped;

            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            UpdateClock();

            _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(250);
            _volumeSaveTimer.Tick += (s, ev) =>
            {
                _volumeSaveTimer.Stop();
                SaveVolumeNow();
            };

            for (int i = 0; i < 32; i++)
            {
                _barHeights[i] = 4;
                _barTargets[i] = 4;
            }
            CompositionTarget.Rendering += Visualizer_Rendering;

            // Плавная прокрутка текста (~60 FPS)
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _scrollTimer.Tick += ScrollTimer_Tick;

            InitTrayIcon();
        }

        private void InitTrayIcon()
        {
            try
            {
                _trayIcon = new Forms.NotifyIcon
                {
                    Visible = false,
                    Text = "Mavino"
                };

                // Icon from embedded resource or exe
                try
                {
                    var uri = new Uri("pack://application:,,,/Assets/mavino.ico");
                    var streamInfo = System.Windows.Application.GetResourceStream(uri);
                    if (streamInfo != null)
                        _trayIcon.Icon = new Drawing.Icon(streamInfo.Stream);
                    else
                        _trayIcon.Icon = Drawing.SystemIcons.Application;
                }
                catch
                {
                    _trayIcon.Icon = Drawing.SystemIcons.Application;
                }

                _trayIcon.DoubleClick += (_, _) => ShowFromTray();

                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
                menu.Items.Add("Выход", null, (_, _) =>
                {
                    _reallyClose = true;
                    _trayIcon.Visible = false;
                    Close();
                });
                _trayIcon.ContextMenuStrip = menu;
            }
            catch { }
        }

        private void Tray_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void HideToTray()
        {
            if (_trayIcon != null)
                _trayIcon.Visible = true;
            Hide();
            ShowInTaskbar = false;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Activate();
            if (_trayIcon != null)
                _trayIcon.Visible = false;
        }

        private void SaveVolumeNow()
        {
            try
            {
                _settings.Volume = VolumeSlider.Value;
                SettingsService.Save(_settings);
            }
            catch { }
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!_reallyClose && _trayIcon != null)
            {
                // Optional: could hide to tray on X — currently X closes. Keep close.
            }

            SaveVolumeNow();
            _settings.Shuffle = _audio.Shuffle;
            _settings.RepeatMode = _audio.RepeatMode;
            SettingsService.Save(_settings);
            _audio.Dispose();
            CompositionTarget.Rendering -= Visualizer_Rendering;

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                PlayPause_Click(sender, e);
                e.Handled = true;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) Maximize_Click(sender, e);
            else DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void PlaylistToggle_Click(object sender, RoutedEventArgs e)
        {
            _showPlaylist = true;
            UpdateSidePanel();
            RefreshPlaylistUi();
        }

        private void LyricsToggle_Click(object sender, RoutedEventArgs e)
        {
            _showPlaylist = false;
            UpdateSidePanel();
        }

        private void UpdateSidePanel()
        {
            if (_showPlaylist)
            {
                LyricsPanel.Visibility = Visibility.Collapsed;
                PlaylistPanel.Visibility = Visibility.Visible;
                PlaylistToggleBtn.Visibility = Visibility.Collapsed;
                LyricsToggleBtn.Visibility = Visibility.Visible;
            }
            else
            {
                LyricsPanel.Visibility = Visibility.Visible;
                PlaylistPanel.Visibility = Visibility.Collapsed;
                PlaylistToggleBtn.Visibility = Visibility.Visible;
                LyricsToggleBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshPlaylistUi()
        {
            _playlistItems.Clear();
            var list = _audio.Playlist;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                _playlistItems.Add(new PlaylistItemVm
                {
                    Index = i,
                    Number = (i + 1).ToString(),
                    Title = t.Title,
                    Artist = t.Artist,
                    Duration = FormatTime(t.Duration),
                    IsCurrent = i == GetCurrentIndexSafe(),
                    Cover = CreateCoverImage(t.AlbumArtData)
                });
            }
            PlaylistItemsControl.ItemsSource = null;
            PlaylistItemsControl.ItemsSource = _playlistItems;
            PlaylistCountText.Text = $"  ·  {list.Count} треков";
        }

        private static ImageSource? CreateCoverImage(byte[]? data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private int GetCurrentIndexSafe()
        {
            var current = _audio.CurrentTrack;
            if (current == null) return -1;
            for (int i = 0; i < _audio.Playlist.Count; i++)
                if (ReferenceEquals(_audio.Playlist[i], current) || _audio.Playlist[i].FilePath == current.FilePath)
                    return i;
            return -1;
        }

        private void PlaylistItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int index)
            {
                _audio.Play(index);
                UpdatePlayPauseUI(true);
                RefreshPlaylistUi();
            }
        }

        private void ModeStatic_Click(object sender, RoutedEventArgs e)
        {
            _liveMode = false;
            UpdateModeButtons();
            ApplyCurrentLyricsMode();
        }

        private void ModeLive_Click(object sender, RoutedEventArgs e)
        {
            _liveMode = true;
            UpdateModeButtons();
            ApplyCurrentLyricsMode();
        }

        private void UpdateModeButtons()
        {
            ModeStaticBtn.Style = (Style)FindResource(_liveMode ? "ModeChip" : "ModeChipActive");
            ModeLiveBtn.Style = (Style)FindResource(_liveMode ? "ModeChipActive" : "ModeChip");
        }

        private void ApplyCurrentLyricsMode()
        {
            _lyricVMs.Clear();
            _currentLyricIndex = -1;

            var source = _liveMode ? _syncedLyrics : (_staticLyrics.Count > 0 ? _staticLyrics : _syncedLyrics);
            _lyrics = source;

            foreach (var line in source)
            {
                _lyricVMs.Add(new LyricViewModel
                {
                    Text = line.Text,
                    IsActive = false,
                    Timestamp = line.Timestamp,
                    ForceBright = !_liveMode
                });
            }

            LyricsItemsControl.ItemsSource = null;
            LyricsItemsControl.ItemsSource = _lyricVMs;

            if (source.Count > 0)
            {
                NoLyricsPlaceholder.Visibility = Visibility.Collapsed;
                LyricsStatusText.Text = _liveMode
                    ? $"  ·  {source.Count} строк · Живой"
                    : $"  ·  {source.Count} строк · Статичный";
            }
        }

        private void OpenFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio files|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac|All files|*.*",
                Title = "Open audio files"
            };
            if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                dlg.InitialDirectory = _settings.LastFolder;

            if (dlg.ShowDialog() == true)
            {
                _settings.LastFolder = Path.GetDirectoryName(dlg.FileNames[0]);
                SettingsService.Save(_settings);

                bool wasEmpty = _audio.Playlist.Count == 0;
                _audio.LoadFiles(dlg.FileNames);
                if (wasEmpty && _audio.Playlist.Count > 0)
                {
                    _audio.Play(0);
                    UpdatePlayPauseUI(true);
                }
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Select music folder" };
            if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                dlg.InitialDirectory = _settings.LastFolder;

            if (dlg.ShowDialog() == true)
            {
                var folder = dlg.FolderName;
                _settings.LastFolder = folder;
                SettingsService.Save(_settings);

                var files = Directory.GetFiles(folder)
                    .Where(f => IsAudio(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (files.Length == 0)
                {
                    MessageBox.Show("В этой папке нет аудиофайлов.", "Mavino", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _audio.ClearPlaylist();
                _audio.LoadFiles(files);
                _audio.Play(0);
                UpdatePlayPauseUI(true);
            }
        }

        private static bool IsAudio(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac";
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_audio.Playlist.Count == 0)
            {
                OpenFolder_Click(sender, e);
                return;
            }

            if (_audio.IsPlaying)
            {
                _audio.Pause();
                UpdatePlayPauseUI(false);
            }
            else
            {
                if (_audio.CurrentTrack == null) _audio.Play(0);
                else _audio.Resume();
                UpdatePlayPauseUI(true);
            }
        }

        private void Previous_Click(object sender, RoutedEventArgs e) => _audio.Previous();
        private void Next_Click(object sender, RoutedEventArgs e) => _audio.Next();

        private void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            _audio.Shuffle = !_audio.Shuffle;
            _settings.Shuffle = _audio.Shuffle;
            SettingsService.Save(_settings);
            UpdateShuffleUI();
        }

        private void Repeat_Click(object sender, RoutedEventArgs e)
        {
            _audio.RepeatMode = _audio.RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };
            _settings.RepeatMode = _audio.RepeatMode;
            SettingsService.Save(_settings);
            UpdateRepeatUI();
        }

        private void Favorite_Click(object sender, RoutedEventArgs e)
        {
            var track = _audio.CurrentTrack;
            if (track == null) return;
            track.IsFavorite = !track.IsFavorite;
            FavoriteIcon.Text = track.IsFavorite ? "♥" : "♡";
            FavoriteIcon.Foreground = track.IsFavorite
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        private void UpdateShuffleUI()
        {
            ShuffleBtn.Foreground = _audio.Shuffle ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ShuffleBtn.Opacity = _audio.Shuffle ? 1.0 : 0.7;
        }

        private void UpdateRepeatUI()
        {
            switch (_audio.RepeatMode)
            {
                case RepeatMode.Off:
                    RepeatIcon.Text = "🔁";
                    RepeatBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                    RepeatBtn.Opacity = 0.7;
                    break;
                case RepeatMode.All:
                    RepeatIcon.Text = "🔁";
                    RepeatBtn.Foreground = Brushes.White;
                    RepeatBtn.Opacity = 1.0;
                    break;
                case RepeatMode.One:
                    RepeatIcon.Text = "🔂";
                    RepeatBtn.Foreground = Brushes.White;
                    RepeatBtn.Opacity = 1.0;
                    break;
            }
        }

        private void UpdatePlayPauseUI(bool playing)
        {
            PlayPauseIcon.Text = playing ? "⏸" : "▶";
            PlayPauseIcon.Margin = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        }

        private void Audio_TrackChanged(object? sender, Track track)
        {
            Dispatcher.Invoke(() =>
            {
                // Keep volume
                _audio.Volume = VolumeSlider.Value;

                TrackTitleText.Text = track.Title;
                TrackArtistText.Text = track.Artist;
                TotalTimeText.Text = FormatTime(track.Duration);
                FavoriteIcon.Text = track.IsFavorite ? "♥" : "♡";

                if (track.AlbumArtData != null && track.AlbumArtData.Length > 0)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = new MemoryStream(track.AlbumArtData);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        AlbumArtImage.Source = bmp;
                        NoArtOverlay.Visibility = Visibility.Collapsed;
                    }
                    catch
                    {
                        AlbumArtImage.Source = null;
                        NoArtOverlay.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    AlbumArtImage.Source = null;
                    NoArtOverlay.Visibility = Visibility.Visible;
                }

                _ = LoadLyricsAsync(track);
                UpdatePlayPauseUI(true);
                if (_showPlaylist) RefreshPlaylistUi();
            });
        }

        private void Audio_PlaybackStopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() => UpdatePlayPauseUI(false));
        }

        private async Task LoadLyricsAsync(Track track)
        {
            _currentLyricsTrackPath = track.FilePath;
            _lyrics.Clear();
            _syncedLyrics.Clear();
            _staticLyrics.Clear();
            _lyricVMs.Clear();
            _currentLyricIndex = -1;
            _allSources.Clear();
            _activeSourceIndex = 0;

            Dispatcher.Invoke(() =>
            {
                LyricsItemsControl.ItemsSource = null;
                SourceChipsPanel.Children.Clear();
                LyricsStatusText.Text = "  ·  Ищем текст...";
                NoLyricsPlaceholder.Visibility = Visibility.Collapsed;
            });

            var results = await LyricsService.GetAllSourcesAsync(track);
            if (_currentLyricsTrackPath != track.FilePath) return;

            _allSources = results.Where(r => r.Lines.Count > 0).ToList();

            // Prefer synced sources first
            _allSources = _allSources
                .OrderByDescending(r => r.IsSynced)
                .ThenByDescending(r => r.Lines.Count)
                .ToList();

            Dispatcher.Invoke(() =>
            {
                BuildSourceChips();

                if (_allSources.Count > 0)
                {
                    ApplySource(_allSources[0]);
                }
                else
                {
                    LyricsStatusText.Text = "  ·  Текст не найден";
                    NoLyricsPlaceholder.Visibility = Visibility.Visible;
                }
            });
        }

        private void BuildSourceChips()
        {
            SourceChipsPanel.Children.Clear();
            for (int i = 0; i < _allSources.Count; i++)
            {
                var src = _allSources[i];
                var btn = new Button
                {
                    Content = src.SourceName,
                    Tag = i,
                    Style = (Style)FindResource(i == _activeSourceIndex ? "ModeChipActive" : "ModeChip")
                };
                int captured = i;
                btn.Click += (s, e) =>
                {
                    _activeSourceIndex = captured;
                    ApplySource(_allSources[captured]);
                    BuildSourceChips();
                };
                SourceChipsPanel.Children.Add(btn);
            }
        }

        private void ApplySource(LyricsResult src)
        {
            _syncedLyrics = src.IsSynced ? src.Lines : new List<LrcLine>();
            _staticLyrics = src.Lines.Select(l => new LrcLine
            {
                Text = l.Text,
                Timestamp = TimeSpan.Zero
            }).ToList();

            if (!src.IsSynced)
                _liveMode = false;

            UpdateModeButtons();
            ApplyCurrentLyricsMode();
        }

        private void LyricLine_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_liveMode) return;
            if (sender is TextBlock tb && tb.Tag is LyricViewModel vm)
            {
                if (vm.Timestamp > TimeSpan.Zero || _lyrics.Any(l => l.Timestamp > TimeSpan.Zero))
                    _audio.Seek(vm.Timestamp);
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_audio.CurrentTrack == null || _isDraggingProgress) return;

            var pos = _audio.CurrentTime;
            var total = _audio.TotalTime;
            if (total.TotalSeconds > 0)
                ProgressSlider.Value = pos.TotalSeconds / total.TotalSeconds * 100;
            CurrentTimeText.Text = FormatTime(pos);

            // Live lyrics only
            if (_liveMode && _lyrics.Count > 0 && _lyrics.Any(l => l.Timestamp > TimeSpan.Zero))
            {
                int newIndex = -1;
                for (int i = _lyrics.Count - 1; i >= 0; i--)
                {
                    if (pos >= _lyrics[i].Timestamp)
                    {
                        newIndex = i;
                        break;
                    }
                }

                if (newIndex != _currentLyricIndex)
                {
                    if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricVMs.Count)
                        _lyricVMs[_currentLyricIndex].IsActive = false;

                    _currentLyricIndex = newIndex;

                    if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricVMs.Count)
                    {
                        _lyricVMs[_currentLyricIndex].IsActive = true;
                        SmoothScrollToCenter(_currentLyricIndex);
                    }
                }
            }

            // Visualizer
            if (_audio.IsPlaying)
            {
                for (int i = 0; i < 32; i++)
                {
                    if (_rand.NextDouble() < 0.28)
                        _barTargets[i] = 6 + _rand.NextDouble() * 52;
                    else
                        _barTargets[i] *= 0.9;
                }
            }
            else
            {
                for (int i = 0; i < 32; i++)
                    _barTargets[i] = 3;
            }
        }

        private void SmoothScrollToCenter(int index)
        {
            if (index < 0 || index >= _lyricVMs.Count) return;
            var sv = LyricsScrollViewer;
            if (sv.ActualHeight <= 0) return;

            double target;
            try
            {
                var container = LyricsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                if (container != null)
                {
                    var transform = container.TransformToAncestor(sv);
                    var pos = transform.Transform(new Point(0, 0));
                    double absoluteY = sv.VerticalOffset + pos.Y;
                    target = Math.Max(0, absoluteY - sv.ActualHeight / 2.0 + container.ActualHeight / 2.0);
                }
                else
                {
                    double lineH = 56;
                    target = Math.Max(0, index * lineH - sv.ActualHeight / 2.0 + 28);
                }
            }
            catch
            {
                double lineH = 56;
                target = Math.Max(0, index * lineH - sv.ActualHeight / 2.0 + 28);
            }

            _scrollTarget = target;
            _scrollAnimating = true;
            if (!_scrollTimer.IsEnabled)
                _scrollTimer.Start();
        }

        private void ScrollTimer_Tick(object? sender, EventArgs e)
        {
            var sv = LyricsScrollViewer;
            if (sv == null)
            {
                _scrollTimer.Stop();
                _scrollAnimating = false;
                return;
            }

            double current = sv.VerticalOffset;
            double diff = _scrollTarget - current;

            // Достигли цели
            if (Math.Abs(diff) < 0.5)
            {
                sv.ScrollToVerticalOffset(_scrollTarget);
                _scrollTimer.Stop();
                _scrollAnimating = false;
                return;
            }

            // Плавное догоняние (lerp). LyricsScrollLerp — главная «мягкость»
            // Дополнительно учитываем желаемую длительность
            double t = Math.Clamp(16.0 / LyricsScrollDurationMs * 8.0, 0.03, 0.25);
            double lerp = Math.Max(LyricsScrollLerp, t);
            double next = current + diff * lerp;
            sv.ScrollToVerticalOffset(next);
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isDraggingProgress = true;

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingProgress = false;
            if (_audio.TotalTime.TotalSeconds > 0)
            {
                var pos = TimeSpan.FromSeconds(ProgressSlider.Value / 100.0 * _audio.TotalTime.TotalSeconds);
                _audio.Seek(pos);
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingProgress && _audio.TotalTime.TotalSeconds > 0)
            {
                var pos = TimeSpan.FromSeconds(e.NewValue / 100.0 * _audio.TotalTime.TotalSeconds);
                CurrentTimeText.Text = FormatTime(pos);
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            _audio.Volume = e.NewValue;
            UpdateVolumeText();
            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }

        private void UpdateVolumeText()
        {
            if (VolumeText == null || VolumeSlider == null) return;
            VolumeText.Text = $"{(int)(VolumeSlider.Value * 100)}%";
        }

        private void ClockTimer_Tick(object? sender, EventArgs e) => UpdateClock();

        private void UpdateClock()
        {
            var now = DateTime.Now;
            ClockTimeText.Text = now.ToString("HH:mm");
            ClockDateText.Text = now.ToString("ddd • d MMM yyyy");
        }

        private void Visualizer_Rendering(object? sender, EventArgs e)
        {
            for (int i = 0; i < 32; i++)
                _barHeights[i] += (_barTargets[i] - _barHeights[i]) * 0.22;

            var canvas = VisualizerCanvas;
            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

            canvas.Children.Clear();
            double barWidth = (canvas.ActualWidth - 31 * 2) / 32.0;
            if (barWidth < 1) barWidth = 1;

            for (int i = 0; i < 32; i++)
            {
                double h = Math.Max(3, Math.Min(canvas.ActualHeight, _barHeights[i]));
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = barWidth,
                    Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                    RadiusX = 1.5,
                    RadiusY = 1.5
                };
                Canvas.SetLeft(rect, i * (barWidth + 2));
                Canvas.SetBottom(rect, 0);
                canvas.Children.Add(rect);
            }
        }

        private static string FormatTime(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }

    public static class ScrollViewerBehavior
    {
        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.RegisterAttached("VerticalOffset", typeof(double), typeof(ScrollViewerBehavior),
                new PropertyMetadata(0.0, OnVerticalOffsetChanged));

        public static double GetVerticalOffset(DependencyObject obj) => (double)obj.GetValue(VerticalOffsetProperty);
        public static void SetVerticalOffset(DependencyObject obj, double value) => obj.SetValue(VerticalOffsetProperty, value);

        private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
                sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    public class LyricViewModel : INotifyPropertyChanged
    {
        private bool _isActive;
        public string Text { get; set; } = "";
        public TimeSpan Timestamp { get; set; }
        public bool ForceBright { get; set; }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PlaylistItemVm
    {
        public int Index { get; set; }
        public string Number { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Duration { get; set; } = "";
        public bool IsCurrent { get; set; }
        public ImageSource? Cover { get; set; }
    }
}
